using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace MicFX;

public partial class MainWindow
{
    private const string RepoSlug = "Norbeto0/MicFX";

    private Action? noticeAction;
    private Action? noticeSecondaryAction;
    private Action? noticeCloseAction;
    private string? updateTag;
    private string? updateHtmlUrl;
    private string? updateSetupUrl;
    private string? updatePortableUrl;
    private bool updating;

    // ---------- notices (VB-Cable setup, updates) ----------

    private void ShowNotice(string text, string? actionText, Action? action,
        string? secondaryText = null, Action? secondary = null, Action? onClose = null)
    {
        txtNotice.Text = text;
        noticeAction = action;
        noticeSecondaryAction = secondary;
        noticeCloseAction = onClose;
        btnNoticeAction.Content = actionText;
        btnNoticeAction.Visibility = actionText != null ? Visibility.Visible : Visibility.Collapsed;
        btnNoticeSecondary.Content = secondaryText;
        btnNoticeSecondary.Visibility = secondaryText != null ? Visibility.Visible : Visibility.Collapsed;
        noticeBar.Visibility = Visibility.Visible;
    }

    private void HideNotice() => noticeBar.Visibility = Visibility.Collapsed;

    private void NoticeAction_Click(object sender, RoutedEventArgs e) => noticeAction?.Invoke();

    private void NoticeSecondary_Click(object sender, RoutedEventArgs e) => noticeSecondaryAction?.Invoke();

    private void NoticeClose_Click(object sender, RoutedEventArgs e)
    {
        noticeCloseAction?.Invoke();
        HideNotice();
    }

    // ---------- updates ----------

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        txtUpdateStatus.Visibility = Visibility.Visible;
        if (updating)
        {
            txtUpdateStatus.Text = "An update is already being installed.";
            return;
        }
        btnCheckUpdates.IsEnabled = false;
        txtUpdateStatus.Text = "Checking…";
        try
        {
            await CheckForUpdatesAsync(manual: true);
        }
        finally
        {
            btnCheckUpdates.IsEnabled = !updating;
        }
    }

    /// <summary>
    /// Looks up the latest GitHub release and offers it in the notice bar. The
    /// check at startup stays silent on failure and does not replace a notice
    /// that is already showing; a manual check reports its result in the
    /// settings menu and always shows the update.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (updating) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MicFX-Updater");
            string json = await http.GetStringAsync(
                $"https://api.github.com/repos/{RepoSlug}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.GetProperty("tag_name").GetString();
            string? url = root.GetProperty("html_url").GetString();
            if (tag == null || url == null) return;

            var latest = Version.Parse(tag.TrimStart('v', 'V'));
            var asm = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var current = new Version(asm.Major, asm.Minor, asm.Build);
            if (latest <= current)
            {
                if (manual) txtUpdateStatus.Text = "You have the latest version.";
                return;
            }
            if (!manual && noticeBar.Visibility == Visibility.Visible) return;

            updateTag = tag;
            updateHtmlUrl = url;
            updateSetupUrl = null;
            updatePortableUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.GetProperty("name").GetString();
                    string? download = asset.GetProperty("browser_download_url").GetString();
                    if (name == null || download == null) continue;
                    if (name.StartsWith("MicFX-Setup", StringComparison.OrdinalIgnoreCase))
                        updateSetupUrl = download;
                    else if (name.Equals("MicFX.exe", StringComparison.OrdinalIgnoreCase))
                        updatePortableUrl = download;
                }
            }

            bool canAutoUpdate = updateSetupUrl != null || updatePortableUrl != null;
            ShowNotice($"MicFX {tag} is available (you have v{current}).",
                canAutoUpdate ? "Install update" : "Download",
                canAutoUpdate ? StartUpdate : () => OpenUrl(url),
                "What's new", () => OpenUrl(url));
            if (manual)
            {
                txtUpdateStatus.Text = $"{tag} is available.";
                btnMenu.IsChecked = false; // reveal the notice bar under the menu
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            if (manual) txtUpdateStatus.Text = "GitHub is limiting requests right now. Try again in an hour.";
        }
        catch
        {
            // Offline or GitHub unreachable. Stay quiet at startup.
            if (manual) txtUpdateStatus.Text = "Could not reach GitHub. Check your connection.";
        }
    }

    /// <summary>
    /// Downloads and applies the update. Installed copies (in
    /// %LOCALAPPDATA%\Programs\MicFX) run the setup silently, which replaces
    /// the files and relaunches; portable copies swap their own exe.
    /// </summary>
    private async void StartUpdate()
    {
        if (updating) return;
        updating = true;
        btnNoticeAction.IsEnabled = false;
        btnNoticeSecondary.IsEnabled = false;
        btnNoticeClose.IsEnabled = false;
        try
        {
            bool installed = IsInstalledCopy();
            string? url = installed
                ? updateSetupUrl ?? updatePortableUrl
                : updatePortableUrl ?? updateSetupUrl;
            if (url == null)
            {
                OpenUrl(updateHtmlUrl ?? $"https://github.com/{RepoSlug}/releases");
                return;
            }

            bool viaSetup = url == updateSetupUrl;
            string dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                viaSetup ? $"MicFX-Setup-{updateTag}.exe" : $"MicFX-{updateTag}.exe.download");
            await DownloadFileAsync(url, dest,
                percent => txtNotice.Text = $"Downloading MicFX {updateTag}… {percent:0}%");

            txtNotice.Text = "Installing — MicFX will restart itself…";
            if (viaSetup)
            {
                Process.Start(new ProcessStartInfo(dest,
                    "/VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART /AUTORELAUNCH=1")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                // A running exe can be renamed but not overwritten: shift the
                // old one aside, drop the new one in place, relaunch delayed
                // (so the single-instance mutex of this process is gone).
                string exe = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine the application path.");
                string old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                File.Move(dest, exe);
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\" --updated")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            ForceExit();
        }
        catch (Exception ex)
        {
            txtNotice.Text = "Update failed: " + ex.Message;
            btnNoticeAction.IsEnabled = true;
            btnNoticeSecondary.IsEnabled = true;
            btnNoticeClose.IsEnabled = true;
            updating = false;
        }
    }

    private static bool IsInstalledCopy()
    {
        string exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        string installDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MicFX");
        return string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(exeDir),
            System.IO.Path.TrimEndingDirectorySeparator(installDir),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFileAsync(string url, string dest, Action<double> progress)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MicFX-Updater");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0) progress(done * 100.0 / total);
        }
    }

    /// <summary>Removes the leftover .old exe from a previous portable self-update.</summary>
    private static void CleanUpAfterUpdate()
    {
        try
        {
            string old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // Previous instance may still be exiting — it'll be cleaned next launch.
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
