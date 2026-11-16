using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MicFX.AiVoice;
using MicFX.Models;
using Microsoft.Win32;

namespace MicFX;

/// <summary>The VOICE card: the AI voice, its downloads, model and status.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan AiUnloadAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AiWarningHold = TimeSpan.FromSeconds(4);

    private AiVoiceHost? aiHost;
    private DispatcherTimer? aiStatusTimer;
    private DispatcherTimer? aiUnloadTimer;
    private CancellationTokenSource? aiDownloadCancel;
    private bool aiUiUpdating;
    private bool aiStarting;
    private bool aiDropoutNotified;
    private long aiSeenMisses;
    private DateTime aiWarningUntil;
    private (string Model, AiBackend Backend, AiVoiceDelay Delay)? aiLoadedConfig;

    private AiVoiceHost AiHost
    {
        get
        {
            if (aiHost == null)
            {
                aiHost = new AiVoiceHost(new AiVoiceHostOptions { Root = Path.Combine(AppSettings.SettingsDir, "ai") })
                {
                    Pitch = settings.AiVoicePitch,
                };
                aiHost.Changed += () => Dispatcher.BeginInvoke(OnAiVoiceHostChanged);
            }
            return aiHost;
        }
    }

    private AiBackend AiBackendSetting => settings.AiVoiceBackend == "Cpu" ? AiBackend.Cpu : AiBackend.Cuda;

    private AiVoiceDelay AiDelaySetting => Enum.TryParse<AiVoiceDelay>(settings.AiVoiceDelay, out var d) ? d : AiVoiceDelay.Normal;

    private void ApplyAiVoiceSettingsToUi()
    {
        aiUiUpdating = true;
        SelectByTag(comboAiBackend, settings.AiVoiceBackend);
        SelectByTag(comboAiDelay, settings.AiVoiceDelay);
        sliderAiVoicePitch.Value = settings.AiVoicePitch;
        lblAiVoicePitch.Text = PitchLabel(settings.AiVoicePitch);
        chkAiVoice.IsChecked = settings.AiVoiceEnabled;
        aiUiUpdating = false;
        UpdateAiVoiceUi();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private static string PitchLabel(double semitones) => semitones switch
    {
        > 0 => $"+{semitones:0} st",
        < 0 => $"{semitones:0} st",
        _ => "0 st",
    };

    /// <summary>Profiles saved before 1.12 have no AI voice settings and leave it as it is.</summary>
    private void ApplyProfileAiVoice(Profile p)
    {
        bool modelChanged = !string.IsNullOrEmpty(p.AiVoiceModelPath) && p.AiVoiceModelPath != settings.AiVoiceModelPath;
        if (!string.IsNullOrEmpty(p.AiVoiceModelPath)) settings.AiVoiceModelPath = p.AiVoiceModelPath;
        if (p.AiVoicePitch is float pitch) sliderAiVoicePitch.Value = pitch;
        if (p.AiVoiceEnabled is bool on && chkAiVoice.IsChecked != on) chkAiVoice.IsChecked = on; // its handler starts or stops the voice
        else if (modelChanged && chkAiVoice.IsChecked == true) _ = StartAiVoiceAsync();
        UpdateAiVoiceUi();
    }

    /// <summary>Starts the AI voice after start-up if it was on when MicFX closed.</summary>
    private void ResumeAiVoice()
    {
        if (settings.AiVoiceEnabled) _ = StartAiVoiceAsync(interactive: false);
    }

    private void AiVoice_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing || aiUiUpdating) return;
        bool on = chkAiVoice.IsChecked == true;
        settings.AiVoiceEnabled = on;
        if (on)
        {
            aiUnloadTimer?.Stop();
            _ = StartAiVoiceAsync();
        }
        else
        {
            aiDownloadCancel?.Cancel();
            engine.SetAiVoice(false, aiHost?.Stream);
            ScheduleAiUnload();
        }
        UpdateAiVoiceUi();
        RefreshRunningStatus();
    }

    /// <summary>
    /// Downloads what is missing (after asking), loads the model and starts the voice.
    /// Not interactive (at start-up): never opens a dialog, reports problems instead.
    /// </summary>
    private async Task StartAiVoiceAsync(bool interactive = true)
    {
        if (aiStarting) return;
        string? model = settings.AiVoiceModelPath;
        if (string.IsNullOrEmpty(model) || !File.Exists(model))
        {
            if (!interactive || !ChooseVoiceModel())
            {
                SetAiVoiceChecked(false);
                ShowAiStatus(string.IsNullOrEmpty(model) ? "Load a voice model first." : $"Voice model not found: {model}", warn: true);
                return;
            }
            model = settings.AiVoiceModelPath!;
        }

        var host = AiHost;
        var backend = AiBackendSetting;
        var delay = AiDelaySetting;
        if (host.State == AiVoiceState.Running && aiLoadedConfig == (model, backend, delay))
        {
            engine.SetAiVoice(true, host.Stream);
            return;
        }

        aiStarting = true;
        try
        {
            string? gpuNote = null;
            if (backend == AiBackend.Cuda)
            {
                string? driver = OrtNative.CheckDriver();
                if (driver != null)
                {
                    gpuNote = driver;
                    backend = AiBackend.Cpu;
                }
            }

            var missing = host.MissingFiles(backend);
            if (missing.Count > 0 && !interactive)
            {
                SetAiVoiceChecked(false);
                ShowAiStatus("The AI voice files are not downloaded. Switch it on to download them.", warn: true);
                return;
            }
            if (missing.Count > 0)
            {
                var answer = ConfirmAiDownloads(missing);
                if (answer == MessageBoxResult.Cancel)
                {
                    SetAiVoiceChecked(false);
                    ShowAiStatus("", warn: false);
                    return;
                }
                if (answer == MessageBoxResult.No)
                {
                    backend = AiBackend.Cpu;
                    missing = host.MissingFiles(backend);
                }
                else if (missing.Any(s => s.Folder == AiVoiceFiles.Cuda.Folder))
                {
                    settings.AiVoiceNvidiaAccepted = true;
                }
                if (missing.Count > 0 && !await DownloadAiFilesAsync(host, missing)) return;
            }

            if (chkAiVoice.IsChecked != true) return;
            host.Pitch = settings.AiVoicePitch;
            engine.SetAiVoice(true, null); // silent while the model loads, so the real voice never leaks
            bool ok = await host.LoadAsync(model, backend, delay,
                gpuNote != null ? $"NVIDIA GPU not used: {gpuNote} Running on the CPU." : null);
            aiLoadedConfig = ok ? (model, AiBackendSetting, delay) : null;
        }
        catch (Exception ex)
        {
            ShowAiStatus("AI voice failed: " + ex.Message, warn: true);
            AiHost.Log.Write("Start failed: " + ex);
        }
        finally
        {
            aiStarting = false;
            UpdateAiVoiceUi();
            RefreshRunningStatus();
        }
    }

    /// <summary>Yes: download everything. No: use the CPU (skip NVIDIA's files). Cancel: nothing.</summary>
    private MessageBoxResult ConfirmAiDownloads(IReadOnlyList<FileSet> missing)
    {
        bool nvidia = missing.Any(s => s.Folder == AiVoiceFiles.Cuda.Folder);
        var lines = missing.Select(s => $"  •  {s.Title}: {s.DownloadSize / 1_000_000} MB");
        string text = "The AI voice needs these downloads, once:\n\n" + string.Join("\n", lines) +
                      $"\n\nThey are saved in {Path.Combine(AppSettings.SettingsDir, "ai")}.";
        if (nvidia)
        {
            text += "\n\nThe NVIDIA CUDA and cuDNN libraries are NVIDIA's proprietary software. They are downloaded " +
                    "directly from NVIDIA under NVIDIA's license; MicFX does not distribute them. The license:\n" +
                    $"  {AiVoiceFiles.CudaLicenseUrl}\n  {AiVoiceFiles.CudnnLicenseUrl}\n\n" +
                    "Yes: accept NVIDIA's license and download everything (needs NVIDIA driver 580 or newer).\n" +
                    "No: skip the NVIDIA files and run the AI voice on the CPU (it may be too slow).\n" +
                    "Cancel: download nothing.";
            return MessageBox.Show(this, text, "MicFX — AI voice downloads", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        }
        return MessageBox.Show(this, text + "\n\nDownload now?", "MicFX — AI voice downloads", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK
            ? MessageBoxResult.Yes
            : MessageBoxResult.Cancel;
    }

    private async Task<bool> DownloadAiFilesAsync(AiVoiceHost host, IReadOnlyList<FileSet> sets)
    {
        aiDownloadCancel = new CancellationTokenSource();
        progressAiVoice.Visibility = Visibility.Visible;
        progressAiVoice.Value = 0;
        try
        {
            foreach (var set in sets)
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    progressAiVoice.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    ShowAiStatus($"Downloading {set.Title}: {p.Done / 1_000_000} of {p.Total / 1_000_000} MB", warn: false);
                });
                await host.InstallAsync(new[] { set }, progress, aiDownloadCancel.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowAiStatus("Download stopped; it continues from there next time.", warn: false);
            return false;
        }
        catch (Exception ex)
        {
            ShowAiStatus("Download failed: " + ex.Message, warn: true);
            SetAiVoiceChecked(false);
            return false;
        }
        finally
        {
            progressAiVoice.Visibility = Visibility.Collapsed;
            aiDownloadCancel.Dispose();
            aiDownloadCancel = null;
        }
    }

    private void SetAiVoiceChecked(bool on)
    {
        aiUiUpdating = true;
        chkAiVoice.IsChecked = on;
        aiUiUpdating = false;
        settings.AiVoiceEnabled = on;
        engine.SetAiVoice(on, aiHost?.Stream);
        RefreshRunningStatus();
    }

    /// <summary>Frees the model (and GPU memory) once the voice has been off for a minute.</summary>
    private void ScheduleAiUnload()
    {
        if (aiHost == null || aiHost.State == AiVoiceState.Off) return;
        aiUnloadTimer ??= new DispatcherTimer { Interval = AiUnloadAfter };
        aiUnloadTimer.Tick -= AiUnloadTick;
        aiUnloadTimer.Tick += AiUnloadTick;
        aiUnloadTimer.Stop();
        aiUnloadTimer.Start();
    }

    private async void AiUnloadTick(object? sender, EventArgs e)
    {
        aiUnloadTimer?.Stop();
        if (chkAiVoice.IsChecked == true || aiHost == null) return;
        aiLoadedConfig = null;
        await aiHost.UnloadAsync();
    }

    private void OnAiVoiceHostChanged()
    {
        if (aiHost == null) return;
        engine.SetAiVoice(chkAiVoice.IsChecked == true, aiHost.Stream);
        if (aiHost.State == AiVoiceState.Failed && chkAiVoice.IsChecked == true)
            NotifyAiProblem("AI voice could not start: " + aiHost.Status);
        if (aiHost.State == AiVoiceState.Running)
        {
            aiSeenMisses = aiHost.Stream?.MissedBlocks ?? 0;
            aiWarningUntil = DateTime.MinValue;
        }
        UpdateAiVoiceUi();
        RefreshRunningStatus();
    }

    private void UpdateAiVoiceUi()
    {
        string? model = settings.AiVoiceModelPath;
        txtAiVoiceModel.Text = string.IsNullOrEmpty(model) ? "No voice model" : Path.GetFileNameWithoutExtension(model);
        bool on = chkAiVoice.IsChecked == true;
        lblAiVoiceDelay.Text = on ? $"+{AiVoiceDelays.Ms(AiDelaySetting)} ms" : "";

        var state = aiHost?.State ?? AiVoiceState.Off;
        if (on && state == AiVoiceState.Running)
        {
            aiStatusTimer ??= CreateAiStatusTimer();
            aiStatusTimer.Start();
            UpdateAiRunningStatus();
            return;
        }
        aiStatusTimer?.Stop();
        if (state == AiVoiceState.Loading) ShowAiStatus(aiHost!.Status, warn: false);
        else if (state == AiVoiceState.Failed && on) ShowAiStatus(aiHost!.Status, warn: true);
        else if (!on && !aiStarting) ShowAiStatus("", warn: false);
    }

    private DispatcherTimer CreateAiStatusTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => UpdateAiRunningStatus();
        return timer;
    }

    /// <summary>Time per block against the block length, and a warning while it cannot keep up.</summary>
    private void UpdateAiRunningStatus()
    {
        var stream = aiHost?.Stream;
        if (stream == null || chkAiVoice.IsChecked != true) return;
        if (stream.Fault != null)
        {
            ShowAiStatus("The AI voice stopped: " + stream.Fault, warn: true);
            return;
        }
        long misses = stream.MissedBlocks;
        if (misses != aiSeenMisses)
        {
            if (!aiDropoutNotified)
            {
                aiDropoutNotified = true;
                NotifyAiProblem("AI voice can't keep up: it is muted until it does. A longer delay in Settings gives it more room.");
            }
            aiSeenMisses = misses;
            aiWarningUntil = DateTime.UtcNow + AiWarningHold;
        }
        if (!stream.IsLive || DateTime.UtcNow < aiWarningUntil)
        {
            ShowAiStatus("Can't keep up: the voice is muted until it does. Choose a longer delay in Settings, " +
                         "or give the GPU more room.", warn: true);
            return;
        }
        // Spare time: how long before its turn to play a block was ready (the real
        // limit is the allowance minus the audio read size, so this is the honest figure).
        string where = aiHost!.Backend == AiBackend.Cuda ? "GPU" : "CPU";
        float ms = stream.LastComputeMs;
        float spare = stream.MinRecentHeadroomMs;
        string text = ms <= 0 ? $"Running on the {where}"
            : float.IsNaN(spare) ? $"{where}: {ms:0} ms per block"
            : $"{where}: {ms:0} ms per block, {Math.Max(0, spare):0} ms to spare";
        // A note from loading (why the GPU is not used) stays in front while it runs.
        string note = aiHost!.Status;
        ShowAiStatus(note.Length > 0 ? $"{note} {text}" : text, warn: note.Length > 0);
    }

    /// <summary>
    /// Puts an AI voice problem in the main status line too, and in a tray balloon
    /// while the window is hidden, so a muted mic never goes unexplained.
    /// </summary>
    private void NotifyAiProblem(string text)
    {
        txtStatus.Text = text;
        AiHost.Log.Write(text);
        if (!IsVisible)
            trayIcon?.ShowBalloonTip(5000, "MicFX — AI voice", text, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void ShowAiStatus(string text, bool warn)
    {
        txtAiVoiceStatus.Text = text;
        txtAiVoiceStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        txtAiVoiceStatus.Foreground = (Brush)FindResource(warn ? "ErrorBrush" : "SubtleTextBrush");
    }

    private void AiVoiceLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!ChooseVoiceModel()) return;
        if (chkAiVoice.IsChecked == true) _ = StartAiVoiceAsync();
        else chkAiVoice.IsChecked = true; // its handler starts the voice
    }

    /// <summary>Asks for a voice model; true when one was chosen.</summary>
    private bool ChooseVoiceModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load a voice model — only voices you have the right to use",
            Filter = "Voice models (*.onnx)|*.onnx|RVC models before conversion (*.pth)|*.pth",
        };
        if (!string.IsNullOrEmpty(settings.AiVoiceModelPath))
            dialog.InitialDirectory = Path.GetDirectoryName(settings.AiVoiceModelPath);
        if (dialog.ShowDialog(this) != true) return false;
        if (dialog.FileName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "MicFX runs voice models in ONNX format. Convert the .pth file first with tools/convert_rvc.py " +
                "from the MicFX source code; the README explains the steps.",
                "MicFX — voice model", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        settings.AiVoiceModelPath = dialog.FileName;
        UpdateAiVoiceUi();
        return true;
    }

    private void AiVoicePitch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblAiVoicePitch == null) return;
        lblAiVoicePitch.Text = PitchLabel(e.NewValue);
        if (initializing || aiUiUpdating) return;
        settings.AiVoicePitch = (float)e.NewValue;
        if (aiHost != null) aiHost.Pitch = (float)e.NewValue;
    }

    private void AiVoiceSetup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || aiUiUpdating || comboAiBackend == null || comboAiDelay == null) return;
        settings.AiVoiceBackend = (comboAiBackend.SelectedItem as ComboBoxItem)?.Tag as string ?? "Cuda";
        settings.AiVoiceDelay = (comboAiDelay.SelectedItem as ComboBoxItem)?.Tag as string ?? "Normal";
        UpdateAiVoiceUi();
        if (chkAiVoice.IsChecked == true) _ = StartAiVoiceAsync();
    }

    private void AiVoiceLog_Click(object sender, RoutedEventArgs e)
    {
        string path = AiHost.Log.Path!;
        if (!File.Exists(path)) AiHost.Log.Write("Log opened");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowAiStatus("Could not open the log: " + ex.Message, warn: true);
        }
    }
}
