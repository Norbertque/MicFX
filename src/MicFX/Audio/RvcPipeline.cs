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
    private static readonly float MelMin = 1127f * MathF.Log(1f + F0Min / 700f);
    private static readonly float MelMax = 1127f * MathF.Log(1f + F0Max / 700f);

    private readonly IContentEncoder encoder;
    private readonly IPitchExtractor? pitch;
    private readonly IVoiceGenerator generator;
    private readonly RationalResampler down;
    private readonly RationalResampler up;
    private readonly int dim, frames, frames20, blockFrames, returnFrames, pitchSamples, pitchNew;
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

    /// <param name="timing">Stream timing; block, crossfade and search must be multiples of 10 ms.</param>
    public RvcPipeline(VoiceTiming timing, IContentEncoder encoder, IPitchExtractor? pitch, IVoiceGenerator generator)
    {
        if (timing.Output % 480 != 0 || timing.Block % 480 != 0) throw new ArgumentException("Timing must be on a 10 ms grid.", nameof(timing));
        if (generator.UsesPitch && pitch == null) throw new ArgumentException("This model needs a pitch extractor.", nameof(pitch));
        this.encoder = encoder;
        this.pitch = generator.UsesPitch ? pitch : null;
        this.generator = generator;
        dim = generator.FeatureDim;

        int outputFrames = timing.Output / 480;
        returnFrames = outputFrames + 1;
        blockFrames = timing.Block / 480;
        frames = generator.Frames > 0 ? generator.Frames : DefaultContextMs / 10 + outputFrames;
        if (frames < returnFrames + 24 + 6 || frames < blockFrames + 6)
            throw new ArgumentException($"The model works on {frames} frames, too few for this delay setting.", nameof(generator));
        ContextSamples = frames * 480;
        int samples16 = frames * 160;
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
        generated = new float[returnFrames * generator.SampleRate / 100];
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

        // 10 ms frame j uses 20 ms frame j / 2 (upstream appends the last frame
        // once and repeats each frame twice).
        for (int t = 0; t < frames; t++)
            Array.Copy(features20, Math.Min(t / 2, frames20 - 1) * dim, features, t * dim, dim);

        if (pitch != null)
        {
            // The buffer moves one block per call; after a gap its old part is unvoiced.
            if (continuous) Array.Copy(f0Cache, blockFrames, f0Cache, 0, frames - blockFrames);
            else Array.Clear(f0Cache);
            pitch.Extract(audio16.AsSpan(audio16.Length - pitchSamples), f0Window);
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
            frames - returnFrames, returnFrames, generated);
        up.Process(generated, resampled);
        resampled.AsSpan(480).CopyTo(output);
    }

    /// <summary>RVC's pitch quantisation: mel scale, 1 = unvoiced, 1..255 over 50..1100 Hz.</summary>
    public static long CoarsePitch(float f0)
    {
        float mel = 1127f * MathF.Log(1f + f0 / 700f);
        if (mel > 0) mel = (mel - MelMin) * 254f / (MelMax - MelMin) + 1f;
        if (mel <= 1f) mel = 1f;
        if (mel > 255f) mel = 255f;
        return (long)MathF.Round(mel);
    }
}
