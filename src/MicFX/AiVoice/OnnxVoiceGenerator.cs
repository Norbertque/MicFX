using System.IO;
using System.Text.Json;
using MicFX.Audio;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MicFX.AiVoice;

/// <summary>Which export of an RVC model a file is.</summary>
public enum RvcDialect
{
    /// <summary>tools/convert_rvc.py: real-time graph with skip_head and return_length.</summary>
    MicFx,

    /// <summary>The ONNX export of older RVC WebUI versions (phone ... ds, rnd).</summary>
    WebUi,

    /// <summary>w-okada voice-changer's export (feats, p_len, pitch, pitchf, sid).</summary>
    WOkada,

    /// <summary>Applio's export (phone, phone_lengths, sid, rate, pitch, nsff0): returns the last rate x T frames.</summary>
    Applio,
}

/// <summary>
/// An RVC voice model in ONNX. Files from tools/convert_rvc.py run exactly as
/// RVC's real-time code does. Applio's export can return only the newest
/// frames (its rate input), so it is asked for the returned frames plus 24 of
/// context for the flow. The old WebUI and w-okada exports have no way to
/// skip the context, so they are given the last 24 context frames plus the
/// returned frames, and the returned part is cut from their output.
/// </summary>
public sealed class OnnxVoiceGenerator : IVoiceGenerator, IDisposable
{
    private const int WindowContextFrames = 24;
    private const float NoiseScale = 0.66666f;

    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private readonly string featureName, lengthName, speakerName, pitchName;
    private readonly bool halfFeatures, halfPitch;
    private readonly string[] outputNames;
    private ulong rng = 0x9E3779B97F4A7C15UL;

    // Inputs, created for the first shape used and kept while it stays the same.
    private int shapeFrames = -1;
    private string[] inputNames = Array.Empty<string>();
    private OrtValue[] inputValues = Array.Empty<OrtValue>();
    private float[] feats = Array.Empty<float>();
    private Float16[] featsHalf = Array.Empty<Float16>();
    private long[] pitch = Array.Empty<long>();
    private float[] pitchf = Array.Empty<float>();
    private Float16[] pitchfHalf = Array.Empty<Float16>();
    private float[] noise = Array.Empty<float>();
    private readonly long[] length = new long[1];
    private readonly long[] speaker = new long[1];
    private readonly long[] skip = new long[1];
    private readonly long[] ret = new long[1];
    private readonly float[] rate = new float[1];

    public OnnxVoiceGenerator(string path, SessionOptions options)
    {
        session = new InferenceSession(path, options);
        var inputs = session.InputMetadata;
        string file = Path.GetFileName(path);

        if (inputs.ContainsKey("phone") && inputs.ContainsKey("skip_head")) Dialect = RvcDialect.MicFx;
        else if (inputs.ContainsKey("phone") && inputs.ContainsKey("rate")) Dialect = RvcDialect.Applio;
        else if (inputs.ContainsKey("phone") && inputs.ContainsKey("rnd")) Dialect = RvcDialect.WebUi;
        else if (inputs.ContainsKey("feats") && inputs.ContainsKey("p_len")) Dialect = RvcDialect.WOkada;
        else throw new InvalidDataException($"{file} is not an RVC voice model in a known ONNX format (inputs: {string.Join(", ", inputs.Keys)}).");

        featureName = Dialect == RvcDialect.WOkada ? "feats" : "phone";
        lengthName = Dialect == RvcDialect.WOkada ? "p_len" : "phone_lengths";
        speakerName = Dialect is RvcDialect.WOkada or RvcDialect.Applio ? "sid" : "ds";
        pitchName = Dialect == RvcDialect.Applio ? "nsff0" : "pitchf";
        UsesPitch = inputs.ContainsKey(pitchName);
        halfFeatures = inputs[featureName].ElementType == typeof(Float16);
        halfPitch = UsesPitch && inputs[pitchName].ElementType == typeof(Float16);
        outputNames = new[] { session.OutputMetadata.Keys.First() };

        var meta = ReadMetadata(session);
        int dims = inputs[featureName].Dimensions.Length == 3 ? inputs[featureName].Dimensions[2] : -1;
        FeatureDim = dims is 256 or 768 ? dims : meta.EmbChannels;
        if (FeatureDim is not (256 or 768))
            throw new InvalidDataException($"{file}: cannot tell whether this is an RVC v1 or v2 model.");
        Frames = Dialect == RvcDialect.MicFx ? meta.Frames : 0;
        if (Dialect == RvcDialect.MicFx && Frames <= 0)
            throw new InvalidDataException($"{file}: the frame count is missing from the model's metadata.");
        SampleRate = meta.SamplingRate is 32000 or 40000 or 48000 ? meta.SamplingRate : ProbeSampleRate(file);
    }

    public RvcDialect Dialect { get; }
    public int SampleRate { get; }
    public int FeatureDim { get; }
    public bool UsesPitch { get; }
    public int Frames { get; }

