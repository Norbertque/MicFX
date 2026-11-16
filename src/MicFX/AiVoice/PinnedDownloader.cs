using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MicFX.AiVoice;

/// <summary>Progress of a <see cref="PinnedDownloader.InstallAsync"/> call.</summary>
public readonly record struct DownloadProgress(string File, long Done, long Total);

/// <summary>
/// Downloads pinned files into a folder of their own: resumes partial
/// downloads, checks size and SHA-256 (a mismatch is never installed),
/// extracts only the listed members of archives and checks those too, and
/// writes a list of what was installed so later starts only check sizes.
/// </summary>
public sealed class PinnedDownloader
{
    private const string Marker = "installed.txt";
    private const int Attempts = 4;
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private readonly HttpClient http;
    private readonly string root;

    public PinnedDownloader(string root, HttpMessageHandler? handler = null)
    {
        this.root = root;
        http = handler != null ? new HttpClient(handler) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MicFX");
    }

    public string FolderOf(FileSet set) => Path.Combine(root, set.Folder);

    private string Downloads => Path.Combine(root, "downloads");

    /// <summary>True when the set was installed and every file still has its size.</summary>
    public bool IsInstalled(FileSet set)
    {
        string folder = FolderOf(set);
        string marker = Path.Combine(folder, Marker);
        if (!File.Exists(marker)) return false;
        foreach (var (name, size, _) in Installed(set))
        {
            var info = new FileInfo(Path.Combine(folder, name));
            if (!info.Exists || info.Length != size) return false;
        }
        return true;
    }

    /// <summary>Bytes still to download for the set (0 when installed).</summary>
    public long RemainingDownload(FileSet set) => IsInstalled(set) ? 0 : set.Files.Where(f => !IsFileInstalled(set, f)).Sum(f => f.Size);

    public async Task InstallAsync(FileSet set, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        if (IsInstalled(set)) return;
        string folder = FolderOf(set);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Downloads);

        var pending = set.Files.Where(f => !IsFileInstalled(set, f)).ToList();
        long need = pending.Sum(f => f.Size + (f.IsArchive ? f.InstalledSize : 0)) + 64L * 1024 * 1024;
        long free = FreeSpace(root);
        if (free >= 0 && free < need)
            throw new IOException($"Not enough disk space: {set.Title} needs {Mb(need)} free on {Path.GetPathRoot(root)}, {Mb(free)} is free.");

        long total = set.DownloadSize;
        long before = set.Files.Except(pending).Sum(f => f.Size);
        foreach (var file in pending)
        {
            long offset = before;
            var fileProgress = progress == null ? null : new Progress<long>(d => progress.Report(new DownloadProgress(file.Name, offset + d, total)));
            string part = Path.Combine(Downloads, file.Name + ".part");
            await DownloadAsync(file, part, fileProgress, cancel).ConfigureAwait(false);
            if (file.IsArchive)
            {
                await Task.Run(() => Extract(part, file, folder, cancel), cancel).ConfigureAwait(false);
                File.Delete(part);
            }
            else
            {
                File.Move(part, Path.Combine(folder, file.Name), overwrite: true);
            }
            before += file.Size;
        }

        File.WriteAllLines(Path.Combine(folder, Marker), Installed(set).Select(e => $"{e.Name} {e.Size} {e.Sha256}"));
        progress?.Report(new DownloadProgress("", total, total));
    }

    private static IEnumerable<(string Name, long Size, string Sha256)> Installed(FileSet set) =>
        set.Files.SelectMany(f => f.IsArchive
            ? f.Members.Select(m => (m.Name, m.Size, m.Sha256))
            : new[] { (f.Name, f.Size, f.Sha256) });

    private bool IsFileInstalled(FileSet set, PinnedFile file)
    {
        string folder = FolderOf(set);
        var names = file.IsArchive ? file.Members.Select(m => (m.Name, m.Size)) : new[] { (file.Name, file.Size) };
        return names.All(n =>
        {
            var info = new FileInfo(Path.Combine(folder, n.Item1));
            return info.Exists && info.Length == n.Item2;
        });
    }

    private async Task DownloadAsync(PinnedFile file, string part, IProgress<long>? progress, CancellationToken cancel)
    {
        bool restarted = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(file, part, progress, cancel).ConfigureAwait(false);
                if (await HashAsync(part, cancel).ConfigureAwait(false) == file.Sha256) return;
                File.Delete(part);
                if (restarted) throw new InvalidDataException($"{file.Name}: the download does not match its pinned SHA-256.");
                restarted = true; // a bad partial file from an earlier run; start over once
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && ex is not InvalidDataException && !cancel.IsCancellationRequested && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancel).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchAsync(PinnedFile file, string part, IProgress<long>? progress, CancellationToken cancel)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size)
        {
            File.Delete(part);
            have = 0;
        }
        if (have == file.Size) return;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        stall.CancelAfter(Stall);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range

        await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16];
        long done = have;
        progress?.Report(done);
        while (true)
        {
            stall.CancelAfter(Stall);
            int n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            if (n == 0) break;
            if (done + n > file.Size) throw new InvalidDataException($"{file.Name}: the server sent more than the pinned size.");
            await output.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
            done += n;
            progress?.Report(done);
        }
        if (done != file.Size) throw new IOException($"{file.Name}: the download ended at {done} of {file.Size} bytes.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancel)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Extract(string archive, PinnedFile file, string folder, CancellationToken cancel)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var member in file.Members)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(member.Path) ?? throw new InvalidDataException($"{file.Name}: {member.Path} is missing.");
            string target = Path.Combine(folder, member.Name);
            string temp = target + ".tmp";
            using (var input = entry.Open())
            using (var output = File.Create(temp))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long size = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                    size += n;
                }
                string digest = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (size != member.Size || digest != member.Sha256)
                {
                    output.Dispose();
                    File.Delete(temp);
                    throw new InvalidDataException($"{member.Name} in {file.Name} does not match its pinned SHA-256.");
                }
            }
            File.Move(temp, target, overwrite: true);
        }
    }

    private static long FreeSpace(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    internal static string Mb(long bytes) => $"{bytes / 1e6:F0} MB";
}
