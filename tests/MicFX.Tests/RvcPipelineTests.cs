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

    /// <summary>Records its inputs; outputs each returned frame's index as a constant.</summary>
    private sealed class RecordingGenerator : IVoiceGenerator
    {
        public int SampleRate { get; init; } = 48000;
        public int FeatureDim { get; init; } = 768;
        public bool UsesPitch { get; init; } = true;
        public int Frames { get; init; }
        public float[] Features = Array.Empty<float>();
        public long[] Coarse = Array.Empty<long>();
        public float[] F0 = Array.Empty<float>();
        public int SkipHead, ReturnFrames;

        public void Generate(ReadOnlySpan<float> features, ReadOnlySpan<long> coarsePitch, ReadOnlySpan<float> f0,
            int skipHead, int returnFrames, Span<float> audio)
        {
            Features = features.ToArray();
            Coarse = coarsePitch.ToArray();
            F0 = f0.ToArray();
            SkipHead = skipHead;
            ReturnFrames = returnFrames;
            int per = SampleRate / 100;
            Assert.Equal(returnFrames * per, audio.Length);
            for (int t = 0; t < returnFrames; t++) audio.Slice(t * per, per).Fill(skipHead + t);
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
    public void FeaturesArePairedFrames_AndTheGeneratorIsAskedForTheNewestFrames()
    {
        var encoder = new FrameIndexEncoder();
        var gen = new RecordingGenerator { FeatureDim = 256 };
        var p = new RvcPipeline(Timing, encoder, new RampPitch(), gen);
        p.Convert(new float[p.ContextSamples], new float[Timing.Output], true);

        int frames = p.Frames;
        Assert.Equal(RvcPipeline.DefaultContextMs / 10 + Timing.Output / 480, frames);
        Assert.Equal(p.ContextSamples / 3, encoder.Samples);
        int frames20 = (encoder.Samples - 400) / 320 + 1;
        Assert.Equal(frames * 256, gen.Features.Length);
        for (int t = 0; t < frames; t++)
        {
            float expected = Math.Min(t / 2, frames20 - 1);
            Assert.Equal(expected, gen.Features[t * 256]);
            Assert.Equal(expected, gen.Features[t * 256 + 255]);
        }
        Assert.Equal(Timing.Output / 480 + 1, gen.ReturnFrames);
        Assert.Equal(frames - gen.ReturnFrames, gen.SkipHead);
    }

    [Fact]
    public void ModelWithFixedLength_SetsTheContext()
    {
        var gen = new RecordingGenerator { Frames = 128 };
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), new RampPitch(), gen);
        Assert.Equal(128, p.Frames);
        Assert.Equal(128 * 480, p.ContextSamples);
        Assert.Throws<ArgumentException>(() => new RvcPipeline(Timing, new FrameIndexEncoder(), new RampPitch(), new RecordingGenerator { Frames = 30 }));
    }

    [Fact]
    public void PitchIsTrackedForTheNewBlockOnly_AndMovesAlongWithTheBlocks()
    {
        var pitch = new RampPitch();
        var gen = new RecordingGenerator();
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), pitch, gen) { Transpose = 12 };
        int block = Timing.Block / 480, kept = block + 2, frames = p.Frames;

        p.Convert(new float[p.ContextSamples], new float[Timing.Output], continuous: false);
        Assert.Equal(160 * block + 800, pitch.Samples);
        Assert.Equal(block + 6, pitch.Frames);
        // After a gap only the new part is voiced: tracker frames 3 .. n-2, transposed an octave up.
        for (int t = 0; t < frames - kept; t++) Assert.Equal(0f, gen.F0[t]);
        for (int j = 0; j < kept; j++)
        {
            float hz = (100 + 3 + j) * 2f;
            Assert.Equal(hz, gen.F0[frames - kept + j], 3);
            Assert.Equal(RvcPipeline.CoarsePitch(hz), gen.Coarse[frames - kept + j]);
        }
        Assert.All(gen.Coarse.Take(frames - kept), c => Assert.Equal(1, c));

        // Next block: the earlier values move back by one block, the newest are overwritten.
        p.Convert(new float[p.ContextSamples], new float[Timing.Output], continuous: true);
        for (int j = 0; j < kept - block; j++)
            Assert.Equal((100 + 3 + j) * 2f, gen.F0[frames - kept - block + j], 3);
        for (int j = 0; j < kept; j++)
            Assert.Equal((100 + 3 + j) * 2f, gen.F0[frames - kept + j], 3);
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
    public void OutputIsTheReturnedFrames_At48k_WithoutTheResamplersEdgeFrame(int rate)
    {
        var gen = new RecordingGenerator { SampleRate = rate };
        var p = new RvcPipeline(Timing, new FrameIndexEncoder(), new RampPitch(), gen);
        var output = new float[Timing.Output];
        p.Convert(new float[p.ContextSamples], output, true);

        int outputFrames = Timing.Output / 480;
        // Away from the frame edges (resampler ringing), each 10 ms of output is one generated frame.
        for (int t = 0; t < outputFrames; t++)
            for (int i = 120; i < 360; i++)
                Assert.Equal(p.Frames - outputFrames + t, output[t * 480 + i], 2);
    }
}
