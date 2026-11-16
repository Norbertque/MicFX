namespace MicFX.AiVoice;

/// <summary>A file inside a downloaded archive that MicFX keeps.</summary>
public sealed record ArchiveMember(string Path, string Name, long Size, string Sha256);

/// <summary>
/// A download pinned by size and SHA-256. When <see cref="Members"/> is not
/// empty the download is a zip archive: only those members are extracted
/// (each checked again) and the archive is deleted.
/// </summary>
public sealed record PinnedFile(string Name, string Url, long Size, string Sha256, IReadOnlyList<ArchiveMember> Members)
{
    public PinnedFile(string name, string url, long size, string sha256) : this(name, url, size, sha256, Array.Empty<ArchiveMember>()) { }

    public bool IsArchive => Members.Count > 0;

    /// <summary>Bytes on disk once installed.</summary>
    public long InstalledSize => IsArchive ? Members.Sum(m => m.Size) : Size;
}

/// <summary>A set of files that is installed into one folder.</summary>
public sealed record FileSet(string Folder, string Title, IReadOnlyList<PinnedFile> Files)
{
    public long DownloadSize => Files.Sum(f => f.Size);
    public long InstalledSize => Files.Sum(f => f.InstalledSize);
}

/// <summary>
/// Everything the AI voice downloads, pinned. Nothing here is a voice:
/// voice models are files the user loads.
/// </summary>
public static class AiVoiceFiles
{
    public const string OnnxRuntimeVersion = "1.32.0";

    /// <summary>
    /// ONNX Runtime (MIT) from Microsoft's NuGet package. The GPU build's
    /// onnxruntime.dll also runs on the CPU, so one download serves both.
    /// </summary>
    public static readonly FileSet Runtime = new(
        "runtime-" + OnnxRuntimeVersion,
        "ONNX Runtime " + OnnxRuntimeVersion,
        new[]
        {
            new PinnedFile(
                "microsoft.ml.onnxruntime.gpu.windows.1.32.0.nupkg",
                "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.windows/1.32.0/microsoft.ml.onnxruntime.gpu.windows.1.32.0.nupkg",
                155209053, "485c1af4e5719d7336c2f1cdf6d3342a3fcec88d3bad9621e33a283fff77c4c7",
                new[]
                {
                    new ArchiveMember("runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll", 21034296, "482ef0ced4d3dd035e61066e8672bae02b32ab835dc49a2ffc154809c94149c6"),
                    new ArchiveMember("runtimes/win-x64/native/onnxruntime_providers_shared.dll", "onnxruntime_providers_shared.dll", 21816, "c65bec36d453ae3439c6eeac993d6c989c1328a0ae11e079c9cdd9f7fbc6eec9"),
                    new ArchiveMember("runtimes/win-x64/native/onnxruntime_providers_cuda.dll", "onnxruntime_providers_cuda.dll", 195548512, "a101b256c0dc39280542b517d911e006bde3719aaf5b3e7ad8b5af74642b967f"),
                }),
        });

    /// <summary>ContentVec and FCPE, exported by tools/ and hosted on the models-1 pre-release.</summary>
    public static readonly FileSet HelperModels = new(
        "models-1",
        "Helper models (ContentVec, FCPE)",
        new[]
        {
            new PinnedFile("contentvec.onnx", "https://github.com/Norbertque/MicFX/releases/download/models-1/contentvec.onnx",
                378517637, "b34bdb15457501ad6af99f914d553b1132f6a009cfad1df07ab2c5fe18e67e5d"),
            new PinnedFile("fcpe.onnx", "https://github.com/Norbertque/MicFX/releases/download/models-1/fcpe.onnx",
                47805607, "322965d19812b768d33ade9cf1fdcf8c484451d40bac55e3eec65d556e509366"),
        });

    private const string CudaRedist = "https://developer.download.nvidia.com/compute/cuda/redist/";
    private const string CudnnRedist = "https://developer.download.nvidia.com/compute/cudnn/redist/";

