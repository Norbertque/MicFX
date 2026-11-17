using MicFX.Audio;
using NAudio.Wave;
using Xunit;

namespace MicFX.Tests;

/// <summary>
/// The AI voice streaming core, driven with a fake model. The worker is stepped
/// synchronously, and the fake model's "inference time" is audio read while it
/// runs, so every deadline decision is a deterministic sample count.
/// </summary>
public class VoiceStreamTests
{
    // 100 ms blocks, 40 ms SOLA overlap, 10 ms search, 60 ms compute allowance.
    private static readonly VoiceTiming Timing = VoiceTiming.FromMs(100, 60);
    private static readonly int D = Timing.Delay;
    private static readonly int FirstBlock = Timing.Block + Timing.Allowance;
    private static readonly int Clean = D + VoiceStream.FadeSamples; // past the stream's 5 ms input fade-in

    /// <summary>
    /// Returns the input unchanged, optionally shifted in time per call (the way a
    /// model's output drifts in phase), and can read audio while it "computes".
    /// </summary>
    private sealed class FakeModel : IVoiceConverter
    {
        public int ContextSamples { get; init; } = Timing.Output + Timing.Search + 960;
        public int Calls;
        public Func<int, int>? Shift;
        public Action<int>? WhileComputing;
        public Func<int, bool>? Throws;
        public int SleepMs;

        public void Convert(ReadOnlySpan<float> context, Span<float> output, bool continuous)
        {
            int call = Calls++;
            WhileComputing?.Invoke(call);
            if (SleepMs > 0) Thread.Sleep(SleepMs);
            if (Throws?.Invoke(call) == true) throw new InvalidOperationException("out of memory");
            int d = Shift?.Invoke(call) ?? 0;
            int start = context.Length - output.Length;
            for (int i = 0; i < output.Length; i++) output[i] = context[start + i - d];
        }
    }

    /// <summary>A provider on a stream, reading a fixed input and recording everything it outputs.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly float[] Input;
        public readonly float[] Output;
        public readonly AiVoiceSampleProvider Provider;
        public readonly VoiceStream Stream;
        public readonly FakeModel Model;
        public readonly int Channels;
        public int Pos; // frames read so far
        public bool Pump = true;

        public Rig(float[] input, IVoiceConverter? model = null, int channels = 1, VoiceTiming? timing = null)
        {
            Input = input;
            Channels = channels;
            Output = new float[input.Length];
            Model = model as FakeModel ?? new FakeModel();
            Stream = new VoiceStream(timing ?? Timing, model ?? Model);
            Provider = new AiVoiceSampleProvider(TestSignals.FromArray(input, channels)) { Stream = Stream, Enabled = true };
        }

        /// <summary>Reads n frames as the output thread would; the worker then catches up instantly.</summary>
        public void Read(int frames)
        {
            int n = Math.Min(frames, Input.Length / Channels - Pos) * Channels;
            if (n <= 0) return;
            int got = Provider.Read(Output, Pos * Channels, n);
            Assert.Equal(n, got);
            Pos += n / Channels;
            if (Pump) StepWorker();
        }

        public void StepWorker()
        {
            while (Stream.TryStep()) { }
        }

        public void Run(int untilFrame, int chunk)
        {
            while (Pos < untilFrame) Read(Math.Min(chunk, untilFrame - Pos));
        }

