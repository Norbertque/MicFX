using MicFX.Audio;
using Xunit;

namespace MicFX.Tests;

public class RationalResamplerTests
{
    private static float[] Tone(int rate, double hz, int n) =>
        Enumerable.Range(0, n).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    [Theory]
    [InlineData(48000, 16000, 1000.0)]
    [InlineData(48000, 16000, 6500.0)]
    [InlineData(40000, 48000, 1000.0)]
    [InlineData(40000, 48000, 15000.0)]
    [InlineData(32000, 48000, 3000.0)]
    public void PassbandTone_KeepsLevelAndPhase(int from, int to, double hz)
    {
        var r = new RationalResampler(from, to);
        var input = Tone(from, hz, from / 2);
        var output = new float[r.OutputLength(input.Length)];
        r.Process(input, output);
        // Zero-phase: output sample m is the tone at input time m * from / to (edges excluded).
        double maxErr = 0;
        for (int m = output.Length / 4; m < output.Length * 3 / 4; m++)
        {
            double expected = 0.5 * Math.Sin(2 * Math.PI * hz * m / to);
            maxErr = Math.Max(maxErr, Math.Abs(output[m] - expected));
        }
        Assert.True(maxErr < 0.5 * 0.006, $"max error {maxErr}"); // within 0.05 dB and no delay
    }

    [Fact]
    public void Decimation_RemovesWhatCannotBeRepresented()
    {
        var r = new RationalResampler(48000, 16000);
        var input = Tone(48000, 11000, 24000);
        var output = new float[r.OutputLength(input.Length)];
        r.Process(input, output);
        double rms = Math.Sqrt(output.Skip(2000).Take(4000).Select(v => (double)v * v).Average());
        Assert.True(20 * Math.Log10(rms / (0.5 / Math.Sqrt(2))) < -60, $"leak {20 * Math.Log10(rms / 0.354)} dB");
    }

    [Fact]
    public void SameRate_IsACopy()
    {
        var r = new RationalResampler(48000, 48000);
        var input = Tone(48000, 440, 1000);
        var output = new float[1000];
        r.Process(input, output);
        Assert.Equal(input, output);
    }
}
