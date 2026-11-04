using MicFX.Audio;
using NAudio.Wave;
using Xunit;

namespace MicFX.Tests;

/// <summary>Leading-silence detection for soundboard clips, on real WAV files.</summary>
public class ClipAnalysisTests
{
    private static float[] Silence(double seconds, int rate) => new float[(int)(seconds * rate)];

    private static float[] Tone(double seconds, int rate, double dbfs = -20, double hz = 440)
    {
        float a = (float)Math.Pow(10, dbfs / 20);
        var x = new float[(int)(seconds * rate)];
        for (int i = 0; i < x.Length; i++) x[i] = a * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return x;
    }

    private static float[] Noise(double seconds, int rate, float amplitude, int seed = 3)
    {
        var rng = new Random(seed);
        var x = new float[(int)(seconds * rate)];
        for (int i = 0; i < x.Length; i++) x[i] = amplitude * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    private static ClipAnalysis Analyze(int rate, params float[][] parts)
    {
        string path = Path.Combine(Path.GetTempPath(), $"micfx-clip-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
                foreach (var part in parts) w.WriteSamples(part, 0, part.Length);
            return LoudnessMeter.AnalyzeFile(path) ?? throw new Xunit.Sdk.XunitException("unreadable");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SilentLeadIn_IsSkipped_KeepingTheAttack()
    {
        var r = Analyze(48000, Silence(0.5, 48000), Tone(3, 48000));
        Assert.InRange(r.StartSeconds, 0.478, 0.482); // 0.5 s minus the 20 ms pre-roll
    }

    [Fact]
    public void SilentLeadIn_DoesNotChangeLoudness()
    {
        var withLeadIn = Analyze(48000, Silence(0.5, 48000), Tone(3, 48000));
        var toneOnly = Analyze(48000, Tone(3, 48000));
        Assert.NotNull(toneOnly.Lufs);
        Assert.InRange(withLeadIn.Lufs!.Value, toneOnly.Lufs!.Value - 0.5, toneOnly.Lufs.Value + 0.5);
    }

    [Fact]
    public void SoundFromTheFirstSample_StartsAtZero() =>
        Assert.Equal(0, Analyze(48000, Tone(1, 48000)).StartSeconds);

    [Fact]
    public void QuietNoiseFloorBeforeTheSound_IsSkippedToo()
    {
        // -70 dBFS hiss is below both the -60 dBFS floor and 40 dB under the peak.
        var r = Analyze(48000, Noise(0.4, 48000, 0.0003f), Tone(1, 48000));
        Assert.InRange(r.StartSeconds, 0.378, 0.382);
    }

    [Fact]
    public void ShortLeadIn_IsLeftAlone() =>
        Assert.Equal(0, Analyze(48000, Silence(0.03, 48000), Tone(1, 48000)).StartSeconds);

    [Fact]
    public void SilentClip_StartsAtZero_AndHasNoLoudness()
    {
        var r = Analyze(48000, Silence(1, 48000));
        Assert.Equal(0, r.StartSeconds);
        Assert.Null(r.Lufs);
    }

    [Fact]
    public void OtherSampleRates_AreMeasuredInSeconds()
    {
        var r = Analyze(44100, Silence(0.25, 44100), Tone(1, 44100));
        Assert.InRange(r.StartSeconds, 0.226, 0.234);
    }
}
