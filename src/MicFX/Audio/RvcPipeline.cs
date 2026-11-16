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

    /// <summary>
    /// Synthesises <paramref name="frames"/> 10 ms frames into frames * SampleRate / 100 samples.
    /// <paramref name="coarsePitch"/> and <paramref name="f0"/> are empty when the model uses no pitch.
    /// </summary>
    void Generate(ReadOnlySpan<float> features, ReadOnlySpan<long> coarsePitch, ReadOnlySpan<float> f0, int frames, Span<float> audio);
}

/// <summary>
/// The RVC voice conversion steps around the three models, following RVC's
/// real-time inference (infer/lib/rtrvc.py, MIT): 16 kHz content features
/// over the whole context, each 20 ms frame used twice for the 10 ms frame
/// grid, pitch for the frames the generator sees with three frames dropped at
/// the start and one at the end of the tracker's window, and the generator
/// run on the newest frames only (24 frames of left context, as upstream
/// keeps for its flow, plus the frames returned). All shapes are fixed at
/// construction, so model sessions never re-plan while streaming.
/// </summary>
public sealed class RvcPipeline : IVoiceConverter
{
    /// <summary>Generator frames of left context before the returned frames.</summary>
    public const int LeftContextFrames = 24;

    private const float F0Min = 50f, F0Max = 1100f;
    private static readonly float MelMin = 1127f * MathF.Log(1f + F0Min / 700f);
    private static readonly float MelMax = 1127f * MathF.Log(1f + F0Max / 700f);

    private readonly IContentEncoder encoder;
    private readonly IPitchExtractor? pitch;
    private readonly IVoiceGenerator generator;
    private readonly RationalResampler down;
    private readonly RationalResampler up;
    private readonly int dim, outputFrames, generatorFrames, frames10, frames20, pitchWindow, returnSamples;
    private readonly float[] audio16;
    private readonly float[] features20;
    private readonly float[] features;
    private readonly float[] f0Window;
    private readonly float[] f0;
    private readonly long[] coarse;
    private readonly float[] generated;
    private readonly float[] resampled;
    private float transpose;

    /// <param name="timing">Stream timing; block, crossfade and search must be multiples of 10 ms.</param>
    /// <param name="contextMs">Audio before the returned part that the content encoder sees.</param>
    public RvcPipeline(VoiceTiming timing, IContentEncoder encoder, IPitchExtractor? pitch, IVoiceGenerator generator, int contextMs = 1000)
    {
        if (timing.Output % 480 != 0) throw new ArgumentException("Timing must be on a 10 ms grid.", nameof(timing));
        if (generator.UsesPitch && pitch == null) throw new ArgumentException("This model needs a pitch extractor.", nameof(pitch));
        this.encoder = encoder;
        this.pitch = generator.UsesPitch ? pitch : null;
        this.generator = generator;
        dim = generator.FeatureDim;

        outputFrames = timing.Output / 480;
        generatorFrames = LeftContextFrames + outputFrames;
        int contextFrames = Math.Max((contextMs + 9) / 10 + outputFrames, generatorFrames + 4);
        ContextSamples = contextFrames * 480;
        int samples16 = contextFrames * 160;
        frames10 = contextFrames;
        frames20 = (samples16 - 400) / 320 + 1;
        pitchWindow = 160 * (generatorFrames + 3);

        down = new RationalResampler(48000, 16000);
        up = new RationalResampler(generator.SampleRate, 48000);
        returnSamples = (outputFrames + 1) * generator.SampleRate / 100; // one extra frame for the resampler's left edge

        audio16 = new float[samples16];
        features20 = new float[frames20 * dim];
        features = new float[generatorFrames * dim];
        f0Window = new float[pitchWindow / 160 + 1];
        f0 = new float[generatorFrames];
        coarse = new long[generatorFrames];
        generated = new float[generatorFrames * generator.SampleRate / 100];
        resampled = new float[(outputFrames + 1) * 480];
    }

    public int ContextSamples { get; }

    /// <summary>Frames passed to the generator per call.</summary>
    public int GeneratorFrames => generatorFrames;

    /// <summary>Samples of 16 kHz audio the content encoder gets per call.</summary>
    public int EncoderSamples => audio16.Length;

    /// <summary>Samples of 16 kHz audio the pitch extractor gets per call.</summary>
    public int PitchSamples => pitchWindow;

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
        // once and repeats each frame twice); keep the generator's frames.
        int first = frames10 - generatorFrames;
        for (int t = 0; t < generatorFrames; t++)
        {
            int src = Math.Min((first + t) / 2, frames20 - 1);
            Array.Copy(features20, src * dim, features, t * dim, dim);
        }

        if (pitch != null)
        {
            pitch.Extract(audio16.AsSpan(audio16.Length - pitchWindow), f0Window);
            float factor = MathF.Pow(2f, Transpose / 12f);
            for (int t = 0; t < generatorFrames; t++)
            {
                float hz = f0Window[3 + t] * factor;
                f0[t] = hz;
                coarse[t] = CoarsePitch(hz);
            }
        }

        generator.Generate(features, pitch != null ? coarse : default, pitch != null ? f0 : default, generatorFrames, generated);
        up.Process(generated.AsSpan(generated.Length - returnSamples), resampled);
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