    public void Generate(ReadOnlySpan<float> features, ReadOnlySpan<long> coarsePitch, ReadOnlySpan<float> f0,
        int skipHead, int returnFrames, Span<float> audio)
    {
        int total = features.Length / FeatureDim;
        int perFrame = SampleRate / 100;
        bool skips = Dialect is RvcDialect.MicFx or RvcDialect.Applio;
        int start = skips ? 0 : Math.Max(0, skipHead - WindowContextFrames);
        int frames = total - start;
        Prepare(frames);

        features.Slice(start * FeatureDim, frames * FeatureDim).CopyTo(feats);
        if (halfFeatures)
            for (int i = 0; i < feats.Length; i++) featsHalf[i] = (Float16)feats[i];
        if (UsesPitch)
        {
            coarsePitch.Slice(start, frames).CopyTo(pitch);
            f0.Slice(start, frames).CopyTo(pitchf);
            if (halfPitch)
                for (int i = 0; i < pitchf.Length; i++) pitchfHalf[i] = (Float16)pitchf[i];
        }
        if (Dialect is RvcDialect.MicFx or RvcDialect.WebUi) FillNoise(noise);
        skip[0] = skipHead - start;
        ret[0] = returnFrames;
        // Applio keeps the last int(T * rate) frames; ask for half a frame more so rounding never loses one.
        int wanted = total - Math.Max(0, skipHead - WindowContextFrames);
        rate[0] = Math.Min(1f, (wanted + 0.5f) / total);

        using var outputs = session.Run(run, inputNames, inputValues, outputNames);
        var result = outputs[0].GetTensorDataAsSpan<float>();
        int offset = Dialect switch
        {
            RvcDialect.MicFx => 0,
            RvcDialect.Applio => (skipHead - (total - result.Length / perFrame)) * perFrame, // output is the last frames
            _ => (skipHead - start) * perFrame,
        };
        if (offset < 0 || result.Length < offset + audio.Length)
            throw new InvalidOperationException($"The voice model returned {result.Length} samples, expected {offset + audio.Length}.");
        result.Slice(offset, audio.Length).CopyTo(audio);
    }

    private void Prepare(int frames)
    {
        if (frames == shapeFrames) return;
        foreach (var v in inputValues) v.Dispose();
        shapeFrames = frames;
        feats = new float[frames * FeatureDim];
        featsHalf = halfFeatures ? new Float16[feats.Length] : Array.Empty<Float16>();
        pitch = new long[frames];
        pitchf = new float[frames];
        pitchfHalf = halfPitch ? new Float16[frames] : Array.Empty<Float16>();
        noise = new float[192 * frames];
        length[0] = frames;

        var names = new List<string>();
        var values = new List<OrtValue>();
        void Add(string name, OrtValue value)
        {
            names.Add(name);
            values.Add(value);
        }
        Add(featureName, halfFeatures ? OnnxSessions.Tensor(featsHalf, 1, frames, FeatureDim) : OnnxSessions.Tensor(feats, 1, frames, FeatureDim));
        Add(lengthName, OnnxSessions.Tensor(length, 1));
        if (UsesPitch)
        {
            Add("pitch", OnnxSessions.Tensor(pitch, 1, frames));
            Add(pitchName, halfPitch ? OnnxSessions.Tensor(pitchfHalf, 1, frames) : OnnxSessions.Tensor(pitchf, 1, frames));
        }
        Add(speakerName, OnnxSessions.Tensor(speaker, 1));
        if (Dialect is RvcDialect.MicFx or RvcDialect.WebUi) Add("rnd", OnnxSessions.Tensor(noise, 1, 192, frames));
        if (Dialect == RvcDialect.Applio) Add("rate", OnnxSessions.Tensor(rate, 1));
        if (Dialect == RvcDialect.MicFx)
        {
            Add("skip_head", OnnxSessions.Tensor(skip, 1));
            Add("return_length", OnnxSessions.Tensor(ret, 1));
        }
        inputNames = names.ToArray();
        inputValues = values.ToArray();
    }

    /// <summary>Gaussian noise times RVC's 0.66666 (xorshift + Box-Muller, no allocation).</summary>
    private void FillNoise(float[] dest)
    {
        for (int i = 0; i < dest.Length; i += 2)
        {
            double u1 = (NextUInt() + 1.0) / 4294967297.0;
            double u2 = NextUInt() / 4294967296.0;
            double r = Math.Sqrt(-2 * Math.Log(u1)) * NoiseScale;
            dest[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < dest.Length) dest[i + 1] = (float)(r * Math.Sin(2 * Math.PI * u2));
        }
    }

    private uint NextUInt()
    {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        return (uint)(rng >> 32);
    }

    /// <summary>Exports without metadata: synthesise a few frames and count the samples.</summary>
    private int ProbeSampleRate(string file)
    {
        foreach (int frames in new[] { 200, 64 })
        {
            try
            {
                Prepare(frames);
                Array.Fill(pitch, 1);
                FillNoise(noise);
                skip[0] = 0;
                ret[0] = frames;
                rate[0] = 1f;
                using var outputs = session.Run(run, inputNames, inputValues, outputNames);
                int hz = outputs[0].GetTensorDataAsSpan<float>().Length * 100 / frames;
                if (hz is 32000 or 40000 or 48000) return hz;
            }
            catch (OnnxRuntimeException)
            {
                // Try the next length.
            }
        }
        throw new InvalidDataException($"{file}: cannot tell the model's sample rate.");
    }

    private readonly record struct Metadata(int SamplingRate, int EmbChannels, int Frames);

    /// <summary>"metadata" (convert_rvc.py, w-okada) or Applio's "model_info" (sr).</summary>
    private static Metadata ReadMetadata(InferenceSession session)
    {
        var map = session.ModelMetadata.CustomMetadataMap;
        if (!map.TryGetValue("metadata", out var json) && !map.TryGetValue("model_info", out json)) return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int Get(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;
            int rateHz = Get("samplingRate");
            if (rateHz == 0) rateHz = Get("sr");
            return new Metadata(rateHz, Get("embChannels"), Get("frames"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>Asks a running call to stop (it then throws). Safe from any thread.</summary>
    public void Cancel() => run.Terminate = true;

    public void Dispose()
    {
        run.Terminate = true;
        foreach (var v in inputValues) v.Dispose();
        session.Dispose();
        run.Dispose();
    }
}
