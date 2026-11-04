using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MicFX.Audio;

namespace MicFX;

public partial class MainWindow
{
    private static readonly AudioDeviceInfo NoDevice = new("", "(none)");

    // Switch to the preferred mic as soon as it carries audio, but wait a while
    // before handing back, so a pause in speech is not mistaken for a
    // disconnect.
    private static readonly TimeSpan PreferredSoundWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SwitchBackAfterSilence = TimeSpan.FromSeconds(8);

    private DeviceWatcher? deviceWatcher;
    private DispatcherTimer? deviceDebounce;
    private DispatcherTimer? autoSwitchTimer;
    private readonly MicActivityProbe probe = new();
    private string? activeInputId;
    private string runningStatus = "";

    // Monitor recovery: a failed or missing headphone device is retried by
    // EnsureMonitor, but not more often than this allows.
    private DateTime nextMonitorAttempt;
    private static readonly TimeSpan MonitorRetryAfterReset = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MonitorRetryAfterError = TimeSpan.FromSeconds(5);
    private DateTime lastOutputRestart;

    // ---------- devices ----------

    private void PopulateDevices()
    {
        bool wasInitializing = initializing;
        initializing = true;

        var selectedMic = (comboMic.SelectedItem as AudioDeviceInfo)?.Id ?? settings.InputDeviceId;
        var selectedPreferred = (comboMicPreferred.SelectedItem as AudioDeviceInfo)?.Id
                                ?? settings.PreferredInputDeviceId;
        var selectedOut = (comboOutput.SelectedItem as AudioDeviceInfo)?.Id ?? settings.OutputDeviceId;
        var selectedMon = (comboMonitor.SelectedItem as AudioDeviceInfo)?.Id
                          ?? settings.MonitorDeviceId
                          ?? AudioDevices.GetDefaultRenderDeviceId();

        var captureDevices = AudioDevices.GetCaptureDevices();
        comboMic.ItemsSource = captureDevices;

        // The auto-switch and monitor lists keep a remembered device even while
        // it is unplugged or switched off, so the choice survives until it is back.
        comboMicPreferred.ItemsSource = AudioDevices.WithRemembered(
            captureDevices.Prepend(NoDevice), selectedPreferred, settings.PreferredInputDeviceName);

        var renderDevices = AudioDevices.GetRenderDevices();
        comboOutput.ItemsSource = renderDevices;
        comboMonitor.ItemsSource = AudioDevices.WithRemembered(
            renderDevices, selectedMon, settings.MonitorDeviceName);
        // Learn the name while the device is visible, for the placeholder later.
        if (renderDevices.FirstOrDefault(d => d.Id == selectedMon) is { } monitorDevice)
            settings.MonitorDeviceName = monitorDevice.Name;

        SelectById(comboMic, selectedMic);
        SelectById(comboOutput, selectedOut);
        SelectById(comboMonitor, selectedMon);
        SelectById(comboMicPreferred, selectedPreferred);
        if (comboMicPreferred.SelectedItem == null)
            comboMicPreferred.SelectedItem = NoDevice;

        if (comboMic.SelectedItem == null)
            SelectById(comboMic, AudioDevices.GetDefaultCaptureDeviceId());

        initializing = wasInitializing;
    }

