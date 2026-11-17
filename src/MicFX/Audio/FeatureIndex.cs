using System.IO;
using System.Numerics;
using System.Text;

namespace MicFX.Audio;

/// <summary>
/// An RVC feature index (the .index file next to a voice model): the content
/// features of the model's training recordings, in a faiss IndexIVFFlat with
/// L2 distance. RVC's "index rate" pulls each new feature towards a weighted
/// average of its 8 nearest training features (weights 1/distance², distance
/// being faiss's squared L2), which makes the voice sound more like the
/// model's own recordings. This reads the faiss file format directly and
/// searches as faiss does (the nprobe nearest clusters, saved in the file).
/// Worker thread only; allocation-free after the first call.
/// </summary>
public sealed class FeatureIndex
{
    public const int Neighbours = 8;

    private readonly float[] centroids; // nlist x dim
    private readonly float[] centroidNorm;
    private readonly float[] vectors;   // ntotal x dim, grouped by list
    private readonly int[] listStart;   // nlist + 1 offsets into vectors (in vectors)
    private readonly int nprobe;
    private readonly float[] bestDist = new float[Neighbours];
    private readonly int[] bestRow = new int[Neighbours];
    private float[] queryNorm = Array.Empty<float>();
    private float[] probeDist = Array.Empty<float>(); // queries x nprobe, ascending
    private int[] probeList = Array.Empty<int>();
    private float[] mix = Array.Empty<float>();

    private FeatureIndex(int dim, float[] centroids, float[] vectors, int[] listStart, int nprobe)
    {
        Dim = dim;
        this.centroids = centroids;
        this.vectors = vectors;
        this.listStart = listStart;
        this.nprobe = Math.Clamp(nprobe, 1, listStart.Length - 1);
        int lists = listStart.Length - 1;
        centroidNorm = new float[lists];
        for (int c = 0; c < lists; c++)
        {
            var v = centroids.AsSpan(c * dim, dim);
            centroidNorm[c] = Dot(v, v);
        }
    }

    public int Dim { get; }
    public int Count => vectors.Length / Dim;
    public int Lists => listStart.Length - 1;
    public int Probes => nprobe;

    public static FeatureIndex Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(stream);
        string name = Path.GetFileName(path);
        Expect(r, "IwFl", name, "is not an RVC index (a faiss IVF index with flat vectors)");
        var (dim, ntotal, metric) = ReadHeader(r, name);
        if (metric != 1) throw new InvalidDataException($"{name}: unsupported distance (faiss metric {metric}).");
        long nlist = r.ReadInt64();
        long nprobe = r.ReadInt64();
        if (nlist <= 0 || nlist > 1 << 24) throw new InvalidDataException($"{name}: bad cluster count {nlist}.");

        Expect(r, "IxF2", name, "has an unsupported cluster index");
        var (qdim, qtotal, _) = ReadHeader(r, name);
        if (qdim != dim || qtotal != nlist) throw new InvalidDataException($"{name}: cluster index does not match.");
        long floats = r.ReadInt64();
        if (floats != nlist * dim) throw new InvalidDataException($"{name}: cluster centres have the wrong size.");
        var centroids = ReadFloats(r, (int)floats);

        r.ReadByte();                        // direct map type
        long mapped = r.ReadInt64();         // direct map entries
        stream.Seek(mapped * 8, SeekOrigin.Current);

        Expect(r, "ilar", name, "has unsupported inverted lists");
        if (r.ReadInt64() != nlist) throw new InvalidDataException($"{name}: list count does not match.");
        long codeSize = r.ReadInt64();
        if (codeSize != dim * 4L) throw new InvalidDataException($"{name}: vectors are not stored as plain floats.");
        string kind = Encoding.ASCII.GetString(r.ReadBytes(4));
        long entries = r.ReadInt64();
        var sizes = new long[nlist];
        if (kind == "full")
        {
            if (entries != nlist) throw new InvalidDataException($"{name}: bad list sizes.");
            for (int i = 0; i < nlist; i++) sizes[i] = r.ReadInt64();
        }
        else if (kind == "sprs")
        {
            for (long i = 0; i < entries / 2; i++)
            {
                long list = r.ReadInt64(), size = r.ReadInt64();
                if (list < 0 || list >= nlist) throw new InvalidDataException($"{name}: bad list number.");
                sizes[list] = size;
            }
        }
        else
        {
            throw new InvalidDataException($"{name}: unsupported list layout \"{kind}\".");
        }
        if (sizes.Sum() != ntotal) throw new InvalidDataException($"{name}: list sizes do not add up.");
        if (ntotal * dim > Array.MaxLength) throw new InvalidDataException($"{name}: too large.");

