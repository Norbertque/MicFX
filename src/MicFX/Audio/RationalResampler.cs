namespace MicFX.Audio;

/// <summary>
/// Resamples a whole buffer by a rational ratio with a Kaiser-windowed sinc,
/// zero-phase: output sample m lies at input time m * from / to, so there is
/// no delay to compensate. Samples outside the buffer count as zero. Used by
/// the AI voice worker (not the audio thread); allocation-free after construction.
/// </summary>
public sealed class RationalResampler
{
    private readonly int up, down, halfLength;
    private readonly float[] kernel; // indexed by upsampled offset + halfLength

    public RationalResampler(int fromRate, int toRate, int zeroCrossings = 16, double rolloff = 0.94, double kaiserBeta = 8.6)
    {
        int g = Gcd(fromRate, toRate);
        up = toRate / g;
        down = fromRate / g;
        FromRate = fromRate;
        ToRate = toRate;

        // Lowpass at the upsampled rate, below the lower of the two Nyquist frequencies.
        double cutoff = 0.5 / Math.Max(up, down) * rolloff; // cycles per upsampled sample
        halfLength = (int)Math.Ceiling(zeroCrossings / (2 * cutoff));
        kernel = new float[2 * halfLength + 1];
        double i0Beta = BesselI0(kaiserBeta);
        for (int n = -halfLength; n <= halfLength; n++)
        {
            double x = 2 * cutoff * n;
            double sinc = n == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = (double)n / halfLength;
            double window = BesselI0(kaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            kernel[n + halfLength] = (float)(2 * cutoff * sinc * window * up);
        }
    }

    public int FromRate { get; }
    public int ToRate { get; }

    /// <summary>Output length for an input of <paramref name="inputLength"/> samples.</summary>
    public int OutputLength(int inputLength) => (int)((long)inputLength * up / down);

    /// <summary>Resamples <paramref name="input"/> into <paramref name="output"/> (OutputLength samples).</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (up == down)
        {
            input.Slice(0, output.Length).CopyTo(output);
            return;
        }
        int n = input.Length;
        for (int m = 0; m < output.Length; m++)
        {
            long centre = (long)m * down; // in upsampled samples
            long kMin = Math.Max(0, CeilDiv(centre - halfLength, up));
            long kMax = Math.Min(n - 1, FloorDiv(centre + halfLength, up));
            float sum = 0;
            for (long k = kMin; k <= kMax; k++)
                sum += input[(int)k] * kernel[(int)(centre - k * up) + halfLength];
            output[m] = sum;
        }
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-12) break;
        }
        return sum;
    }
}
