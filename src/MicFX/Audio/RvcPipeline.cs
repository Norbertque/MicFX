namespace MicFX.Audio;

/// <summary>Speech content features (ContentVec / HuBERT) from 16 kHz audio.</summary>
public interface IContentEncoder
{
    /// <summary>
    /// Encodes <paramref name="audio16k"/> into 20 ms frames of <paramref name="dim"/> features
    /// (row-major), frame f covering samples [320 f, 320 f + 400).
    /// </summary>
    void Encode(ReadOnlySpan<float> audio16k, int dim, Span<float> features);
}

/// <summary>Pitch tracker on 16 kHz audio.</summary>
public interface IPitchExtractor
{
    /// <summary>
    /// F0 in Hz (0 = unvoiced) for samples/160 + 1 frames, frame i centred on sample 160 i.
    /// </summary>
    void Extract(ReadOnlySpan<float> audio16k, Span<float> f0);
}

/// <summary>An RVC synthesizer (the voice model file).</summary>
public interface IVoiceGenerator
{
    /// <summary>Output sample rate: 32, 40 or 48 kHz.</summary>
    int SampleRate { get; }

    /// <summary>Content feature size: 256 (RVC v1) or 768 (v2).</summary>
    int FeatureDim { get; }

    /// <summary>False for models trained without pitch.</summary>
    bool UsesPitch { get; }

    /// <summary>Frames per call the model needs, or 0 when any length works.</summary>
    int Frames { get; }

    /// <summary>
    /// Synthesises frames [skipHead, skipHead + returnFrames) of the <c>features.Length / FeatureDim</c>
    /// 10 ms frames given, into returnFrames * SampleRate / 100 samples. Earlier frames are context.
    /// <paramref name="coarsePitch"/> and <paramref name="f0"/> are empty when the model uses no pitch.
    /// </summary>
    void Generate(ReadOnlySpan<float> features, ReadOnlySpan<long> coarsePitch, ReadOnlySpan<float> f0,
        int skipHead, int returnFrames, Span<float> audio);
}

/// <summary>
/// Streaming quality: how much extra the models see around each block. Measured
/// against converting a whole utterance at once (tools: see the 1.13 notes),
/// lookahead helps most, then decoder context, then a longer content context.
/// </summary>
/// <param name="EncoderContextMs">Audio before the returned part that the content encoder sees (0 = the generator's window).</param>
/// <param name="DecoderContextFrames">10 ms frames before the returned part that the decoder synthesises and drops.</param>
/// <param name="LookaheadFrames">10 ms frames after the returned part the models see; must match the stream timing's lookahead.</param>
public readonly record struct RvcQuality(int EncoderContextMs, int DecoderContextFrames, int LookaheadFrames);

/// <summary>
/// The RVC voice conversion steps around the three models, following RVC's
/// real-time inference (infer/lib/rtrvc.py, MIT): 16 kHz content features
/// over the whole context, each 20 ms frame used twice for the 10 ms frame
/// grid; pitch tracked only for the newest block plus 50 ms and kept in a
/// buffer that moves along with the blocks (the tracker's first three and
/// last frame are dropped); the generator asked for the newest frames only.
/// One frame more than the block's output is synthesised so the resampler
/// has real audio at its left edge. All shapes are fixed at construction,
/// so model sessions never re-plan while streaming.
/// </summary>
public sealed class RvcPipeline : IVoiceConverter
{
    /// <summary>Context frames used when the generator accepts any length (1 s plus the returned audio).</summary>
    public const int DefaultContextMs = 1000;

    private const float F0Min = 50f, F0Max = 1100f;
    private const float MaxF0 = 8000f; // above this a tracker's value is not a voice
    private static readonly float MelMin = 1127f * MathF.Log(1f + F0Min / 700f);
    private static readonly float MelMax = 1127f * MathF.Log(1f + F0Max / 700f);

