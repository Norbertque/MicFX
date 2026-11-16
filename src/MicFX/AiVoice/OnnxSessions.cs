using Microsoft.ML.OnnxRuntime;

namespace MicFX.AiVoice;

/// <summary>Where the AI voice models run.</summary>
public enum AiBackend
{
    /// <summary>NVIDIA GPU through CUDA (operators CUDA lacks fall back to the CPU).</summary>
    Cuda,
    Cpu,
}

/// <summary>Session settings shared by the three AI voice models.</summary>
public static class OnnxSessions
{
    /// <summary>
    /// Options for one model session. CPU threads are limited and never spin while
    /// idle, so the models do not compete with the audio thread or a game for CPU.
    /// </summary>
    public static SessionOptions Create(AiBackend backend, int cpuThreads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Max(1, cpuThreads),
            InterOpNumThreads = 1,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (backend == AiBackend.Cuda)
        {
            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = "0",
                // Exhaustive search runs every new shape many times; heuristics are enough here.
                ["cudnn_conv_algo_search"] = "HEURISTIC",
                // Grow GPU memory only by what is needed, to leave room for games.
                ["arena_extend_strategy"] = "kSameAsRequested",
            });
            options.AppendExecutionProvider_CUDA(cuda);
        }
        return options;
    }

    /// <summary>CPU threads to give the models: half the logical processors, at most 4.</summary>
    public static int DefaultCpuThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    internal static OrtValue Tensor<T>(T[] data, params long[] shape) where T : unmanaged =>
        OrtValue.CreateTensorValueFromMemory(data, shape);
}