    /// <summary>
    /// NVIDIA's CUDA 13.0.3 and cuDNN 9.16 runtime libraries, downloaded from
    /// NVIDIA only when the user chooses the GPU and accepts NVIDIA's license.
    /// MicFX does not distribute them. CUDA 13.0 because ONNX Runtime 1.32 is
    /// built with it; it needs NVIDIA driver 580 or newer.
    /// </summary>
    public static readonly FileSet Cuda = new(
        "cuda-13.0-cudnn-9.16",
        "NVIDIA CUDA 13.0 and cuDNN 9.16 libraries",
        new[]
        {
            new PinnedFile("cuda_cudart-windows-x86_64-13.0.96-archive.zip",
                CudaRedist + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip",
                2873447, "a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4",
                new[]
                {
                    new ArchiveMember("cuda_cudart-windows-x86_64-13.0.96-archive/bin/x64/cudart64_13.dll", "cudart64_13.dll", 470640, "769440f43991b2af0fbc7ee69d77f6f6d60f7468f585ae7cf1fb86451a51fcb1"),
                    new ArchiveMember("cuda_cudart-windows-x86_64-13.0.96-archive/LICENSE", "LICENSE-CUDA.txt", 67875, "f176e5fd245a4ff1d7aee743e345e14b29c4b724f150e4858c4c6a313966d1fb"),
                }),
            new PinnedFile("libcublas-windows-x86_64-13.1.1.3-archive.zip",
                CudaRedist + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.1.3-archive.zip",
                403660026, "d6dfb0f7ad09730bafa1fd948bc65472e28c8a6dc30b4a2b32191f2380f9282b",
                new[]
                {
                    new ArchiveMember("libcublas-windows-x86_64-13.1.1.3-archive/bin/x64/cublas64_13.dll", "cublas64_13.dll", 51561584, "b787fada026a2cfe3eb07fe6f15b73b7a2aec6083a3cbc9584eb006737b980d6"),
                    new ArchiveMember("libcublas-windows-x86_64-13.1.1.3-archive/bin/x64/cublasLt64_13.dll", "cublasLt64_13.dll", 480399472, "5d9ef9e66b68713f2b2a9cd6f0219f20f458e5d36e893fe34fe483bbcd68a744"),
                }),
            new PinnedFile("cuda_nvrtc-windows-x86_64-13.0.88-archive.zip",
                CudaRedist + "cuda_nvrtc/windows-x86_64/cuda_nvrtc-windows-x86_64-13.0.88-archive.zip",
                318076161, "8c50a52467826167e0dbe99936140c52d62272bfc5849fe2d6587d050c8c5d29",
                new[]
                {
                    new ArchiveMember("cuda_nvrtc-windows-x86_64-13.0.88-archive/bin/x64/nvrtc64_130_0.dll", "nvrtc64_130_0.dll", 90965024, "28d1021a82003f4aed742ea36ece1d595e32a107f915dfbd07d3ee8764858bce"),
                    new ArchiveMember("cuda_nvrtc-windows-x86_64-13.0.88-archive/bin/x64/nvrtc-builtins64_130.dll", "nvrtc-builtins64_130.dll", 4471328, "cd4ce3e0351b0d10f43b07c667782028420e03858999fab65207c40a28546d19"),
                }),
            new PinnedFile("cudnn-windows-x86_64-9.16.0.29_cuda13-archive.zip",
                CudnnRedist + "cudnn/windows-x86_64/cudnn-windows-x86_64-9.16.0.29_cuda13-archive.zip",
                335375136, "606c405a46e55bec01be8dd81092d238900f4028fee10a7ed1bc32cd5e23714e",
                new[]
                {
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn64_9.dll", "cudnn64_9.dll", 274544, "d896089a27dcc373921eeb058c5cb5d6b7bbe175ec66b82377df3466d318e241"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_graph64_9.dll", "cudnn_graph64_9.dll", 2411632, "f74c1b2b672ad6711e0cea9b509f1c3b2eae9df7843ef6cb57e257a60ab4d731"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_ops64_9.dll", "cudnn_ops64_9.dll", 35023472, "8ad4a9d2255e2d343567ab50d11834902889317497e8dffdb0b3b70a4ca49d6f"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_cnn64_9.dll", "cudnn_cnn64_9.dll", 1516656, "4144f7702f2f84809fb5bb75f2f42ae98ddd53ed535e6e5241ba0a20ef42e950"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_adv64_9.dll", "cudnn_adv64_9.dll", 104883312, "185087a12aa9d53761ff0edb5a67807b2bde921a11cf0698533d262a17e2944f"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_heuristic64_9.dll", "cudnn_heuristic64_9.dll", 54163056, "b6d2436aa0674b79a4273653c412c5af89ce2d780fd847ced3faff0cd151b2f3"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_engines_precompiled64_9.dll", "cudnn_engines_precompiled64_9.dll", 196692080, "c58b4ac0d5685a55e6da0fbdd9959518cb4de2751d5a53622ef3e8c4ebee897c"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/bin/cudnn_engines_runtime_compiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll", 23422576, "b80de2a3e66248dc6157ccf35f95c165eb2a139987a0ebe98725bcfb20969157"),
                    new ArchiveMember("cudnn-windows-x86_64-9.16.0.29_cuda13-archive/LICENSE", "LICENSE-cuDNN.txt", 29574, "295a1b7118d3417f5d6a943e5f87b59e2a0631fb277129c3b15dee6248adc7cf"),
                }),
        });

    /// <summary>NVIDIA's license for the CUDA and cuDNN libraries, shown before they are downloaded.</summary>
    public const string CudaLicenseUrl = "https://docs.nvidia.com/cuda/eula/index.html";
    public const string CudnnLicenseUrl = "https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html";

    /// <summary>Order in which the NVIDIA libraries are loaded by full path (dependencies first).</summary>
    public static readonly string[] CudaLoadOrder =
    {
        "cudart64_13.dll", "nvrtc-builtins64_130.dll", "nvrtc64_130_0.dll", "cublasLt64_13.dll", "cublas64_13.dll",
        "cudnn_graph64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_heuristic64_9.dll",
        "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll", "cudnn64_9.dll",
    };
}
