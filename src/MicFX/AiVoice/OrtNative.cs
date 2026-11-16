using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace MicFX.AiVoice;

/// <summary>
/// Loads the downloaded ONNX Runtime and NVIDIA libraries by full path, so a
/// different copy elsewhere on the system (Windows ships an old
/// onnxruntime.dll in System32) is never picked up.
/// </summary>
public static class OrtNative
{
    private static readonly object Gate = new();
    private static string? runtimeFolder;
    private static string? cudaFolder;

    /// <summary>Folder the ONNX Runtime library was loaded from, once set.</summary>
    public static string? RuntimeFolder => runtimeFolder;

    /// <summary>
    /// Makes ONNX Runtime load from <paramref name="folder"/>. Must run before any ONNX
    /// Runtime type is used; a process can only use one runtime folder.
    /// </summary>
    public static void UseRuntime(string folder)
    {
        lock (Gate)
        {
            if (runtimeFolder != null)
            {
                if (!string.Equals(runtimeFolder, folder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ONNX Runtime is already loaded from another folder; restart MicFX.");
                return;
            }
            string? missing = MissingVisualCppRuntime();
            if (missing != null)
                throw new DllNotFoundException($"The Microsoft Visual C++ Redistributable (x64) is not installed ({missing} is missing). " +
                                               "Install it from https://aka.ms/vs/17/release/vc_redist.x64.exe and try again.");
            OrtEnv.DisableDllImportResolver = true;
            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, Resolve);
            // Load it now so a broken file is reported here, by name.
            NativeLibrary.Load(Path.Combine(folder, "onnxruntime.dll"));
            runtimeFolder = folder;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? folder = runtimeFolder;
        if (folder == null) return IntPtr.Zero;
        if (name.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase) || name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
            return NativeLibrary.Load(Path.Combine(folder, "onnxruntime.dll"));
        return IntPtr.Zero;
    }

    /// <summary>
    /// Prepares the NVIDIA libraries in <paramref name="folder"/> for the CUDA provider:
    /// checks the driver, puts the folder first on the PATH and loads every library by
    /// full path. Returns null when ready, otherwise what is wrong, in words for the user.
    /// </summary>
    public static string? PrepareCuda(string folder)
    {
        lock (Gate)
        {
            if (cudaFolder != null) return null;
            string? problem = CheckDriver() ?? LoadCudaLibraries(folder);
            if (problem == null) cudaFolder = folder;
            return problem;
        }
    }

    /// <summary>
    /// Puts <paramref name="folder"/> first on the PATH and loads every NVIDIA library in it
    /// by full path, dependencies first. Returns null when all load, else what failed.
    /// </summary>
    public static string? LoadCudaLibraries(string folder)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.StartsWith(folder + ";", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", folder + ";" + path);
        foreach (string dll in AiVoiceFiles.CudaLoadOrder)
        {
            string file = Path.Combine(folder, dll);
            if (!NativeLibrary.TryLoad(file, out _))
                return File.Exists(file)
                    ? $"{dll} could not be loaded (Windows error {Marshal.GetLastPInvokeError()})."
                    : $"{dll} is missing from {folder}.";
        }
        return null;
    }

    /// <summary>
    /// The NVIDIA driver's CUDA version must be 13.0 or newer (driver 580 or newer).
    /// </summary>
    public static string? CheckDriver()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var nvcuda))
            return "No NVIDIA graphics driver was found. The AI voice on the GPU needs an NVIDIA graphics card.";
        try
        {
            var getVersion = Marshal.GetDelegateForFunctionPointer<CuDriverGetVersion>(NativeLibrary.GetExport(nvcuda, "cuDriverGetVersion"));
            if (getVersion(out int version) != 0) return "The NVIDIA driver did not report its CUDA version.";
            if (version < 13000)
                return $"The NVIDIA driver supports CUDA {version / 1000}.{version % 1000 / 10}; CUDA 13.0 is needed. Update the NVIDIA driver to version 580 or newer.";
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return "The NVIDIA driver is too old. Update it to version 580 or newer.";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CuDriverGetVersion(out int version);

    /// <summary>The first Visual C++ runtime library ONNX Runtime needs that cannot be loaded, or null.</summary>
    public static string? MissingVisualCppRuntime()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (string dll in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll" })
            if (!NativeLibrary.TryLoad(dll, out _)) return dll;
        return null;
    }
}