    private readonly IContentEncoder encoder;
    private readonly IPitchExtractor? pitch;
    private readonly IVoiceGenerator generator;
    private readonly RationalResampler down;
    private readonly RationalResampler up;
    private readonly int dim, frames, encFrames, frames20, blockFrames, returnFrames, pitchSamples, pitchNew;
    private readonly int decoderContext, lookahead, perFrame;
    private readonly FeatureIndex? index;
    private float indexRate;
    private readonly float[] audio16;
    private readonly float[] features20;
    private readonly float[] features;
    private readonly float[] f0Window;
    private readonly float[] f0Cache;
    private readonly float[] f0;
    private readonly long[] coarse;
    private readonly float[] generated;
    private readonly float[] resampled;
    private float transpose;

    /// <param name="timing">Stream timing; block, crossfade, search and lookahead must be multiples of 10 ms.</param>
    /// <param name="index">The voice model's feature index, or null; its size must match the model's features.</param>
    public RvcPipeline(VoiceTiming timing, IContentEncoder encoder, IPitchExtractor? pitch, IVoiceGenerator generator,
        RvcQuality quality = default, FeatureIndex? index = null)
    {
        if (timing.Output % 480 != 0 || timing.Block % 480 != 0) throw new ArgumentException("Timing must be on a 10 ms grid.", nameof(timing));
        if (timing.Lookahead != quality.LookaheadFrames * 480) throw new ArgumentException("The timing's lookahead does not match the quality setting.", nameof(timing));
        if (index != null && index.Dim != generator.FeatureDim)
            throw new ArgumentException($"The index has {index.Dim} features per frame, the model {generator.FeatureDim}.", nameof(index));
        if (generator.UsesPitch && pitch == null) throw new ArgumentException("This model needs a pitch extractor.", nameof(pitch));
        this.encoder = encoder;
        this.pitch = generator.UsesPitch ? pitch : null;
        this.generator = generator;
        dim = generator.FeatureDim;

        int outputFrames = timing.Output / 480;
        returnFrames = outputFrames + 1;
        blockFrames = timing.Block / 480;
        decoderContext = quality.DecoderContextFrames;
        lookahead = quality.LookaheadFrames;
        perFrame = generator.SampleRate / 100;
        int needed = returnFrames + decoderContext + lookahead;
        frames = generator.Frames > 0 ? generator.Frames : DefaultContextMs / 10 + outputFrames + decoderContext + lookahead;
        if (frames < needed + 24 + 6 || frames < blockFrames + 6)
            throw new ArgumentException($"The model works on {frames} frames, too few for this delay and quality setting.", nameof(generator));
        encFrames = Math.Max(frames, quality.EncoderContextMs / 10 + outputFrames + lookahead);
        // Keep the 20 ms pairing of 10 ms frames the same from block to block.
        if ((encFrames - frames) % 2 != 0) encFrames++;
        ContextSamples = encFrames * 480;
        int samples16 = encFrames * 160;
        frames20 = (samples16 - 400) / 320 + 1;
        pitchSamples = blockFrames * 160 + 800; // the new block plus 50 ms
        pitchNew = blockFrames + 2;             // tracker frames kept: all but the first three and the last

        down = new RationalResampler(48000, 16000);
        up = new RationalResampler(generator.SampleRate, 48000);

        audio16 = new float[samples16];
        features20 = new float[frames20 * dim];
        features = new float[frames * dim];
        f0Window = new float[pitchSamples / 160 + 1];
        f0Cache = new float[frames];
        f0 = new float[frames];
        coarse = new long[frames];
        generated = new float[(returnFrames + decoderContext) * perFrame];
        this.index = index;
        resampled = new float[returnFrames * 480];
    }

    public int ContextSamples { get; }

    /// <summary>10 ms frames passed to the generator per call.</summary>
    public int Frames => frames;

    /// <summary>Frames the generator synthesises per call.</summary>
    public int ReturnFrames => returnFrames;

    /// <summary>Samples of 16 kHz audio the content encoder gets per call.</summary>
    public int EncoderSamples => audio16.Length;

    /// <summary>Samples of 16 kHz audio the pitch extractor gets per call.</summary>
    public int PitchSamples => pitchSamples;

