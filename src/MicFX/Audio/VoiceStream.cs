using System.Diagnostics;
using System.Numerics;

namespace MicFX.Audio;

/// <summary>Block timing of the AI voice stream, in samples at 48 kHz.</summary>
/// <param name="Block">Audio converted per model call.</param>
/// <param name="Crossfade">SOLA overlap: how long consecutive blocks are crossfaded.</param>
/// <param name="Search">SOLA search range: how far a block may be shifted to line up with the previous one.</param>
/// <param name="Allowance">Time the model gets per block, from the block's last input sample to its first output sample.</param>
public readonly record struct VoiceTiming(int Block, int Crossfade, int Search, int Allowance)
{
    /// <summary>Samples the model returns per call.</summary>
    public int Output => Block + Crossfade + Search;

    /// <summary>Fixed delay from input to output.</summary>
    public int Delay => Block + Crossfade + Search + Allowance;

    public static VoiceTiming FromMs(int blockMs, int allowanceMs, int crossfadeMs = 40, int searchMs = 10) =>
        new(blockMs * 48, crossfadeMs * 48, searchMs * 48, allowanceMs * 48);
}

/// <summary>A voice model, as seen by <see cref="VoiceStream"/>.</summary>
public interface IVoiceConverter
{
    /// <summary>48 kHz input samples wanted per call, ending at the newest sample.</summary>
    int ContextSamples { get; }

    /// <summary>
    /// Converts one block. Called on the worker thread only. <paramref name="context"/> holds the
    /// newest input (zeros before the stream started); write the converted audio for its last
    /// <c>output.Length</c> samples, time-aligned with them. <paramref name="continuous"/> is false
    /// when the previous block was not converted, so state carried between calls must be reset.
    /// </summary>
    void Convert(ReadOnlySpan<float> context, Span<float> output, bool continuous);
}

/// <summary>
/// Real-time streaming around a voice model that is too slow to run on the
/// audio thread. The audio thread writes input and plays output on one
/// timeline of absolute sample positions; a worker thread converts blocks.
///
/// Block k's input is [kB, (k+1)B). It plays at [(k+1)B + A, (k+2)B + A), so
/// the delay is fixed: B + crossfade + search + A. Whether block k plays is
/// decided once, when the audio thread reaches its start: if the worker has
/// published it, it plays; if not, the previous block's continuation fades
/// out over 5 ms and the block is silent. A block published late is never
/// played, so the delay can never grow.
///
/// After a miss the stream stays silent until enough converted blocks in a
/// row arrive with at least <see cref="MinHeadroom"/> to spare, and that hold
/// doubles when misses come back soon after recovering, so a GPU that only
/// just keeps up gives one drop-out rather than a stutter.
///
/// Consecutive blocks are joined as in RVC's real-time code (MIT): the
/// model returns a little more than a block; the start of each block is
/// shifted by up to the search range to line up with the end of the previous
/// one (normalised cross-correlation) and crossfaded into it with a sin² window.
///
/// Every decision is made in sample positions, never in wall-clock time, and
/// <see cref="TryStep"/> is the worker's whole job, so the logic can be driven
/// synchronously. Audio thread and worker share no locks: the audio thread
/// publishes (generation, position) in one long, the worker publishes each
/// block by writing its slot header last.
/// </summary>
public sealed class VoiceStream : IDisposable
{
    /// <summary>Fade used for drop-outs and recovery: 5 ms.</summary>
    public const int FadeSamples = 240;

    /// <summary>Spare time a converted block needs to count towards recovery: 10 ms.</summary>
    public const int MinHeadroom = 480;

    /// <summary>Input quieter than this (RMS, -60 dBFS) is not converted.</summary>
    public const float SilenceRms = 0.001f;

    private const int SlotCount = 8;
    private const int GenerationBits = 15;
    private const long PositionMask = (1L << 48) - 1;
    private const int FlapWindowSeconds = 10;
    private const int MaxHoldSeconds = 4;

    private enum BlockMode { Silent, Play, FadeOut }

