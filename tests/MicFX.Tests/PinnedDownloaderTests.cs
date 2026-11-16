using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MicFX.AiVoice;
using Xunit;

namespace MicFX.Tests;

public class PinnedDownloaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "micfx-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>Serves byte arrays by URL, honouring Range requests, and records what was asked.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<(string Url, long? From)> Requests = new();
        public int FailFirst;
        public bool IgnoreRange;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            long? from = request.Headers.Range?.Ranges.First().From;
            Requests.Add((url, from));
            if (FailFirst > 0)
            {
                FailFirst--;
                throw new HttpRequestException("connection reset");
            }
            if (!Files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (from is long start && !IgnoreRange)
            {
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)start..]) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
                return Task.FromResult(partial);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Bytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] Zip(params (string Path, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, data) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private (FakeServer Server, FileSet Set, byte[] Model, byte[] Dll) Setup()
    {
        var model = Bytes(300_000, 1);
        var dll = Bytes(120_000, 2);
        var other = Bytes(50_000, 3);
        var archive = Zip(("pkg/bin/runtime.dll", dll), ("pkg/bin/unused.dll", other));
        var server = new FakeServer();
        server.Files["https://example.test/model.onnx"] = model;
        server.Files["https://example.test/pkg.zip"] = archive;
        var set = new FileSet("set-1", "Test files", new[]
        {
            new PinnedFile("model.onnx", "https://example.test/model.onnx", model.Length, Sha(model)),
            new PinnedFile("pkg.zip", "https://example.test/pkg.zip", archive.Length, Sha(archive),
                new[] { new ArchiveMember("pkg/bin/runtime.dll", "runtime.dll", dll.Length, Sha(dll)) }),
        });
        return (server, set, model, dll);
    }

    [Fact]
    public async Task Installs_ChecksAndExtractsOnlyTheListedMembers()
    {
        var (server, set, model, dll) = Setup();
        var downloader = new PinnedDownloader(root, server);
        var reports = new List<DownloadProgress>();
        Assert.False(downloader.IsInstalled(set));
        Assert.Equal(set.DownloadSize, downloader.RemainingDownload(set));

        await downloader.InstallAsync(set, new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

        string folder = downloader.FolderOf(set);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(folder, "model.onnx")));
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(folder, "runtime.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "unused.dll")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "downloads")));
        Assert.True(downloader.IsInstalled(set));
        Assert.Equal(0, downloader.RemainingDownload(set));
        Assert.Equal(set.DownloadSize, reports[^1].Done);

        // Installed sets are not fetched again.
        int requests = server.Requests.Count;
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.Equal(requests, server.Requests.Count);
    }

    [Fact]
    public async Task WrongContent_IsNeverInstalled()
    {
        var (server, set, _, _) = Setup();
        server.Files["https://example.test/model.onnx"] = Bytes(300_000, 99); // same size, other bytes
        var downloader = new PinnedDownloader(root, server);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => downloader.InstallAsync(set, null, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(Path.Combine(downloader.FolderOf(set), "model.onnx")));
        Assert.False(downloader.IsInstalled(set));
    }

    [Fact]
    public async Task WrongArchiveMember_IsNeverInstalled()
    {
        var (server, _, _, dll) = Setup();
        var archive = Zip(("pkg/bin/runtime.dll", Bytes(dll.Length, 77)));
        server.Files["https://example.test/bad.zip"] = archive;
        var set = new FileSet("set-2", "Bad", new[]
        {
            new PinnedFile("bad.zip", "https://example.test/bad.zip", archive.Length, Sha(archive),
                new[] { new ArchiveMember("pkg/bin/runtime.dll", "runtime.dll", dll.Length, Sha(dll)) }),
        });
        var downloader = new PinnedDownloader(root, server);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.InstallAsync(set, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(downloader.FolderOf(set), "runtime.dll")));
    }

    [Fact]
    public async Task PartialDownload_IsResumedFromWhereItStopped()
    {
        var (server, set, model, _) = Setup();
        Directory.CreateDirectory(Path.Combine(root, "downloads"));
        File.WriteAllBytes(Path.Combine(root, "downloads", "model.onnx.part"), model[..120_000]);
        var downloader = new PinnedDownloader(root, server);
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.Contains(("https://example.test/model.onnx", 120_000L), server.Requests);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(downloader.FolderOf(set), "model.onnx")));
    }

    [Fact]
    public async Task CorruptPartialDownload_IsStartedOverOnce()
    {
        var (server, set, model, _) = Setup();
        Directory.CreateDirectory(Path.Combine(root, "downloads"));
        File.WriteAllBytes(Path.Combine(root, "downloads", "model.onnx.part"), Bytes(120_000, 5));
        var downloader = new PinnedDownloader(root, server);
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(downloader.FolderOf(set), "model.onnx")));
        Assert.Contains(("https://example.test/model.onnx", (long?)null), server.Requests);
    }

    [Fact]
    public async Task ServerIgnoringRange_StartsFromTheBeginning()
    {
        var (server, set, model, _) = Setup();
        server.IgnoreRange = true;
        Directory.CreateDirectory(Path.Combine(root, "downloads"));
        File.WriteAllBytes(Path.Combine(root, "downloads", "model.onnx.part"), model[..120_000]);
        var downloader = new PinnedDownloader(root, server);
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(downloader.FolderOf(set), "model.onnx")));
    }

    [Fact]
    public async Task NetworkErrors_AreRetried()
    {
        var (server, set, model, _) = Setup();
        server.FailFirst = 1;
        var downloader = new PinnedDownloader(root, server);
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(downloader.FolderOf(set), "model.onnx")));
    }

    [Fact]
    public async Task ChangedFileOnDisk_MakesTheSetNotInstalled()
    {
        var (server, set, _, _) = Setup();
        var downloader = new PinnedDownloader(root, server);
        await downloader.InstallAsync(set, null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(downloader.FolderOf(set), "runtime.dll"), new byte[10]);
        Assert.False(downloader.IsInstalled(set));
        await downloader.InstallAsync(set, null, CancellationToken.None);
        Assert.True(downloader.IsInstalled(set));
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        public SyncProgress(Action<T> action) => this.action = action;
        public void Report(T value) => action(value);
    }
}