    public bool HasIndex => index != null;

    /// <summary>Whether a generator with a fixed frame count has room for this timing and quality.</summary>
    public static bool Fits(IVoiceGenerator generator, VoiceTiming timing, RvcQuality quality)
    {
        if (generator.Frames <= 0) return true;
        int needed = timing.Output / 480 + 1 + quality.DecoderContextFrames + quality.LookaheadFrames;
        return generator.Frames >= needed + 24 + 6 && generator.Frames >= timing.Block / 480 + 6;
    }

    /// <summary>First generator frame synthesised per call (decoder context included).</summary>
    public int DecodeStart => frames - returnFrames - lookahead - decoderContext;

    /// <summary>
    /// How strongly features are pulled towards the voice model's own recordings
    /// (RVC's index rate, 0..1); no effect without an index. Safe to set from any thread.
    /// </summary>
    public float IndexRate
    {
        get => Volatile.Read(ref indexRate);
        set => Volatile.Write(ref indexRate, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Pitch shift in semitones applied before synthesis. Safe to set from any thread.</summary>
    public float Transpose
    {
        get => Volatile.Read(ref transpose);
        set => Volatile.Write(ref transpose, value);
    }

    public void Convert(ReadOnlySpan<float> context, Span<float> output, bool continuous)
    {
        down.Process(context, audio16);
        encoder.Encode(audio16, dim, features20);

        // Generator frame t is encoder 10 ms frame e = offset + t, which uses 20 ms frame
        // e / 2 (upstream appends the last frame once and repeats each frame twice).
        int offset = encFrames - frames;
        int start = DecodeStart;

        // RVC's index: only for the frames that are synthesised, as upstream.
        float rate = IndexRate;
        if (index != null && rate > 0f)
        {
            int first = Math.Min((offset + start) / 2, frames20 - 1);
            index.Blend(features20.AsSpan(first * dim, (frames20 - first) * dim), rate);
        }

        for (int t = 0; t < frames; t++)
            Array.Copy(features20, Math.Min((offset + t) / 2, frames20 - 1) * dim, features, t * dim, dim);

        if (pitch != null)
        {
            // The buffer moves one block per call; after a gap its old part is unvoiced.
            if (continuous) Array.Copy(f0Cache, blockFrames, f0Cache, 0, frames - blockFrames);
            else Array.Clear(f0Cache);
            pitch.Extract(audio16.AsSpan(audio16.Length - pitchSamples), f0Window);
            // Anything that is not a frequency (FCPE gives NaN for frames it is not
            // confident about, e.g. silence) is unvoiced.
            for (int i = 0; i < f0Window.Length; i++)
                if (!(f0Window[i] > 0f && f0Window[i] < MaxF0)) f0Window[i] = 0f;
            Array.Copy(f0Window, 3, f0Cache, frames - pitchNew, pitchNew);

            float factor = MathF.Pow(2f, Transpose / 12f);
            for (int t = 0; t < frames; t++)
            {
                float hz = f0Cache[t] * factor;
                f0[t] = hz;
                coarse[t] = CoarsePitch(hz);
            }
        }

        generator.Generate(features, pitch != null ? coarse : default, pitch != null ? f0 : default,
            start, returnFrames + decoderContext, generated);
        up.Process(generated.AsSpan(decoderContext * perFrame), resampled);
        resampled.AsSpan(480).CopyTo(output);
    }

    /// <summary>RVC's pitch quantisation: mel scale, 1 = unvoiced, 1..255 over 50..1100 Hz.</summary>
    public static long CoarsePitch(float f0)
    {
        if (!(f0 > 0f)) return 1; // unvoiced, and NaN
        float mel = 1127f * MathF.Log(1f + f0 / 700f);
        if (mel > 0) mel = (mel - MelMin) * 254f / (MelMax - MelMin) + 1f;
        if (mel <= 1f) mel = 1f;
        if (mel > 255f) mel = 255f;
        return (long)MathF.Round(mel);
    }
}