    private readonly VoiceTiming timing;
    private readonly IVoiceConverter converter;
    private readonly int block, crossfade, search, allowance, context;
    private readonly float[] inputRing;
    private readonly int inputMask;
    private readonly float[][] slots; // block + fade-out continuation
    private readonly long[] slotHeader;
    private readonly long[] slotStamp;
    private readonly bool[] slotSilent;
    private readonly float[] solaFadeIn;
    private readonly float[] fade; // rising sin², FadeSamples long
    private readonly int baseHold, maxHold;
    private readonly AutoResetEvent wake = new(false);
    private readonly int[] recentHeadroom = new int[16];

    // Written by the audio thread: (generation << 48) | samples written.
    private long published;

    // Audio thread state.
    private int aGeneration;
    private long aPos;
    private long aDecided;
    private BlockMode aMode;
    private bool aFadeIn;
    private bool aLive;
    private bool aPrevPlayed;
    private int aGood;
    private int aHold;
    private long aLastLive;
    private int aHeadroomIndex;
    private long missed;

    // Worker state.
    private int wGeneration = -1;
    private long wNext;
    private bool wContinuous;
    private readonly float[] wContext;
    private readonly float[] wOut;
    private readonly float[] wTail;
    private volatile bool faulted;
    private volatile string? fault;
    private float lastComputeMs;

    private Thread? thread;
    private volatile bool stopping;

    public VoiceStream(VoiceTiming timing, IVoiceConverter converter)
    {
        if (timing.Block <= 0 || timing.Crossfade < FadeSamples || timing.Crossfade > timing.Block ||
            timing.Search < 0 || timing.Allowance <= 0 || timing.Allowance >= (SlotCount - 2) * timing.Block)
            throw new ArgumentException("Unsupported voice timing.", nameof(timing));
        this.timing = timing;
        this.converter = converter;
        block = timing.Block;
        crossfade = timing.Crossfade;
        search = timing.Search;
        allowance = timing.Allowance;
        context = Math.Max(converter.ContextSamples, timing.Output);

        int ring = 1;
        while (ring < context + 4 * block + allowance + 48000) ring <<= 1;
        inputRing = new float[ring];
        inputMask = ring - 1;

        slots = new float[SlotCount][];
        slotHeader = new long[SlotCount];
        slotStamp = new long[SlotCount];
        slotSilent = new bool[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = new float[block + FadeSamples];
            slotHeader[i] = -1;
        }

        solaFadeIn = new float[crossfade];
        for (int i = 0; i < crossfade; i++)
        {
            float s = MathF.Sin(0.5f * MathF.PI * i / (crossfade - 1));
            solaFadeIn[i] = s * s;
        }
        fade = new float[FadeSamples];
        for (int i = 0; i < FadeSamples; i++)
        {
            float s = MathF.Sin(0.5f * MathF.PI * i / (FadeSamples - 1));
            fade[i] = s * s;
        }

        baseHold = HoldBlocks(timing);
        maxHold = Math.Max(baseHold, (MaxHoldSeconds * 48000 + block - 1) / block);
        wContext = new float[context];
        wOut = new float[timing.Output];
        wTail = new float[crossfade];
        Array.Fill(recentHeadroom, int.MaxValue);
    }

    /// <summary>Converted blocks in a row needed to come back after a drop-out (about 400 ms).</summary>
    public static int HoldBlocks(VoiceTiming timing) => Math.Max(3, (19200 + timing.Block - 1) / timing.Block);

    public VoiceTiming Timing => timing;

    /// <summary>Blocks that were not ready in time while the voice was playing.</summary>
    public long MissedBlocks => Volatile.Read(ref missed);

    /// <summary>True while converted audio is being played (false after a drop-out until it recovers).</summary>
    public bool IsLive => Volatile.Read(ref aLive);

    /// <summary>Set when the model failed; the stream is silent from then on.</summary>
    public string? Fault => fault;

    /// <summary>Time the last model call took.</summary>
    public float LastComputeMs => Volatile.Read(ref lastComputeMs);

    /// <summary>Smallest spare time of the last 16 published blocks, in ms (time between publishing and playing).</summary>
    public float MinRecentHeadroomMs
    {
        get
        {
            int min = int.MaxValue;
            for (int i = 0; i < recentHeadroom.Length; i++) min = Math.Min(min, Volatile.Read(ref recentHeadroom[i]));
            return min == int.MaxValue ? float.NaN : min / 48f;
        }
    }

    // ---- Audio thread --------------------------------------------------------

