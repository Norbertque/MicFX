using System.IO;
using MicFX.Audio;
using Microsoft.ML.OnnxRuntime;

namespace MicFX.AiVoice;

/// <summary>
/// ContentVec exported by tools/export_contentvec.py: input "audio" [1, samples]
/// at 16 kHz, outputs "feats768" and "feats256" [1, frames, dim].
/// </summary>
public sealed class OnnxContentEncoder : IContentEncoder, IDisposable
{
    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private float[] input = Array.Empty<float>();
    private OrtValue? inputValue;
    private readonly string[] inputNames = { "audio" };
    private readonly string[] out768 = { "feats768" };
    private readonly string[] out256 = { "feats256" };

    public OnnxContentEncoder(string path, SessionOptions options)
    {
        session = new InferenceSession(path, options);
        foreach (var name in new[] { "audio" })
            if (!session.InputMetadata.ContainsKey(name)) throw new InvalidDataException($"{Path.GetFileName(path)} is not the MicFX content encoder.");
        foreach (var name in new[] { "feats768", "feats256" })
            if (!session.OutputMetadata.ContainsKey(name)) throw new InvalidDataException($"{Path.GetFileName(path)} is not the MicFX content encoder.");
    }

    public void Encode(ReadOnlySpan<float> audio16k, int dim, Span<float> features)
    {
        if (input.Length != audio16k.Length)
        {
            inputValue?.Dispose();
            input = new float[audio16k.Length];
            inputValue = OnnxSessions.Tensor(input, 1, input.Length);
        }
        audio16k.CopyTo(input);
        using var outputs = session.Run(run, inputNames, new[] { inputValue! }, dim == 256 ? out256 : out768);
        var result = outputs[0].GetTensorDataAsSpan<float>();
        if (result.Length < features.Length)
            throw new InvalidOperationException($"Content encoder returned {result.Length} values, expected {features.Length}.");
        result.Slice(0, features.Length).CopyTo(features);
    }

    public void Dispose()
    {
        run.Terminate = true;
        inputValue?.Dispose();
        session.Dispose();
        run.Dispose();
    }
}

/// <summary>
/// FCPE exported by tools/export_fcpe.py: input "audio" [1, samples] at 16 kHz,
/// output "f0" [1, samples / 160 + 1] in Hz (0 = unvoiced).
/// </summary>
public sealed class OnnxPitchExtractor : IPitchExtractor, IDisposable
{
    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private float[] input = Array.Empty<float>();
    private OrtValue? inputValue;
    private readonly string[] inputNames = { "audio" };
    private readonly string[] outputNames = { "f0" };

    public OnnxPitchExtractor(string path, SessionOptions options)
    {
        session = new InferenceSession(path, options);
        if (!session.InputMetadata.ContainsKey("audio") || !session.OutputMetadata.ContainsKey("f0"))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not the MicFX pitch extractor.");
    }

    public void Extract(ReadOnlySpan<float> audio16k, Span<float> f0)
    {
        if (input.Length != audio16k.Length)
        {
            inputValue?.Dispose();
            input = new float[audio16k.Length];
            inputValue = OnnxSessions.Tensor(input, 1, input.Length);
        }
        audio16k.CopyTo(input);
        using var outputs = session.Run(run, inputNames, new[] { inputValue! }, outputNames);
        var result = outputs[0].GetTensorDataAsSpan<float>();
        if (result.Length != f0.Length)
            throw new InvalidOperationException($"Pitch extractor returned {result.Length} frames, expected {f0.Length}.");
        result.CopyTo(f0);
    }

    public void Dispose()
    {
        run.Terminate = true;
        inputValue?.Dispose();
        session.Dispose();
        run.Dispose();
    }
}