    private static void SelectById(ComboBox combo, string? id)
    {
        if (id == null) return;
        foreach (AudioDeviceInfo item in combo.Items)
        {
            if (item.Id == id)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    // ---------- engine control ----------

    private void TryAutoStart()
    {
        if (comboMic.SelectedItem != null && comboOutput.SelectedItem != null)
            StartEngine();
    }

    /// <summary>
    /// The mic to actually run with: the auto-switch device while it is
    /// carrying audio, otherwise the primary one.
    /// </summary>
    private AudioDeviceInfo? ResolveInputDevice()
    {
        var available = AudioDevices.GetCaptureDevices();
        var ids = available.Select(d => d.Id).ToHashSet();
        string? primary = (comboMic.SelectedItem as AudioDeviceInfo)?.Id;
        string? preferred = PreferredInputId();
        string? chosen = AudioDevices.ChooseInput(preferred, primary, ids, PreferredHasSound(preferred));
        return chosen == null ? null : available.First(d => d.Id == chosen);
    }

    private string? PreferredInputId()
    {
        string? id = (comboMicPreferred.SelectedItem as AudioDeviceInfo)?.Id;
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// Whether the preferred mic is currently carrying audio. While it is the
    /// active input the engine's own stream answers that; otherwise the probe
    /// listening to it does.
    /// </summary>
    private bool PreferredHasSound(string? preferredId)
    {
        if (preferredId == null) return false;
        if (engine.IsRunning && activeInputId == preferredId)
            return engine.SinceInputSound < SwitchBackAfterSilence;
        return probe.DeviceId == preferredId && probe.HasSoundWithin(PreferredSoundWindow);
    }

    /// <summary>
    /// Keeps a probe on the preferred mic whenever it is not the one in use, so
    /// its activity can be seen without routing it anywhere.
    /// </summary>
    private void UpdateProbe()
    {
        string? preferred = PreferredInputId();
        if (preferred == null || (engine.IsRunning && activeInputId == preferred))
        {
            probe.Stop();
            return;
        }
        try
        {
            probe.Watch(AudioDevices.GetDevice(preferred));
        }
        catch
        {
            probe.Stop(); // device vanished between enumeration and opening
        }
    }

    private void EvaluateAutoSwitch()
    {
        if (!engine.IsRunning || PreferredInputId() == null) return;
        var chosen = ResolveInputDevice();
        if (chosen != null && chosen.Id != activeInputId)
            StartEngine();
        else
            UpdateProbe();
    }

    private void StartEngine()
    {
        if (comboOutput.SelectedItem is not AudioDeviceInfo render)
        {
            txtStatus.Text = "Select an output device first.";
            return;
        }
        if (ResolveInputDevice() is not AudioDeviceInfo mic)
        {
            txtStatus.Text = "Select a microphone first.";
            return;
        }

        if (AudioDevices.IsVirtualCableLoop(mic.Name, render.Name))
        {
            StopEngine();
            txtStatus.Text = $"\"{mic.Name}\" is the other end of \"{render.Name}\" — " +
                             "capturing it would feed the output back into itself. Pick a real microphone.";
            return;
        }

        try
        {
            engine.Start(AudioDevices.GetDevice(mic.Id), AudioDevices.GetDevice(render.Id));
            activeInputId = mic.Id;
            UpdateProbe();
            ApplyMonitor();
            btnStartStop.Content = "Stop";
            SetStatusDot("running");
            runningStatus = $"Running — {mic.Name}  →  {render.Name}  (~{engine.EstimatedLatencyMs} ms)";
            if (engine.CaptureNote != null)
                runningStatus += $"  ({engine.CaptureNote})";
            txtStatus.Text = runningStatus;
        }
        catch (Exception ex)
        {
            engine.Stop();
            activeInputId = null;
            btnStartStop.Content = "Start";
            SetStatusDot("error");
            txtStatus.Text = "Error: " + ex.Message;
        }
    }

    /// <summary>Colours the header dot: running, stopped or failed.</summary>
    private void SetStatusDot(string state) =>
        statusDot.Fill = (Brush)FindResource(state switch
        {
            "running" => "MeterBrush",
            "error" => "ErrorBrush",
            _ => "IdleBrush"
        });

    /// <summary>
    /// Watches for devices appearing and disappearing so the preferred mic can
    /// take over (and hand back) without touching anything downstream.
    /// Notifications arrive on a COM thread and can burst, so they are
    /// marshalled to the UI thread and debounced.
    /// </summary>
    private void StartDeviceWatcher()
    {
        deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        deviceDebounce.Tick += (_, _) =>
        {
            deviceDebounce!.Stop();
            OnDevicesChanged();
        };

        deviceWatcher = new DeviceWatcher();
        deviceWatcher.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            deviceDebounce!.Stop();
            deviceDebounce.Start();
        });

        // Presence changes alone cannot tell whether a virtual mic is live, so
        // poll its activity as well.
        autoSwitchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        autoSwitchTimer.Tick += (_, _) =>
        {
            EvaluateAutoSwitch();
            EnsureMonitor();
        };
        autoSwitchTimer.Start();
        UpdateProbe();
    }

    private void OnDevicesChanged()
    {
        PopulateDevices();
        if (!engine.IsRunning) return;

        var chosen = ResolveInputDevice();
        if (chosen == null)
        {
            StopEngine();
            txtStatus.Text = "Microphone disconnected.";
            return;
        }
        if (chosen.Id != activeInputId)
            StartEngine(); // restarts on the newly preferred/available mic
        else
            EnsureMonitor(); // e.g. the headphones were just switched on
    }

    private void StopEngine()
    {
        engine.Stop();
        btnStartStop.Content = "Start";
        SetStatusDot("idle");
        txtStatus.Text = "Stopped";
        meterIn.Value = 0;
        meterOut.Value = 0;
    }

    private void RestartIfRunning()
    {
        if (engine.IsRunning) StartEngine();
    }

    private void ApplyMonitor()
    {
        if (!engine.IsRunning) return;
        var info = comboMonitor.SelectedItem as AudioDeviceInfo;
        if (chkMonitor.IsChecked != true || info == null || !AudioDevices.IsActiveRender(info.Id))
        {
            // Off, or the headphones aren't connected right now (the list shows
            // them as "not connected"); EnsureMonitor opens them once they are.
            engine.SetMonitor(null, false);
            return;
        }
        try
        {
            engine.SetMonitor(AudioDevices.GetDevice(info.Id), true);
            if (txtStatus.Text.StartsWith("Monitor error", StringComparison.Ordinal))
                txtStatus.Text = runningStatus;
        }
        catch (Exception ex)
        {
            nextMonitorAttempt = DateTime.UtcNow + MonitorRetryAfterError;
            txtStatus.Text = "Monitor error: " + ex.Message;
        }
    }

    /// <summary>
    /// Opens the monitor when it should be playing but isn't: the headphones
    /// were switched on after MicFX started, or their stream died (power
    /// cycle, format change). Runs every second, so it does nothing — not even
    /// a device lookup — while the monitor is playing on the selected device.
    /// </summary>
    private void EnsureMonitor()
    {
        if (!engine.IsRunning || chkMonitor.IsChecked != true) return;
        if (comboMonitor.SelectedItem is not AudioDeviceInfo info) return;
        if (engine.MonitorPlaying && engine.MonitorDeviceId == info.Id) return;
        if (DateTime.UtcNow < nextMonitorAttempt || !AudioDevices.IsActiveRender(info.Id)) return;
        ApplyMonitor();
    }

    private void OnMonitorFailed()
    {
        // A device that reset (format change, power blip) is usually back
        // within a moment; one that was switched off returns via the watcher.
        nextMonitorAttempt = DateTime.UtcNow + MonitorRetryAfterReset;
    }

    /// <summary>
    /// The virtual cable stream died (e.g. its format was changed). Restart
    /// the engine to reopen it, but give up if it keeps failing right away.
    /// </summary>
    private void OnOutputFailed(Exception ex)
    {
        if (!engine.IsRunning) return;
        if (DateTime.UtcNow - lastOutputRestart < TimeSpan.FromSeconds(5))
        {
            StopEngine();
            SetStatusDot("error");
            txtStatus.Text = "Output device keeps failing: " + ex.Message;
            return;
        }
        lastOutputRestart = DateTime.UtcNow;
        StartEngine();
    }

    private void CheckCableSetup()
    {
        var cable = comboOutput.Items.Cast<AudioDeviceInfo>()
            .FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

        if (cable != null)
        {
            if (comboOutput.SelectedItem == null)
            {
                SelectById(comboOutput, cable.Id);
                TryAutoStart();
            }
            return;
        }

        if (settings.CableNoticeDismissed) return;

        ShowNotice(
            "No virtual cable found. MicFX needs one so Discord/OBS can use the processed mic — " +
            "install the free VB-Audio Cable (run as admin, then reboot).",
            "Get VB-Cable", () => OpenUrl("https://vb-audio.com/Cable/"),
            "Re-check", () =>
            {
                PopulateDevices();
                var found = comboOutput.Items.Cast<AudioDeviceInfo>()
                    .FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    SelectById(comboOutput, found.Id);
                    HideNotice();
                    txtStatus.Text = "Virtual cable found and selected as output.";
                    TryAutoStart();
                }
            },
            onClose: () => settings.CableNoticeDismissed = true);
    }

    // ---------- device and monitor controls ----------

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (engine.IsRunning) StopEngine();
        else StartEngine();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => PopulateDevices();

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        RestartIfRunning();
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        ApplyMonitor();
    }

    private void MonitorVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblMonitorVoice != null) lblMonitorVoice.Text = $"{sliderMonitorVoice.Value * 100:0}%";
        if (lblMonitorSound != null) lblMonitorSound.Text = $"{sliderMonitorSound.Value * 100:0}%";
        if (initializing) return;
        engine.SetMonitorVolumes((float)sliderMonitorVoice.Value, (float)sliderMonitorSound.Value);
    }
}