    /// <summary>Starts a new stream at position 0 (audio thread). Nothing from before is ever played.</summary>
    public void BeginGeneration()
    {
        aGeneration = (aGeneration + 1) & ((1 << GenerationBits) - 1);
        aPos = 0;
        aDecided = -1;
        aMode = BlockMode.Silent;
        aFadeIn = false;
        aPrevPlayed = false;
        aGood = 0;
        aHold = baseHold;
        aLastLive = long.MinValue / 2;
        Volatile.Write(ref aLive, true);
        Volatile.Write(ref published, (long)aGeneration << 48);
    }

    /// <summary>
    /// Writes mono input and produces the same number of output samples (audio thread).
    /// No allocation, no locks; signals the worker when a block's input is complete.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        long p = aPos;
        int n = input.Length;
        for (int i = 0; i < n; i++) inputRing[(int)((p + i) & inputMask)] = input[i];
        // A new stream starts mid-voice; fade its input in so the model does not see a step.
        for (long i = p; i < FadeSamples && i < p + n; i++) inputRing[(int)i] *= fade[i];

        long firstStart = block + allowance;
        int done = 0;
        while (done < n)
        {
            long rel = p + done - firstStart;
            if (rel < 0)
            {
                int m = (int)Math.Min(n - done, -rel);
                output.Slice(done, m).Clear();
                done += m;
                continue;
            }
            long k = rel / block;
            int j = (int)(rel - k * block);
            if (k > aDecided) Decide(k, firstStart + k * block);
            int len = Math.Min(n - done, block - j);
            Render(k, j, output.Slice(done, len));
            done += len;
        }