        var vectors = new float[ntotal * dim];
        var listStart = new int[nlist + 1];
        long at = 0;
        for (int i = 0; i < nlist; i++)
        {
            listStart[i] = (int)at;
            long n = sizes[i];
            if (n == 0) continue;
            ReadFloats(r, vectors.AsSpan((int)(at * dim), (int)(n * dim)));
            stream.Seek(n * 8, SeekOrigin.Current); // ids: RVC only uses the vectors
            at += n;
        }
        listStart[nlist] = (int)at;
        return new FeatureIndex((int)dim, centroids, vectors, listStart, (int)nprobe);
    }

    /// <summary>
    /// Replaces each of the <c>rows.Length / Dim</c> features in <paramref name="rows"/> by
    /// rate x (weighted average of its nearest training features) + (1 - rate) x itself, as
    /// RVC does. All rows are compared with each cluster centre while it is in the cache.
    /// </summary>
    public void Blend(Span<float> rows, float rate)
    {
        int count = rows.Length / Dim;
        if (rate <= 0f || count == 0) return;
        if (queryNorm.Length < count)
        {
            queryNorm = new float[count];
            probeDist = new float[count * nprobe];
            probeList = new int[count * nprobe];
        }
        if (mix.Length < Dim) mix = new float[Dim];

        for (int q = 0; q < count; q++)
        {
            var row = rows.Slice(q * Dim, Dim);
            queryNorm[q] = Dot(row, row);
        }
        probeDist.AsSpan(0, count * nprobe).Fill(float.PositiveInfinity);

        // Nearest clusters (squared L2 = |q|^2 + |c|^2 - 2 q.c).
        for (int c = 0; c < centroidNorm.Length; c++)
        {
            var centre = centroids.AsSpan(c * Dim, Dim);
            float cn = centroidNorm[c];
            for (int q = 0; q < count; q++)
            {
                float d = queryNorm[q] + cn - 2f * Dot(rows.Slice(q * Dim, Dim), centre);
                int b = q * nprobe;
                if (d >= probeDist[b + nprobe - 1]) continue;
                int i = nprobe - 1;
                while (i > 0 && probeDist[b + i - 1] > d)
                {
                    probeDist[b + i] = probeDist[b + i - 1];
                    probeList[b + i] = probeList[b + i - 1];
                    i--;
                }
                probeDist[b + i] = d;
                probeList[b + i] = c;
            }
        }

        for (int q = 0; q < count; q++) BlendOne(rows.Slice(q * Dim, Dim), probeList.AsSpan(q * nprobe, nprobe), rate);
    }

    private void BlendOne(Span<float> query, ReadOnlySpan<int> lists, float rate)
    {
        // Nearest vectors in the probed lists.
        Array.Fill(bestDist, float.PositiveInfinity);
        Array.Fill(bestRow, -1);
        foreach (int list in lists)
        {
            for (int row = listStart[list]; row < listStart[list + 1]; row++)
            {
                float d = L2(query, vectors.AsSpan(row * Dim, Dim));
                if (d >= bestDist[Neighbours - 1]) continue;
                int i = Neighbours - 1;
                while (i > 0 && bestDist[i - 1] > d)
                {
                    bestDist[i] = bestDist[i - 1];
                    bestRow[i] = bestRow[i - 1];
                    i--;
                }
                bestDist[i] = d;
                bestRow[i] = row;
            }
        }

        double total = 0;
        for (int i = 0; i < Neighbours; i++)
            if (bestRow[i] >= 0) total += Weight(bestDist[i]);
        if (total <= 0) return;

        var sum = mix.AsSpan(0, Dim);
        sum.Clear();
        for (int i = 0; i < Neighbours; i++)
        {
            if (bestRow[i] < 0) continue;
            float w = (float)(Weight(bestDist[i]) / total);
            var v = vectors.AsSpan(bestRow[i] * Dim, Dim);
            for (int j = 0; j < Dim; j++) sum[j] += w * v[j];
        }
        for (int j = 0; j < Dim; j++) query[j] = sum[j] * rate + query[j] * (1f - rate);
    }

    private static double Weight(float squaredDistance)
    {
        double inv = 1.0 / Math.Max(squaredDistance, 1e-12f);
        return inv * inv;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - w; i += w) acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static float L2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - w; i += w)
        {
            var d = new Vector<float>(a.Slice(i, w)) - new Vector<float>(b.Slice(i, w));
            acc += d * d;
        }
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }

    private static void Expect(BinaryReader r, string fourcc, string name, string problem)
    {
        var got = r.ReadBytes(4);
        if (got.Length != 4 || Encoding.ASCII.GetString(got) != fourcc) throw new InvalidDataException($"{name} {problem}.");
    }

    private static (long Dim, long Total, int Metric) ReadHeader(BinaryReader r, string name)
    {
        int dim = r.ReadInt32();
        long total = r.ReadInt64();
        r.ReadInt64();
        r.ReadInt64();
        r.ReadByte(); // is_trained
        int metric = r.ReadInt32();
        if (metric > 1) r.ReadSingle();
        if (dim <= 0 || dim > 4096 || total < 0) throw new InvalidDataException($"{name}: bad index header.");
        return (dim, total, metric);
    }

    private static float[] ReadFloats(BinaryReader r, int count)
    {
        var data = new float[count];
        ReadFloats(r, data);
        return data;
    }

    private static void ReadFloats(BinaryReader r, Span<float> dest)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest);
        while (bytes.Length > 0)
        {
            int n = r.Read(bytes);
            if (n <= 0) throw new EndOfStreamException("The index file ends early.");
            bytes = bytes[n..];
        }
    }
}
