using MicFX.Audio;
using Xunit;

namespace MicFX.Tests;

public class PeakLimiterTests
{
    private const int Rate = TestSignals.Rate;
    private static readonly float Ceiling = MathF.Pow(10f, PeakLimiterSampleProvider.CeilingDb / 20f);

    [Fact]
    public void LoudSine_NeverExceedsCeiling_AndIsNotOverLimited()
    {
        var lim = new PeakLimiterSampleProvider(TestSignals.Mono(n => 2f * (float)Math.Sin(2 * Math.PI * 440 * n / Rate))); // +6 dBFS
        var y = TestSignals.ReadAll(lim, Rate * 2);
        float peak = y.Skip(Rate / 2).Max(Math.Abs);
        Assert.True(y.All(v => Math.Abs(v) <= Ceiling + 1e-6f), $"peak {y.Max(Math.Abs)}");
        Assert.True(peak > Ceiling * 0.95f, $"over-limited to {peak}");
        Assert.True(lim.ConsumeMaxGainReductionDb() > 5f);
    }

    [Fact]
    public void Transients_NeverExceedCeiling()
    {
        var rng = new Random(9);
        var x = new float[Rate * 3];
        for (int i = 0; i < x.Length; i++) x[i] = 0.1f * (float)(rng.NextDouble() * 2 - 1);
        for (int k = 0; k < 200; k++) x[rng.Next(x.Length)] = (float)(rng.NextDouble() * 8 - 4); // spikes to ±4
        var lim = new PeakLimiterSampleProvider(TestSignals.FromArray(x));
        var y = TestSignals.ReadAll(lim, x.Length);
        Assert.True(y.All(v => Math.Abs(v) <= Ceiling + 1e-6f));
    }

    [Fact]
    public void QuietSignal_PassesBitExact_AfterLookaheadDelay()
    {
        var x = new float[Rate];
        for (int i = 0; i < x.Length; i++) x[i] = 0.1f * (float)Math.Sin(2 * Math.PI * 300 * i / Rate); // -20 dBFS
        var lim = new PeakLimiterSampleProvider(TestSignals.FromArray(x));
        var y = TestSignals.ReadAll(lim, x.Length);
        int d = lim.LatencySamples;
        Assert.Equal(48, d);
        for (int i = 0; i < d; i++) Assert.Equal(0f, y[i]);
        for (int i = d; i < y.Length; i++) Assert.Equal(x[i - d], y[i]);
        Assert.Equal(0f, lim.ConsumeMaxGainReductionDb());
    }

    [Fact]
    public void StereoGainIsLinked()
    {
        // Loud left, quiet right: both reduced by the same gain, so the ratio holds.
        var lim = new PeakLimiterSampleProvider(TestSignals.Stereo(n =>
        {
            float s = (float)Math.Sin(2 * Math.PI * 250 * n / Rate);
            return (2f * s, 0.2f * s);
        }));
        var y = TestSignals.ReadAll(lim, Rate * 2 * 2);
        for (int i = Rate * 2; i + 1 < y.Length; i += 2)
            if (Math.Abs(y[i]) > 0.05f && Math.Abs(y[i]) < Ceiling * 0.999f)
                Assert.Equal(10f, y[i] / y[i + 1], 2);
    }

    /// <summary>
    /// The limiter only scans its lookahead window while a peak in it is over
    /// the ceiling. Output must match the straightforward version (scan every
    /// frame) sample for sample, on stereo with bursts below, at and over the
    /// ceiling.
    /// </summary>
    [Fact]
    public void SkippingTheWindowScan_ChangesNothing()
    {
        var rng = new Random(11);
        var x = new float[Rate * 4 * 2];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / 2.0 / Rate;
            float level = (int)(t * 3) % 4 switch { 0 => 0.2f, 1 => 0.9f, 2 => 1.6f, _ => 3f };
            x[i] = level * (float)(rng.NextDouble() * 2 - 1);
        }
        var fast = TestSignals.ReadAll(new PeakLimiterSampleProvider(TestSignals.FromArray(x, 2)), x.Length);
        var reference = ReferenceLimiter(x, 2);
        Assert.Equal(reference.Length, fast.Length);
        for (int i = 0; i < reference.Length; i++)
            Assert.True(reference[i] == fast[i], $"sample {i}: {fast[i]} vs {reference[i]}");
    }

    /// <summary>The limiter as it was before: the window maximum computed on every frame.</summary>
    private static float[] ReferenceLimiter(float[] x, int channels)
    {
        int lookahead = Math.Max(1, (int)(1f * Rate / 1000f));
        float attack = MathF.Exp(-1f / (0.2f * Rate / 1000f));
        float release = MathF.Exp(-1f / (80f * Rate / 1000f));
        var delay = new float[lookahead * channels];
        var peaks = new float[lookahead + 1];
        int delayPos = 0, peakPos = 0;
        float gain = 1f;
        var y = new float[x.Length];
        for (int f = 0; f + channels <= x.Length; f += channels)
        {
            float peak = 0f;
            for (int ch = 0; ch < channels; ch++) peak = MathF.Max(peak, MathF.Abs(x[f + ch]));
            peaks[peakPos] = peak;
            peakPos = (peakPos + 1) % peaks.Length;
            float windowMax = 0f;
            foreach (var p in peaks) windowMax = MathF.Max(windowMax, p);
            float target = windowMax > Ceiling ? Ceiling / windowMax : 1f;
            float coef = target < gain ? attack : release;
            gain = target + (gain - target) * coef;
            int d = delayPos * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                float delayed = delay[d + ch];
                delay[d + ch] = x[f + ch];
                y[f + ch] = Math.Clamp(delayed * gain, -Ceiling, Ceiling);
            }
            delayPos = (delayPos + 1) % lookahead;
        }
        return y;
    }
}