        aPos = p + n;
        Volatile.Write(ref published, ((long)aGeneration << 48) | aPos);
        if (p / block != aPos / block && !faulted) wake.Set();
    }

    private void Decide(long k, long start)
    {
        aDecided = k;
        int s = (int)(k % SlotCount);
        bool ready = Volatile.Read(ref slotHeader[s]) == Pack(aGeneration, k);
        long headroom = ready ? start - slotStamp[s] : long.MinValue;
        bool silent = ready && slotSilent[s];
        if (ready)
        {
            recentHeadroom[aHeadroomIndex] = (int)Math.Clamp(headroom, int.MinValue + 1, int.MaxValue - 1);
            aHeadroomIndex = (aHeadroomIndex + 1) % recentHeadroom.Length;
        }

        if (aLive)
        {
            if (ready)
            {
                aMode = BlockMode.Play;
                aFadeIn = false;
                aPrevPlayed = true;
                return;
            }
            aMode = aPrevPlayed ? BlockMode.FadeOut : BlockMode.Silent;
            aPrevPlayed = false;
            Volatile.Write(ref aLive, false);
            Volatile.Write(ref missed, missed + 1);
            aGood = 0;
            aHold = start - aLastLive < FlapWindowSeconds * 48000L ? Math.Min(aHold * 2, maxHold) : baseHold;
            return;
        }

        // Recovering: only converted blocks with real headroom prove the model keeps up.
        // Silent blocks prove nothing either way.
        if (!ready) aGood = 0;
        else if (!silent) aGood = headroom >= MinHeadroom ? aGood + 1 : 0;

        if (ready && aGood >= aHold)
        {
            aMode = BlockMode.Play;
            aFadeIn = true;
            aPrevPlayed = true;
            aLastLive = start;
            Volatile.Write(ref aLive, true);
        }
        else
        {
            aMode = BlockMode.Silent;
            aPrevPlayed = false;
        }
    }

    private void Render(long k, int j, Span<float> dest)
    {
        switch (aMode)
        {
            case BlockMode.Play:
            {
                var src = slots[(int)(k % SlotCount)];
                src.AsSpan(j, dest.Length).CopyTo(dest);
                if (aFadeIn)
                    for (int i = 0; i < dest.Length && j + i < FadeSamples; i++) dest[i] *= fade[j + i];
                break;
            }
            case BlockMode.FadeOut:
            {
                // The previous block's slot holds its continuation past the block end.
                var prev = slots[(int)((k + SlotCount - 1) % SlotCount)];
                for (int i = 0; i < dest.Length; i++)
                {
                    int t = j + i;
                    dest[i] = t < FadeSamples ? prev[block + t] * (1f - fade[t]) : 0f;
                }
                break;
            }
            default:
                dest.Clear();
                break;
        }
    }

    // ---- Worker ----------------------------------------------------------------

    /// <summary>
    /// Does the worker's next piece of work: converts and publishes the next block
    /// whose deadline has not passed. Returns false when there is nothing to do.
    /// </summary>
    public bool TryStep()
    {
        if (faulted) return false;
        long state = Volatile.Read(ref published);
        int gen = (int)(state >> 48);
        long pos = state & PositionMask;
        if (gen != wGeneration)
        {
            wGeneration = gen;
            wNext = 0;
            ResetJoin();
            return true;
        }

        long complete = pos / block;
        if (wNext >= complete) return false;

        // Blocks whose start has already been reached are lost; skip to the first one that is not.
        long q = pos - block - allowance;
        long fresh = q < 0 ? 0 : q / block + 1;
        if (fresh > wNext)
        {
            wNext = fresh;
            ResetJoin();
            if (wNext >= complete) return true;
        }

        long k = wNext;
        long end = (k + 1) * block;
        CopyInput(end - context, wContext);
        long after = Volatile.Read(ref published);
        if ((int)(after >> 48) != gen) return true;
        if ((after & PositionMask) - (end - context) > inputRing.Length)
        {
            // The input was overwritten while copying: far too late anyway.
            wNext = k + 1;
            ResetJoin();
            return true;
        }

        var region = wContext.AsSpan(context - timing.Output);
        bool silent = Dot(region, region) < SilenceRms * SilenceRms * region.Length;
        if (silent)
        {
            Array.Clear(wOut);
        }
        else
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                converter.Convert(wContext, wOut, wContinuous);
            }
            catch (Exception ex)
            {
                fault = ex.Message;
                faulted = true;
                return false;
            }
            Volatile.Write(ref lastComputeMs, (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        wContinuous = !silent;

        Publish(k, gen, silent);
        wNext = k + 1;
        return true;
    }

    private void ResetJoin()
    {
        Array.Clear(wTail);
        wContinuous = false;
    }

    private void CopyInput(long from, float[] dest)
    {
        for (int i = 0; i < dest.Length; i++)
        {
            long p = from + i;
            dest[i] = p < 0 ? 0f : inputRing[(int)(p & inputMask)];
        }
    }

    private void Publish(long k, int gen, bool silent)
    {
        // SOLA: shift this block's start to line up with the previous block's continuation.
        int offset = 0;
        float best = float.NegativeInfinity;
        var tail = wTail.AsSpan();
        for (int o = 0; o <= search; o++)
        {
            var seg = wOut.AsSpan(o, crossfade);
            float score = Dot(seg, tail) / MathF.Sqrt(Dot(seg, seg) + 1e-8f);
            if (score > best)
            {
                best = score;
                offset = o;
            }
        }

        int s = (int)(k % SlotCount);
        Interlocked.Exchange(ref slotHeader[s], -1);
        var slot = slots[s];
        for (int i = 0; i < crossfade; i++)
            slot[i] = wOut[offset + i] * solaFadeIn[i] + wTail[i] * (1f - solaFadeIn[i]);
        Array.Copy(wOut, offset + crossfade, slot, crossfade, block - crossfade + FadeSamples);
        Array.Copy(wOut, offset + block, wTail, 0, crossfade);
        slotSilent[s] = silent;

        long now = Volatile.Read(ref published);
        if ((int)(now >> 48) != gen) return;
        slotStamp[s] = now & PositionMask;
        Volatile.Write(ref slotHeader[s], Pack(gen, k));
    }

    private static long Pack(int generation, long blockIndex) => ((long)generation << 48) | blockIndex;

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - w; i += w) acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // ---- Worker thread -----------------------------------------------------------

    /// <summary>Runs <see cref="TryStep"/> on a background thread, woken at each block boundary.</summary>
    public void StartWorker()
    {
        if (thread != null) return;
        thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "MicFX AI voice",
        };
        thread.Start();
    }

    private void WorkerLoop()
    {
        while (!stopping)
        {
            if (!TryStep()) wake.WaitOne(100);
        }
    }

    /// <summary>Stops the worker. Never blocks for long: a model call still running finishes in the background.</summary>
    public void Dispose()
    {
        stopping = true;
        wake.Set();
        thread?.Join(500);
    }
}