        public void Dispose() => Stream.Dispose();
    }

    private static float[] Noise(int n, int seed = 1, float amp = 0.3f)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = amp * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    private static float[] Sine(int n, float hz = 80f, float amp = 0.5f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = amp * MathF.Sin(2 * MathF.PI * hz * i / TestSignals.Rate);
        return x;
    }

    private static float Delayed(float[] input, int i) => i - D >= 0 ? input[i - D] : 0f;

    private static void AssertDelayed(float[] input, float[] output, int from, int to, float tol = 1e-6f)
    {
        for (int i = from; i < to; i++)
            Assert.True(Math.Abs(output[i] - Delayed(input, i)) <= tol,
                $"sample {i}: got {output[i]}, expected {Delayed(input, i)}");
    }

    private static float MaxStep(float[] x, int from, int to)
    {
        float m = 0;
        for (int i = Math.Max(1, from); i < to; i++) m = Math.Max(m, Math.Abs(x[i] - x[i - 1]));
        return m;
    }

    private static long BlockStart(long k) => FirstBlock + k * Timing.Block;

    [Fact]
    public void Off_IsBitExactPassThrough_AndNeverRunsTheModel()
    {
        foreach (int channels in new[] { 1, 2 })
        {
            var input = Noise(48000 * channels);
            using var rig = new Rig(input, channels: channels);
            rig.Provider.Enabled = false;
            rig.Run(48000, 480);
            Assert.Equal(input, rig.Output);
            Assert.Equal(0, rig.Model.Calls);
        }
    }

    /// <summary>Identity model that returns the audio ending <c>lookahead</c> samples before the newest.</summary>
    private sealed class LookaheadModel : IVoiceConverter
    {
        private readonly int lookahead;
        public LookaheadModel(int lookahead) => this.lookahead = lookahead;
        public int ContextSamples => Timing.Output + 9600;
        public void Convert(ReadOnlySpan<float> context, Span<float> output, bool continuous) =>
            context.Slice(context.Length - lookahead - output.Length, output.Length).CopyTo(output);
    }

    [Fact]
    public void Lookahead_AddsExactlyItsLengthToTheDelay()
    {
        var timing = VoiceTiming.FromMs(100, 60, lookaheadMs: 30);
        Assert.Equal(Timing.Delay + 1440, timing.Delay);
        var input = Noise(48000 * 3, seed: 12);
        using var rig = new Rig(input, new LookaheadModel(timing.Lookahead), timing: timing);
        rig.Run(input.Length, 480);
        int d = timing.Delay;
        for (int i = d + VoiceStream.FadeSamples; i < input.Length; i++)
            Assert.True(Math.Abs(rig.Output[i] - input[i - d]) <= 1e-6f, $"sample {i}");
    }

    [Fact]
    public void IdentityModel_GivesInputDelayedByExactlyD_ForAnyReadSizeUpToTheAllowance()
    {
        var input = Noise(48000 * 3);
        float[]? reference = null;
        foreach (int chunk in new[] { 480, 1, 7, 441, 1024, Timing.Allowance })
        {
            using var rig = new Rig(input);
            rig.Run(input.Length, chunk);
            AssertDelayed(input, rig.Output, Clean, input.Length);
            Assert.Equal(0, rig.Stream.MissedBlocks);
            if (reference == null) reference = rig.Output;
            else Assert.Equal(reference, rig.Output);
        }
    }

    [Fact]
    public void SolaRealignsAModelThatShiftsEachBlock()
    {
        var input = Noise(48000 * 3, seed: 2);
        var rng = new Random(5);
        // Block 0 has nothing to line up with, so the chain keeps its shift; start it at 0.
        var shifts = Enumerable.Range(0, 200).Select(i => i == 0 ? 0 : rng.Next(0, Timing.Search + 1)).ToArray();
        using var rig = new Rig(input, new FakeModel { Shift = c => shifts[c] });
        rig.Run(input.Length, 480);
        // Crossfading two identical stretches is exact up to float rounding.
        AssertDelayed(input, rig.Output, (int)BlockStart(1) + Timing.Crossfade, input.Length, tol: 1e-5f);
    }

    [Fact]
    public void StereoMic_BothChannelsCarryTheConvertedMix()
    {
        var left = Noise(48000 * 2, seed: 3);
        var right = Noise(48000 * 2, seed: 4);
        var input = new float[left.Length * 2];
        var mix = new float[left.Length];
        for (int i = 0; i < left.Length; i++)
        {
            input[2 * i] = left[i];
            input[2 * i + 1] = right[i];
            mix[i] = 0.5f * (left[i] + right[i]);
        }
        using var rig = new Rig(input, channels: 2);
        rig.Run(left.Length, 480);
        for (int i = Clean; i < left.Length; i++)
        {
            Assert.Equal(rig.Output[2 * i], rig.Output[2 * i + 1]);
            Assert.True(Math.Abs(rig.Output[2 * i] - Delayed(mix, i)) <= 1e-6f);
        }
    }

    [Fact]
    public void Silence_GivesExactSilence_AndSkipsTheModel()
    {
        var input = new float[48000 * 2];
        using var rig = new Rig(input);
        rig.Run(input.Length, 480);
        Assert.All(rig.Output, v => Assert.Equal(0f, v));
        Assert.Equal(0, rig.Model.Calls);
        Assert.Equal(0, rig.Stream.MissedBlocks);
    }

    [Fact]
    public void LateBlock_FadesOutWithoutAClick_IsNeverPlayed_AndTheVoiceReturnsWithTheSameDelay()
    {
        var input = Sine(48000 * 4);
        const int late = 10;
        Rig? r = null;
        // Block 10's "inference" runs past its deadline; its output is a marker value.
        using var rig = new Rig(input, new MarkerModel(late, () => r!));
        r = rig;
        rig.Run(input.Length, 480);

        Assert.Equal(1, rig.Stream.MissedBlocks);
        long missStart = BlockStart(late);
        // Fades out over 5 ms, then exact silence for the rest of the block.
        for (long i = missStart + VoiceStream.FadeSamples; i < missStart + Timing.Block; i++)
            Assert.Equal(0f, rig.Output[i]);
        // No step anywhere is larger than the sine's own steepest step (plus a little for the fades).
        float sineStep = 0.5f * 2 * MathF.PI * 80 / 48000;
        Assert.True(MaxStep(rig.Output, Clean, rig.Output.Length) < sineStep * 1.5f);
        // After the hold it plays again, with exactly the same delay.
        int resume = (int)BlockStart(late + 1 + VoiceStream.HoldBlocks(Timing)) + VoiceStream.FadeSamples;
        Assert.True(resume < input.Length - Timing.Block);
        AssertDelayed(input, rig.Output, resume, input.Length, tol: 1e-5f);
        Assert.True(rig.Stream.IsLive);
    }

    /// <summary>Identity model whose block <c>late</c> reads past its deadline (and is quieter, to tell it apart).</summary>
    private sealed class MarkerModel : IVoiceConverter
    {
        private readonly int late;
        private readonly Func<Rig> rig;
        private int calls;
        public MarkerModel(int late, Func<Rig> r) { this.late = late; rig = r; }
        public int ContextSamples => Timing.Output + Timing.Search + 960;

        public void Convert(ReadOnlySpan<float> context, Span<float> output, bool continuous)
        {
            int call = calls++;
            if (call == late)
            {
                rig().Read(Timing.Allowance + 480);
                for (int i = 0; i < output.Length; i++) output[i] = 0.5f * context[context.Length - output.Length + i];
                return;
            }
            context[^output.Length..].CopyTo(output);
        }
    }

    [Fact]
    public void WorkerFarBehind_SkipsStaleBlocks_AndCatchesUp()
    {
        var input = Noise(48000 * 4, seed: 6);
        using var rig = new Rig(input);
        rig.Run(48000, 480);
        int callsBefore = rig.Model.Calls;
        // The worker stalls for 600 ms while audio keeps flowing.
        rig.Pump = false;
        rig.Run(48000 + 28800, 480);
        rig.Pump = true;
        rig.StepWorker();
        // Only the newest block whose deadline has not passed is converted.
        Assert.Equal(callsBefore + 1, rig.Model.Calls);
        rig.Run(input.Length, 480);
        Assert.True(rig.Stream.MissedBlocks >= 1);
        int resume = 48000 + 28800 + (VoiceStream.HoldBlocks(Timing) + 3) * Timing.Block;
        AssertDelayed(input, rig.Output, resume, input.Length);
    }

    [Fact]
    public void MarginalGpu_DoesNotFlap()
    {
        foreach (int every in new[] { 3, 5 })
        {
            var input = Noise(48000 * 12, seed: 7);
            Rig? r = null;
            var model = new FakeModel { WhileComputing = c => { if (c % every == every - 1) r!.Read(Timing.Allowance + 480); } };
            using var rig = new Rig(input, model);
            r = rig;
            rig.Run(input.Length, 480);
            // One drop-out, perhaps a second; never a stutter of play/silence/play.
            Assert.InRange(rig.Stream.MissedBlocks, 1, 2);
        }
    }

    [Fact]
    public void TightHeadroom_DoesNotCountTowardsRecovery()
    {
        var input = Noise(48000 * 4, seed: 8);
        Rig? r = null;
        // Block 5 misses; afterwards every block lands only 5 ms before its deadline.
        var model = new FakeModel
        {
            WhileComputing = c =>
            {
                if (c == 5) r!.Read(Timing.Allowance + 480);
                else if (c > 5) r!.Read(Timing.Allowance - 480);
            },
        };
        using var rig = new Rig(input, model);
        r = rig;
        rig.Run(input.Length, 240);
        Assert.Equal(1, rig.Stream.MissedBlocks);
        Assert.False(rig.Stream.IsLive);
        long missStart = BlockStart(5);
        for (long i = missStart + VoiceStream.FadeSamples; i < input.Length; i++)
            Assert.Equal(0f, rig.Output[i]);
    }

    [Fact]
    public void ReadsLongerThanTheAllowance_MissSomeBlocks_ButStayCleanAndOnTime()
    {
        var input = Sine(48000 * 3);
        using var rig = new Rig(input);
        rig.Run(input.Length, Timing.Allowance + 1200);
        Assert.True(rig.Stream.MissedBlocks >= 1);
        AssertScaledDelayed(input, rig.Output, 1440);
        float sineStep = 0.5f * 2 * MathF.PI * 80 / 48000;
        Assert.True(MaxStep(rig.Output, 1440, input.Length) < sineStep * 1.5f);
    }

    /// <summary>Every sample is the input delayed by exactly D times a gain between 0 and 1.</summary>
    private static void AssertScaledDelayed(float[] input, float[] output, int from)
    {
        for (int i = from; i < output.Length; i++)
        {
            float expected = Delayed(input, i);
            Assert.True(Math.Abs(output[i]) <= Math.Abs(expected) + 1e-5f, $"sample {i}");
            Assert.True(output[i] * expected >= -1e-9f, $"sample {i}");
        }
    }

    [Fact]
    public void ReEnabling_NeverReplaysAudioFromBefore()
    {
        var speech = Noise(48000 * 2, seed: 10);
        var input = new float[48000 * 5];
        speech.CopyTo(input, 0);
        using var rig = new Rig(input);
        rig.Run(48000 * 2, 480);
        rig.Provider.Enabled = false;
        rig.Run(48000 * 2 + 4800, 480);
        rig.Provider.Enabled = true;
        int reenabled = rig.Pos;
        rig.Run(input.Length, 480);
        for (int i = reenabled; i < input.Length; i++) Assert.Equal(0f, rig.Output[i]);

        // A new provider on the same stream (the engine restarted) starts clean too.
        var fresh = new AiVoiceSampleProvider(TestSignals.FromArray(new float[48000 * 2])) { Stream = rig.Stream, Enabled = true };
        var outFresh = new float[48000 * 2];
        for (int pos = 0; pos < outFresh.Length; pos += 480)
        {
            fresh.Read(outFresh, pos, 480);
            while (rig.Stream.TryStep()) { }
        }
        Assert.All(outFresh, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ModelThatThrows_IsReportedAndFadesToSilence()
    {
        var input = Sine(48000 * 3);
        using var rig = new Rig(input, new FakeModel { Throws = c => c == 8 });
        rig.Run(input.Length, 480);
        Assert.Contains("out of memory", rig.Stream.Fault);
        long missStart = BlockStart(8);
        for (long i = missStart + VoiceStream.FadeSamples; i < input.Length; i++)
            Assert.Equal(0f, rig.Output[i]);
        float sineStep = 0.5f * 2 * MathF.PI * 80 / 48000;
        Assert.True(MaxStep(rig.Output, Clean, input.Length) < sineStep * 1.5f);
    }

    [Fact]
    public void SwitchingOnAndOff_IsClickFree()
    {
        var input = Sine(48000 * 4);
        using var rig = new Rig(input);
        rig.Run(48000, 480);
        rig.Provider.Enabled = false;
        rig.Run(48000 * 2, 480);
        rig.Provider.Enabled = true;
        rig.Run(48000 * 3, 480);
        rig.Provider.Enabled = false;
        rig.Run(input.Length, 480);
        float sineStep = 0.5f * 2 * MathF.PI * 80 / 48000;
        // Dry and converted are out of phase, so the 30 ms crossfade may add a little slope.
        Assert.True(MaxStep(rig.Output, 0, input.Length) < sineStep * 2f);
        // Fully off again: bit-exact dry.
        for (int i = 48000 * 3 + 1440; i < input.Length; i++) Assert.Equal(input[i], rig.Output[i]);
    }

    [Fact]
    public void SwappingTheStream_FadesOut_ThenStartsTheNewOneClean()
    {
        var input = Sine(48000 * 4);
        using var rig = new Rig(input);
        rig.Run(48000 * 2, 480);
        var second = new VoiceStream(VoiceTiming.FromMs(60, 40), new FakeModel());
        rig.Provider.Stream = second;
        int swapAt = rig.Pos;
        while (rig.Pos < input.Length)
        {
            rig.Read(480);
            while (second.TryStep()) { }
        }
        float sineStep = 0.5f * 2 * MathF.PI * 80 / 48000;
        float step = MaxStep(rig.Output, Clean, input.Length);
        // A 5 ms fade on a full-scale sine adds up to 0.6 of its own steepest step; a click would be ~100 times that.
        Assert.True(step < sineStep * 2f, $"step {step} vs sine {sineStep}");
        // The new stream plays the input with its own, shorter delay.
        int d2 = VoiceTiming.FromMs(60, 40).Delay;
        int from = swapAt + 480 + d2 + VoiceTiming.FromMs(60, 40).Crossfade + 4800;
        for (int i = from; i < input.Length; i++) Assert.True(Math.Abs(rig.Output[i] - input[i - d2]) < 1e-5f, $"sample {i}");
        second.Dispose();
    }

    [Fact]
    public void RealWorkerThread_SmokeTest()
    {
        // Only properties that hold at any timing: every sample is the input delayed by
        // exactly D times a gain between 0 and 1, the counts are exact, shutdown is clean.
        var input = Noise(48000 * 2, seed: 11);
        var model = new FakeModel { SleepMs = 2 };
        var stream = new VoiceStream(Timing, model);
        var provider = new AiVoiceSampleProvider(TestSignals.FromArray(input)) { Stream = stream, Enabled = true };
        stream.StartWorker();
        var output = new float[input.Length];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int pos = 0; pos < input.Length; pos += 480)
        {
            Assert.Equal(480, provider.Read(output, pos, 480));
            int ahead = (int)(pos / 48 - clock.ElapsedMilliseconds);
            if (ahead > 0) Thread.Sleep(ahead);
        }
        var stop = System.Diagnostics.Stopwatch.StartNew();
        stream.Dispose();
        Assert.True(stop.ElapsedMilliseconds < 2000);
        AssertScaledDelayed(input, output, 1440);
    }
}
