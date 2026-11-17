using MicFX.AiVoice;
using MicFX.Audio;
using Xunit;
using Xunit.Abstractions;

namespace MicFX.Tests;

/// <summary>
/// The ONNX Runtime wrappers, on the CPU provider, with the tiny models from
/// tools/make_test_models.py (see that script for what each one computes).
/// </summary>
public class OnnxModelTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly ITestOutputHelper log;

    public OnnxModelTests(ITestOutputHelper log) => this.log = log;

    private static OnnxVoiceGenerator Generator(string name) =>
        new(Path.Combine(Fixtures, name), OnnxSessions.Create(AiBackend.Cpu, 1));

    private static (float[] Features, long[] Coarse, float[] F0) Frames(int frames, int dim)
    {
        var features = new float[frames * dim];
        var f0 = new float[frames];
        var coarse = new long[frames];
        for (int t = 0; t < frames; t++)
        {
            features[t * dim] = t * 0.25f;
            f0[t] = 100 + t;
            coarse[t] = RvcPipeline.CoarsePitch(f0[t]);
        }
        return (features, coarse, f0);
    }

    [Fact]
    public void MicFxExport_IsAskedForExactlyTheReturnedFrames()
    {
        using var gen = Generator("gen-micfx.onnx");
        Assert.Equal(RvcDialect.MicFx, gen.Dialect);
        Assert.Equal(40000, gen.SampleRate);
        Assert.Equal(768, gen.FeatureDim);
        Assert.Equal(64, gen.Frames);
        Assert.True(gen.UsesPitch);

        var (features, coarse, f0) = Frames(64, 768);
        var audio = new float[3 * 400];
        gen.Generate(features, coarse, f0, 50, 3, audio);
        for (int f = 0; f < 3; f++)
            for (int i = 0; i < 400; i++)
                Assert.Equal(100 + 50 + f + (50 + f) * 0.25f, audio[f * 400 + i], 4);
    }

    [Fact]
    public void WOkadaExport_WithHalfFeatures_GetsContextWindowAndIsTrimmed()
    {
        using var gen = Generator("gen-wokada-half.onnx");
        Assert.Equal(RvcDialect.WOkada, gen.Dialect);
        Assert.Equal(32000, gen.SampleRate);
        Assert.Equal(256, gen.FeatureDim);
        Assert.Equal(0, gen.Frames);

        var (features, coarse, f0) = Frames(100, 256);
        var audio = new float[20 * 320];
        gen.Generate(features, coarse, f0, 80, 20, audio);
        for (int f = 0; f < 20; f++)
            Assert.Equal(100 + 80 + f + (80 + f) * 0.25f, audio[f * 320 + 100], 1); // features went through float16
    }

    [Fact]
    public void WebUiExport_WithoutMetadata_HasItsSampleRateMeasured()
    {
        using var gen = Generator("gen-webui.onnx");
        Assert.Equal(RvcDialect.WebUi, gen.Dialect);
        Assert.Equal(48000, gen.SampleRate);
        Assert.Equal(256, gen.FeatureDim);

        var (features, coarse, f0) = Frames(90, 256);
        var audio = new float[16 * 480];
        gen.Generate(features, coarse, f0, 74, 16, audio);
        Assert.Equal(100 + 74 + 74 * 0.25f, audio[10], 4);
        Assert.Equal(100 + 89 + 89 * 0.25f, audio[^1], 4);
    }

    [Fact]
    public void ApplioExport_IsAskedForTheReturnedFramesPlusFlowContext()
    {
        using var gen = Generator("gen-applio.onnx");
        Assert.Equal(RvcDialect.Applio, gen.Dialect);
        Assert.Equal(32000, gen.SampleRate); // from Applio's "model_info"
        Assert.Equal(256, gen.FeatureDim);
        Assert.Equal(0, gen.Frames);

        foreach (int total in new[] { 100, 115, 128 })
        {
            var (features, coarse, f0) = Frames(total, 256);
            var audio = new float[16 * 320];
            gen.Generate(features, coarse, f0, total - 16, 16, audio);
            for (int f = 0; f < 16; f++)
            {
                int frame = total - 16 + f;
                Assert.Equal(100 + frame + frame * 0.25f, audio[f * 320 + 100], 3);
            }
        }
    }

    [Fact]
    public void OtherOnnxFiles_AreRejectedWithAReason()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Generator("fcpe-tiny.onnx"));
        Assert.Contains("not an RVC voice model", ex.Message);
        Assert.Throws<InvalidDataException>(() => new OnnxContentEncoder(Path.Combine(Fixtures, "fcpe-tiny.onnx"), OnnxSessions.Create(AiBackend.Cpu, 1)));
        Assert.Throws<InvalidDataException>(() => new OnnxPitchExtractor(Path.Combine(Fixtures, "gen-micfx.onnx"), OnnxSessions.Create(AiBackend.Cpu, 1)));
    }

    [Fact]
    public void HelperModels_ReturnTheirFramesInTheExpectedLayout()
    {
        using var encoder = new OnnxContentEncoder(Path.Combine(Fixtures, "contentvec-tiny.onnx"), OnnxSessions.Create(AiBackend.Cpu, 1));
        var audio = new float[16000];
        for (int i = 0; i < audio.Length; i++) audio[i] = i / 16000f;
        int frames = (audio.Length - 400) / 320 + 1;
        foreach (int dim in new[] { 256, 768 })
        {
            var features = new float[frames * dim];
            encoder.Encode(audio, dim, features);
            for (int f = 0; f < frames; f++)
            {
                float mean = (320 * f + 199.5f) / 16000f;
                Assert.Equal(mean, features[f * dim], 3);
                Assert.Equal(mean, features[f * dim + dim - 1], 3);
            }
        }

        using var pitch = new OnnxPitchExtractor(Path.Combine(Fixtures, "fcpe-tiny.onnx"), OnnxSessions.Create(AiBackend.Cpu, 1));
        var f0 = new float[audio.Length / 160 + 1];
        pitch.Extract(audio, f0);
        Assert.Equal(100 + 1000 * (79.5f / 16000f), f0[0], 2);
        Assert.Equal(100f, f0[^1], 2);
    }

    [Fact]
    public void WholeChain_StreamsThroughOnnxRuntime()
    {
        var timing = VoiceTiming.FromMs(100, 60);
        var options = OnnxSessions.Create(AiBackend.Cpu, 1);
        using var encoder = new OnnxContentEncoder(Path.Combine(Fixtures, "contentvec-tiny.onnx"), options);
        using var pitch = new OnnxPitchExtractor(Path.Combine(Fixtures, "fcpe-tiny.onnx"), options);
        using var gen = Generator("gen-micfx.onnx");
        var pipeline = new RvcPipeline(timing, encoder, pitch, gen);
        using var stream = new VoiceStream(timing, pipeline);

        var input = new float[48000 * 2];
        for (int i = 0; i < input.Length; i++) input[i] = 0.3f * MathF.Sin(2 * MathF.PI * 150 * i / 48000);
        var provider = new AiVoiceSampleProvider(TestSignals.FromArray(input)) { Stream = stream, Enabled = true };
        var output = new float[input.Length];
        for (int pos = 0; pos < input.Length; pos += 480)
        {
            Assert.Equal(480, provider.Read(output, pos, 480));
            while (stream.TryStep()) { }
        }
        Assert.Null(stream.Fault);
        Assert.Equal(0, stream.MissedBlocks);
        Assert.All(output, v => Assert.True(float.IsFinite(v)));
        Assert.Contains(output, v => v != 0);
    }

    /// <summary>
    /// The real models, when MICFX_MODELS_DIR has contentvec.onnx, fcpe.onnx and a voice
    /// model voice.onnx (e.g. from convert_rvc.py --untrained). Prints the time per block.
    /// </summary>
    [Fact]
    public void RealModels_WhenAvailable()
    {
        string? dir = Environment.GetEnvironmentVariable("MICFX_MODELS_DIR");
        if (dir == null) return;
        var timing = VoiceTiming.FromMs(100, 60);
        var options = OnnxSessions.Create(AiBackend.Cpu, OnnxSessions.DefaultCpuThreads);
        using var encoder = new OnnxContentEncoder(Path.Combine(dir, "contentvec.onnx"), options);
        using var pitch = new OnnxPitchExtractor(Path.Combine(dir, "fcpe.onnx"), options);
        using var gen = new OnnxVoiceGenerator(Path.Combine(dir, "voice.onnx"), options);
        var pipeline = new RvcPipeline(timing, encoder, pitch, gen);
        log.WriteLine($"{gen.Dialect}, {gen.SampleRate} Hz, {gen.FeatureDim} features, {pipeline.Frames} frames");

        var context = new float[pipeline.ContextSamples];
        // A voiced sound that stops: the pitch tracker then sees silence, for which FCPE gives NaN.
        for (int i = 0; i < context.Length - 9600; i++) context[i] = 0.2f * MathF.Sin(2 * MathF.PI * 140 * i / 48000);
        var output = new float[timing.Output];
        var times = new List<double>();
        for (int k = 0; k < 8; k++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            pipeline.Convert(context, output, k > 0);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Assert.All(output, v => Assert.True(float.IsFinite(v)));
        log.WriteLine($"per block: {string.Join(", ", times.Select(t => t.ToString("F0")))} ms");
    }
}
