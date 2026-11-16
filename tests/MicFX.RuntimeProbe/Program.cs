using MicFX.AiVoice;
using MicFX.Audio;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("The runtime probe needs Windows.");
    return 0;
}

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "micfx-runtime-probe");
string model = Path.Combine(AppContext.BaseDirectory, "gen-micfx.onnx");
var host = new AiVoiceHost(new AiVoiceHostOptions { Root = root });

// 1. Downloads from the real pinned URLs (nuget.org, the models-1 release, NVIDIA).
var sets = host.MissingFiles(AiBackend.Cuda);
Console.WriteLine($"Downloading {string.Join(", ", sets.Select(s => s.Title))}");
await host.InstallAsync(sets, null, CancellationToken.None);
Check(host.MissingFiles(AiBackend.Cuda).Count == 0, "all pinned files downloaded and checked");

// 2. ONNX Runtime from the downloaded folder, through OrtNative, on the CPU.
bool loaded = await host.LoadAsync(model, AiBackend.Cpu, AiVoiceDelay.Normal);
Check(loaded && host.State == AiVoiceState.Running, $"voice runs on the CPU ({host.State}: {host.Status})");
Check(OrtNative.RuntimeFolder == host.Downloader.FolderOf(AiVoiceFiles.Runtime), $"ONNX Runtime loaded from {OrtNative.RuntimeFolder}");

// 3. Real blocks through the worker thread.
if (host.Stream is VoiceStream stream)
{
    var input = new float[48000 * 3];
    for (int i = 0; i < input.Length; i++) input[i] = 0.3f * MathF.Sin(2 * MathF.PI * 150 * i / 48000f);
    var provider = new AiVoiceSampleProvider(new ArraySource(input)) { Stream = stream, Enabled = true };
    var output = new float[input.Length];
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (int pos = 0; pos < input.Length; pos += 480)
    {
        provider.Read(output, pos, 480);
        int ahead = pos / 48 - (int)clock.ElapsedMilliseconds;
        if (ahead > 0) Thread.Sleep(ahead);
    }
    Check(stream.Fault == null, $"no fault ({stream.Fault})");
    Check(output.Any(v => v != 0), $"converted audio came out ({stream.LastComputeMs:F1} ms per block, {stream.MissedBlocks} missed)");
}
await host.UnloadAsync();

// 4. The GPU path without a GPU: the host must explain and fall back to the CPU.
loaded = await host.LoadAsync(model, AiBackend.Cuda, AiVoiceDelay.Normal);
Check(loaded && host.Backend == AiBackend.Cpu && host.Status.Contains("Running on the CPU"), $"GPU falls back to the CPU: {host.Status}");
await host.UnloadAsync();

// 5. CUDA session options are accepted; whatever fails must be the missing GPU, not an option.
try
{
    Console.WriteLine($"NVIDIA libraries: {OrtNative.LoadCudaLibraries(host.Downloader.FolderOf(AiVoiceFiles.Cuda)) ?? "all loaded"}");
    using var options = OnnxSessions.Create(AiBackend.Cuda, 1);
    using var session = new InferenceSession(model, options);
    Console.WriteLine("CUDA session created");
}
catch (Exception ex)
{
    Console.WriteLine($"CUDA session: {ex.GetType().Name}: {ex.Message}");
    Check(!ex.Message.Contains("option", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("cuda", StringComparison.OrdinalIgnoreCase),
          "the CUDA failure is about the GPU, not about our options");
}

Console.WriteLine();
Console.WriteLine(File.ReadAllText(Path.Combine(root, "log.txt")));
host.Dispose();
Console.WriteLine(failures == 0 ? "Runtime probe passed." : $"Runtime probe: {failures} check(s) failed.");
return failures == 0 ? 0 : 1;

sealed class ArraySource : ISampleProvider
{
    private readonly float[] data;
    private int pos;
    public ArraySource(float[] data) => this.data = data;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public int Read(float[] buffer, int offset, int count)
    {
        int n = Math.Min(count, data.Length - pos);
        Array.Copy(data, pos, buffer, offset, n);
        pos += n;
        for (int i = n; i < count; i++) buffer[offset + i] = 0;
        return count;
    }
}
