using MicFX.Audio;
using Xunit;

namespace MicFX.Tests;

/// <summary>
/// The RVC feature index reader and search, against faiss itself: the fixtures are
/// faiss IndexIVFFlat files and RVC's blend of 20 queries computed with faiss
/// (tools/make_test_models.py).
/// </summary>
public class FeatureIndexTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static float[] Floats(string name) => TestSignals.ReadF32(Path.Combine(Fixtures, name));

    [Theory]
    [InlineData("ivf-full", 300, 8, 1)]     // every list used: faiss's "full" layout
    [InlineData("ivf-sparse", 75, 64, 2)]   // most lists empty: faiss's "sprs" layout, two probes
    public void BlendMatchesRvcWithFaiss(string name, int count, int lists, int probes)
    {
        var index = FeatureIndex.Load(Path.Combine(Fixtures, name + ".index"));
        Assert.Equal(16, index.Dim);
        Assert.Equal(count, index.Count);
        Assert.Equal(lists, index.Lists);
        Assert.Equal(probes, index.Probes);

        var queries = Floats("ivf-queries.f32");
        var expected = Floats(name + ".expected.f32");
        // One at a time and all together give the same.
        var batch = queries.ToArray();
        index.Blend(batch, 0.75f);
        for (int q = 0; q < 20; q++)
        {
            var feature = queries.AsSpan(q * 16, 16).ToArray();
            index.Blend(feature, 0.75f);
            for (int j = 0; j < 16; j++)
            {
                Assert.True(Math.Abs(feature[j] - expected[q * 16 + j]) < 1e-4f, $"query {q}, value {j}: {feature[j]} vs {expected[q * 16 + j]}");
                Assert.Equal(feature[j], batch[q * 16 + j]);
            }
        }
    }

    [Fact]
    public void RateZero_LeavesTheFeature()
    {
        var index = FeatureIndex.Load(Path.Combine(Fixtures, "ivf-full.index"));
        var feature = Floats("ivf-queries.f32").AsSpan(0, 16).ToArray();
        var before = feature.ToArray();
        index.Blend(feature, 0f);
        Assert.Equal(before, feature);
    }

    [Fact]
    public void OtherFiles_AreRejectedWithAReason()
    {
        var ex = Assert.Throws<InvalidDataException>(() => FeatureIndex.Load(Path.Combine(Fixtures, "gen-micfx.onnx")));
        Assert.Contains("not an RVC index", ex.Message);
    }

    /// <summary>A real RVC index against faiss, when MICFX_INDEX_CHECK names "index.index;queries.f32;expected.f32".</summary>
    [Fact]
    public void RealIndex_WhenAvailable()
    {
        string? spec = Environment.GetEnvironmentVariable("MICFX_INDEX_CHECK");
        if (spec == null) return;
        var parts = spec.Split(';');
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = FeatureIndex.Load(parts[0]);
        long loadMs = sw.ElapsedMilliseconds;
        var queries = TestSignals.ReadF32(parts[1]);
        var expected = TestSignals.ReadF32(parts[2]);
        int n = queries.Length / index.Dim;
        double worst = 0;
        var all = queries.ToArray();
        index.Blend(all.AsSpan(0, 20 * index.Dim), 0.75f); // warm up
        all = queries.ToArray();
        sw.Restart();
        index.Blend(all, 0.75f);
        for (int i = 0; i < all.Length; i++) worst = Math.Max(worst, Math.Abs(all[i] - expected[i]));
        Console.WriteLine($"{index.Count} vectors, {index.Lists} lists, nprobe {index.Probes}; load {loadMs} ms; " +
                          $"{sw.Elapsed.TotalMilliseconds / n:F2} ms per frame; worst difference {worst:E2}");
        Assert.True(worst < 1e-3);
    }
}
