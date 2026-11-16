using MicFX.Audio;
using Xunit;

namespace MicFX.Tests;

public class RvcPipelineTests
{
    private static readonly VoiceTiming Timing = VoiceTiming.FromMs(100, 60);

    private sealed class FrameIndexEncoder : IContentEncoder
    {
        public int Samples;
        public void Encode(ReadOnlySpan<float> audio16k, int dim, Span<float> features)
        {
            Samples = audio16k.Length;
            for (int f = 0; f < features.Length / dim; f++) features.Slice(f * dim, dim).Fill(f);
        }
    }

    private sealed class RampPitch : IPitchExtractor
    {
        public int Samples, Frames;
        public void Extract(ReadOnlySpan<float> audio16k, Span<float> f0)
        {
            Samples = audio16k.Length;
            Frames = f0.Length;
            for (int i = 0; i < f0.Length; i++) f0[i] = 100 + i;
        }
    }

    /// <summary>Records its inputs; outputs each frame's index as a constant.</summary>
    private sealed class RecordingGenerator : IVoiceGenerator
    {
        public int SampleRate { get; init; } = 48000;
        public int FeatureDim { get; init; } = 768;
        public bool UsesPitch { get; init; } = true;
        public float[] Features = Array.Empty<float>();
        public long[] Coarse = Array.Empty<long>();
        public float[] F0 = Array.Empty<float>();

        public void Generate(ReadOnlySpan<float> features, ReadOnlySpan<long> coarsePitch, ReadOnlySpan<float> f0, int frames, Span<float> audio)
        {
            Features = features.ToArray();
            Coarse = coarsePitch.ToArray();
            F0 = f0.ToArray();
            int per = SampleRate / 100;
            Assert.Equal(frames * per, audio.Length);
            for (int t = 0; t < frames; t++) audio.Slice(t * per, per).Fill(t);
        }
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(30f, 1)]
    [InlineData(50f, 1)]
    [InlineData(100f, 20)]
    [InlineData(220f, 60)]
    [InlineData(440f, 122)]
    [InlineData(1000f, 238)]
    [InlineData(1100f, 255)]
    [InlineData(1500f, 255)]
    public void CoarsePitch_MatchesRvc(float f0, long expected)
    {
        // Reference values from RVC's get_f0_post in float32.
        Assert.Equal(expected, RvcPipeline.CoarsePitch(f0));
    }

    [Fact]
    public void FeaturesArePairedFramesOfTheNewestGeneratorWindow()
    {
        var encoder = new FrameIndexEncoder();
        var gen = new RecordingGenerator { FeatureDim = 256 };
        var p = new RvcPipeline(Timing, encoder, new RampPitch(), gen);
        p.Convert(new float[p.ContextSamples], new float[Timing.Output], true);

        Assert.Equal(p.ContextSamples / 3, encoder.Samples);
        int frames10 = p.ContextSamples / 480;
        int frames20 = (encoder.Samples - 400) / 320 + 1;
        int g = p.GeneratorFrames;
        Assert.Equal(RvcPipeline.LeftContextFrames + Timing.Output / 480, g);
        Assert.Equal(g * 256, gen.Features.Length);
        for (int t = 0; t < g; t++)
        {
            float expected = Math.Min((frames10 - g + t) / 2, frames20 - 1);
            Assert.Equal(expected, gen.Features[t * 256]);
            Assert.Equal(expected, gen.Features[t * 256 + 255]);
        }
    }

    [Fact]
    public void PitchDropsTheTrackersEdgeFrames_AndTransposes()
    {
        var pitch = new RampPitch();
        var gen = new RecordingGenerator();
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), pitch, gen) { Transpose = 12 };
        p.Convert(new float[p.ContextSamples], new float[Timing.Output], true);

        int g = p.GeneratorFrames;
        Assert.Equal(160 * (g + 3), pitch.Samples);
        Assert.Equal(g + 4, pitch.Frames);
        for (int t = 0; t < g; t++)
        {
            float hz = (100 + 3 + t) * 2f;
            Assert.Equal(hz, gen.F0[t], 3);
            Assert.Equal(RvcPipeline.CoarsePitch(hz), gen.Coarse[t]);
        }
    }

    [Fact]
    public void ModelWithoutPitch_GetsNoPitch()
    {
        var gen = new RecordingGenerator { UsesPitch = false };
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), null, gen);
        p.Convert(new float[p.ContextSamples], new float[Timing.Output], true);
        Assert.Empty(gen.F0);
        Assert.Empty(gen.Coarse);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(40000)]
    [InlineData(32000)]
    public void OutputIsTheNewestGeneratedFrames_At48k(int rate)
    {
        var gen = new RecordingGenerator { SampleRate = rate };
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), new RampPitch(), gen);
        var output = new float[Timing.Output];
        p.Convert(new float[p.ContextSamples], output, true);

        int returned = Timing.Output / 480;
        int g = p.GeneratorFrames;
        // Away from the frame edges (resampler ringing), each 10 ms of output is one generator frame.
        for (int t = 0; t < returned; t++)
            for (int i = 120; i < 360; i++)
                Assert.Equal(g - returned + t, output[t * 480 + i], 2);
    }
}
