using System.Net;
using System.Security.Cryptography;
using MicFX.AiVoice;
using Xunit;

namespace MicFX.Tests;

/// <summary>The host's life cycle with the tiny test models on the CPU.</summary>
public class AiVoiceHostTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly string root = Path.Combine(Path.GetTempPath(), "micfx-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private AiVoiceHost Host()
    {
        var server = new Server();
        PinnedFile Serve(string name, string fixture)
        {
            var data = File.ReadAllBytes(Path.Combine(Fixtures, fixture));
            string url = "https://example.test/" + name;
            server.Files[url] = data;
            return new PinnedFile(name, url, data.Length, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        }
        var helpers = new FileSet("models-test", "Helper models", new[] { Serve("contentvec.onnx", "contentvec-tiny.onnx"), Serve("fcpe.onnx", "fcpe-tiny.onnx") });
        var cuda = new FileSet("cuda-test", "NVIDIA libraries", new[] { Serve("cudart.dll", "gen-webui.onnx") });
        return new AiVoiceHost(new AiVoiceHostOptions { Root = root, Helpers = helpers, Cuda = cuda, ManageNativeRuntime = false, Http = server });
    }

    [Fact]
    public async Task DownloadsLoadsRunsAndUnloads()
    {
        using var host = Host();
        int changes = 0;
        host.Changed += () => Interlocked.Increment(ref changes);

        var missing = host.MissingFiles(AiBackend.Cpu);
        Assert.Single(missing);
        await host.InstallAsync(missing, null, CancellationToken.None);
        Assert.Empty(host.MissingFiles(AiBackend.Cpu));
        Assert.Single(host.MissingFiles(AiBackend.Cuda));

        host.Pitch = 5;
        Assert.True(await host.LoadAsync(Path.Combine(Fixtures, "gen-micfx.onnx"), AiBackend.Cpu, AiVoiceDelay.Normal));
        Assert.Equal(AiVoiceState.Running, host.State);
        Assert.NotNull(host.Stream);
        Assert.Equal(AiBackend.Cpu, host.Backend);
        Assert.Equal(210, host.DelayMs);
        Assert.Contains("ms per block after warm-up", File.ReadAllText(host.Log.Path!));

        var stream = host.Stream!;
        await host.UnloadAsync();
        Assert.Null(host.Stream);
        Assert.Equal(AiVoiceState.Off, host.State);
        Assert.True(stream.WorkerExited.IsCompleted);
        Assert.True(changes >= 4);
    }

    [Fact]
    public async Task GpuWithoutNvidiaLibraries_RunsOnTheCpuAndSaysSo()
    {
        using var host = Host();
        await host.InstallAsync(host.MissingFiles(AiBackend.Cpu), null, CancellationToken.None);
        Assert.True(await host.LoadAsync(Path.Combine(Fixtures, "gen-micfx.onnx"), AiBackend.Cuda, AiVoiceDelay.Safe));
        Assert.Equal(AiBackend.Cpu, host.Backend);
        Assert.Contains("Running on the CPU", host.Status);
        await host.UnloadAsync();
    }

    [Fact]
    public async Task HigherQualityAndAnIndex_StepDownOrAreDroppedWithANote_NeverFail()
    {
        using var host = Host();
        await host.InstallAsync(host.MissingFiles(AiBackend.Cpu), null, CancellationToken.None);
        // The test index has 16 features per frame, the test model 768: reported, voice still runs.
        // The test model has 64 frames, too few for Best: it steps down to High and says so.
        string index = Path.Combine(Fixtures, "ivf-full.index");
        Assert.True(await host.LoadAsync(Path.Combine(Fixtures, "gen-micfx.onnx"), AiBackend.Cpu, AiVoiceDelay.Normal,
            quality: AiVoiceQuality.Best, indexPath: index));
        Assert.Equal(AiVoiceDelays.Ms(AiVoiceDelay.Normal, AiVoiceQuality.High), host.DelayMs);
        Assert.Null(host.IndexPath);
        Assert.Contains("Index not used", host.Status);
        Assert.Contains("using High", host.Status);
        await host.UnloadAsync();
    }

    [Fact]
    public void IndexFile_IsFoundNextToTheModel()
    {
        Directory.CreateDirectory(root);
        string model = Path.Combine(root, "Voice.onnx");
        File.WriteAllText(model, "");
        Assert.Null(AiVoiceHost.FindIndex(model));
        string only = Path.Combine(root, "model.index");
        File.WriteAllText(only, "");
        Assert.Equal(only, AiVoiceHost.FindIndex(model));             // the only one in the folder
        string named = Path.Combine(root, "Voice.index");
        File.WriteAllText(named, "");
        Assert.Equal(named, AiVoiceHost.FindIndex(model));            // the model's own name wins
        Assert.Equal(only, AiVoiceHost.FindIndex(model, only));       // a chosen one wins over both
        Assert.Equal(named, AiVoiceHost.FindIndex(model, Path.Combine(root, "gone.index")));
    }

    [Fact]
    public async Task NotAVoiceModel_FailsWithAReason()
    {
        using var host = Host();
        await host.InstallAsync(host.MissingFiles(AiBackend.Cpu), null, CancellationToken.None);
        Assert.False(await host.LoadAsync(Path.Combine(Fixtures, "fcpe-tiny.onnx"), AiBackend.Cpu, AiVoiceDelay.Normal));
        Assert.Equal(AiVoiceState.Failed, host.State);
        Assert.Contains("not an RVC voice model", host.Status);
        Assert.Null(host.Stream);
    }

    [Fact]
    public async Task MissingDownloads_AreReportedNotGuessed()
    {
        using var host = Host();
        Assert.False(await host.LoadAsync(Path.Combine(Fixtures, "gen-micfx.onnx"), AiBackend.Cpu, AiVoiceDelay.Normal));
        Assert.Contains("not downloaded", host.Status);
    }

    [Fact]
    public void DelayPresets_AreOnTheTenMillisecondGrid()
    {
        Assert.Equal(240, AiVoiceDelays.Ms(AiVoiceDelay.Normal, AiVoiceQuality.High));
        Assert.Equal(270, AiVoiceDelays.Ms(AiVoiceDelay.Normal, AiVoiceQuality.Best));
        Assert.Equal(150, AiVoiceDelays.Ms(AiVoiceDelay.Short));
        Assert.Equal(210, AiVoiceDelays.Ms(AiVoiceDelay.Normal));
        Assert.Equal(310, AiVoiceDelays.Ms(AiVoiceDelay.Safe));
        foreach (var d in Enum.GetValues<AiVoiceDelay>())
            foreach (var q in Enum.GetValues<AiVoiceQuality>())
            {
                var t = AiVoiceDelays.Timing(d, q);
                Assert.Equal(0, t.Output % 480);
                Assert.Equal(0, t.Block % 480);
                Assert.Equal(AiVoiceDelays.Quality(q).LookaheadFrames * 480, t.Lookahead);
            }
    }
}

/// <summary>
/// Downloads the real pinned ONNX Runtime and NVIDIA libraries and checks that every
/// one of them, and the CUDA provider, loads by full path. Needs Windows but no GPU,
/// so it runs on the release workflow's runner when MICFX_CUDA_PROBE=1.
/// </summary>
public class CudaLibraryProbe
{
    [Fact]
    public async Task PinnedNvidiaLibraries_Load()
    {
        if (Environment.GetEnvironmentVariable("MICFX_CUDA_PROBE") != "1" || !OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "micfx-cuda-probe");
        var downloader = new PinnedDownloader(root);
        await downloader.InstallAsync(AiVoiceFiles.Runtime, null, CancellationToken.None);
        await downloader.InstallAsync(AiVoiceFiles.Cuda, null, CancellationToken.None);

        Assert.Null(OrtNative.LoadCudaLibraries(downloader.FolderOf(AiVoiceFiles.Cuda)));
        string runtime = downloader.FolderOf(AiVoiceFiles.Runtime);
        System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(runtime, "onnxruntime_providers_shared.dll"));
        // Without an NVIDIA driver the CUDA provider's start-up code fails (0x8007045A,
        // "initialization routine failed"); that still proves every library it imports
        // was found. A missing one would be 0x8007007E ("module not found").
        try
        {
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(runtime, "onnxruntime_providers_cuda.dll"));
        }
        catch (DllNotFoundException ex) when (OrtNative.CheckDriver() != null)
        {
            Assert.DoesNotContain("0x8007007E", ex.Message);
            Assert.Contains("0x8007045A", ex.Message);
        }
    }
}
