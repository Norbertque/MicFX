using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MicFX.Audio;
using MicFX.Models;
using WinForms = System.Windows.Forms;

namespace MicFX;

public partial class MainWindow
{
    private bool profilesUpdating;

    // ---------- settings ----------

    private void ApplySettingsToUi()
    {
        sliderMicGain.Value = settings.MicGain;
        sliderMaster.Value = settings.MasterVolume;
        sliderSoundVol.Value = settings.SoundboardVolume;
        chkGate.IsChecked = settings.GateEnabled;
        sliderGate.Value = settings.GateThresholdDb;
        ShowDenoiseSettings(settings.DenoiseEnabled, settings.DenoiseMode, settings.VoiceGateEnabled);
        chkMatchLoudness.IsChecked = settings.MatchClipLoudness;
        chkComp.IsChecked = settings.CompressorEnabled;
        sliderComp.Value = settings.CompressorAmount;
        chkMonitor.IsChecked = settings.MonitorEnabled;
        sliderMonitorVoice.Value = settings.MonitorVoiceVolume;
        sliderMonitorSound.Value = settings.MonitorSoundVolume;
        engine.SetMonitorVolumes(settings.MonitorVoiceVolume, settings.MonitorSoundVolume);
        sliderEqBands.Value = settings.EqBandCount;
        lblEqBands.Text = settings.EqBandCount.ToString();
        foreach (ComboBoxItem item in comboLatency.Items)
        {
            if ((string)item.Tag == settings.LatencyMode)
            {
                item.IsSelected = true;
                break;
            }
        }
        engine.SetLatencyMode(settings.LatencyMode);


        foreach (var clip in settings.Sounds)
        {
            sounds.Add(new SoundTile
            {
                Name = clip.Name,
                Path = clip.Path,
                Volume = clip.Volume,
                Loop = clip.Loop,
                HotkeyMods = clip.HotkeyMods,
                HotkeyVk = clip.HotkeyVk,
                HotkeyText = clip.HotkeyText,
                LoudnessLufs = clip.LoudnessLufs
            });
        }
        RefreshTileIndexes();

        PopulateProfilesCombo(settings.ActiveProfile);

        // Push restored values into the engine so they apply on Start().
        engine.SetMicGain(settings.MicGain);
        engine.SetMasterVolume(settings.MasterVolume);
        engine.SetSoundboardVolume(settings.SoundboardVolume);
        engine.SetGate(settings.GateEnabled, settings.GateThresholdDb);
        PushDenoiseToEngine();
        engine.SetCompressor(settings.CompressorEnabled, settings.CompressorAmount);
        engine.SetEqBands(settings.EqBandCount, settings.EqGainsDb);
    }

    /// <summary>A device's name without the "(not connected)" placeholder suffix.</summary>
    private static string DeviceName(AudioDeviceInfo device) =>
        device.Name.EndsWith(AudioDevices.NotConnectedSuffix, StringComparison.Ordinal)
            ? device.Name[..^AudioDevices.NotConnectedSuffix.Length]
            : device.Name;

    private void CollectSettings()
    {
        settings.InputDeviceId = (comboMic.SelectedItem as AudioDeviceInfo)?.Id;
        if (comboMicPreferred.SelectedItem is AudioDeviceInfo preferred && preferred.Id.Length > 0)
        {
            settings.PreferredInputDeviceId = preferred.Id;
            settings.PreferredInputDeviceName = DeviceName(preferred);
        }
        else
        {
            settings.PreferredInputDeviceId = null;
            settings.PreferredInputDeviceName = null;
        }
        settings.OutputDeviceId = (comboOutput.SelectedItem as AudioDeviceInfo)?.Id;
        var monitor = comboMonitor.SelectedItem as AudioDeviceInfo;
        settings.MonitorDeviceId = monitor?.Id;
        settings.MonitorDeviceName = monitor == null ? null : DeviceName(monitor);
        settings.MonitorEnabled = chkMonitor.IsChecked == true;
        settings.MonitorVoiceVolume = (float)sliderMonitorVoice.Value;
        settings.MonitorSoundVolume = (float)sliderMonitorSound.Value;
        settings.MicGain = (float)sliderMicGain.Value;
        settings.MasterVolume = (float)sliderMaster.Value;
        settings.SoundboardVolume = (float)sliderSoundVol.Value;
        settings.GateEnabled = chkGate.IsChecked == true;
        settings.GateThresholdDb = (float)sliderGate.Value;
        StoreDenoiseSlider();
        settings.DenoiseEnabled = chkDenoise.IsChecked == true;
        settings.DenoiseMode = CurrentDenoiseMode();
        settings.VoiceGateEnabled = chkVoiceGate.IsChecked == true;
        settings.MatchClipLoudness = chkMatchLoudness.IsChecked == true;
        settings.CompressorEnabled = chkComp.IsChecked == true;
        settings.CompressorAmount = (float)sliderComp.Value;
        settings.EqBandCount = eqFrequencies.Length;
        settings.LatencyMode = CurrentLatencyMode();
        settings.ActiveProfile = comboProfile.SelectedItem as string;
        settings.Sounds = sounds
            .Select(s => new SoundClipSetting
            {
                Name = s.Name,
                Path = s.Path,
                Volume = s.Volume,
                Loop = s.Loop,
                HotkeyMods = s.HotkeyMods,
                HotkeyVk = s.HotkeyVk,
                HotkeyText = s.HotkeyText,
                LoudnessLufs = s.LoudnessLufs
            })
            .ToList();
    }

    // ---------- profiles ----------

    private void PopulateProfilesCombo(string? select)
    {
        profilesUpdating = true;
        comboProfile.Items.Clear();
        foreach (var profile in settings.Profiles)
            comboProfile.Items.Add(profile.Name);
        if (select != null && comboProfile.Items.Contains(select))
            comboProfile.SelectedItem = select;
        profilesUpdating = false;
    }

    private Profile CaptureProfile(string name) => new()
    {
        Name = name,
        MicGain = (float)sliderMicGain.Value,
        MasterVolume = (float)sliderMaster.Value,
        GateEnabled = chkGate.IsChecked == true,
        GateThresholdDb = (float)sliderGate.Value,
        DenoiseEnabled = chkDenoise.IsChecked == true,
        DenoiseStrengthDb = StoreDenoiseSlider().SpectralDb,
        AiMaxReductionDb = settings.AiMaxReductionDb,
        DenoiseMode = CurrentDenoiseMode(),
        VoiceGateEnabled = chkVoiceGate.IsChecked == true,
        CompressorEnabled = chkComp.IsChecked == true,
        CompressorAmount = (float)sliderComp.Value,
        EqGainsDb = eqSliders.Select(s => (float)s.Value).ToArray(),
    };

    private void ApplyProfile(Profile p)
    {
        sliderMicGain.Value = p.MicGain;
        sliderMaster.Value = p.MasterVolume;
        chkGate.IsChecked = p.GateEnabled;
        sliderGate.Value = p.GateThresholdDb;
        settings.DenoiseEnabled = p.DenoiseEnabled;
        settings.DenoiseMode = p.DenoiseMode;
        settings.VoiceGateEnabled = p.VoiceGateEnabled;
        settings.DenoiseStrengthDb = p.DenoiseStrengthDb;
        settings.AiMaxReductionDb = p.AiMaxReductionDb;
        ShowDenoiseSettings(p.DenoiseEnabled, p.DenoiseMode, p.VoiceGateEnabled);
        PushDenoiseToEngine();
        chkComp.IsChecked = p.CompressorEnabled;
        sliderComp.Value = p.CompressorAmount;
        var profileGains = MapGainsToBands(p.EqGainsDb, eqFrequencies);
        for (int i = 0; i < eqSliders.Count && i < profileGains.Length; i++)
            eqSliders[i].Value = profileGains[i];
        txtStatus.Text = $"Profile \"{p.Name}\" applied.";
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || profilesUpdating) return;
        if (comboProfile.SelectedItem is not string name) return;
        var profile = settings.Profiles.FirstOrDefault(p => p.Name == name);
        if (profile != null) ApplyProfile(profile);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (comboProfile.SelectedItem is not string name)
        {
            NewProfile_Click(sender, e);
            return;
        }
        int index = settings.Profiles.FindIndex(p => p.Name == name);
        if (index >= 0)
        {
            settings.Profiles[index] = CaptureProfile(name);
            settings.Save();
            txtStatus.Text = $"Profile \"{name}\" saved.";
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog(this, "New profile", "Profile name:",
            $"Profile {settings.Profiles.Count + 1}");
        if (dialog.ShowDialog() != true) return;

        string name = dialog.Value;
        int existing = settings.Profiles.FindIndex(p => p.Name == name);
        var profile = CaptureProfile(name);
        if (existing >= 0) settings.Profiles[existing] = profile;
        else settings.Profiles.Add(profile);
        settings.Save();
        PopulateProfilesCombo(name);
        txtStatus.Text = $"Profile \"{name}\" saved.";
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (comboProfile.SelectedItem is not string name) return;
        settings.Profiles.RemoveAll(p => p.Name == name);
        settings.Save();
        PopulateProfilesCombo(null);
        txtStatus.Text = $"Profile \"{name}\" deleted.";
    }

    // ---------- settings menu ----------

    private void RunOnBoot_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        try
        {
            StartupManager.SetEnabled(chkRunOnBoot.IsChecked == true);
            txtStatus.Text = chkRunOnBoot.IsChecked == true
                ? "MicFX will start minimized when you sign in to Windows."
                : "Removed from Windows startup.";
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Startup setting error: " + ex.Message;
        }
    }

    private string CurrentLatencyMode() =>
        (comboLatency.SelectedItem as ComboBoxItem)?.Tag as string ?? "Normal";

    private void About_Click(object sender, RoutedEventArgs e)
    {
        btnMenu.IsChecked = false;
        new AboutWindow(this).ShowDialog();
    }

    private void Latency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        settings.LatencyMode = CurrentLatencyMode();
        engine.SetLatencyMode(settings.LatencyMode);
        RestartIfRunning();
    }

    // ---------- theme color ----------

    private void BuildAccentSwatches()
    {
        foreach (var (name, hex) in Theme.Presets)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(Theme.Parse(hex)),
                Tag = hex,
                ToolTip = name
            };
            swatch.Click += (_, _) => SetAccent(hex);
            accentSwatches.Children.Add(swatch);
        }

        var custom = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            ToolTip = "Custom color…"
        };
        custom.Click += (_, _) => PickCustomAccent();
        accentSwatches.Children.Add(custom);
        RefreshAccentSwatches();
    }

    /// <summary>Rings the selected swatch; the last swatch shows a custom color or a "+".</summary>
    private void RefreshAccentSwatches()
    {
        string current = Theme.ToHex(Theme.Parse(settings.AccentColor));
        var ring = (Brush)FindResource("TextBrush");
        bool matchedPreset = false;
        foreach (var swatch in accentSwatches.Children.OfType<Button>())
        {
            if (swatch.Tag is string hex)
            {
                bool selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
                matchedPreset |= selected;
                swatch.BorderBrush = selected ? ring : Brushes.Transparent;
            }
            else
            {
                swatch.Background = matchedPreset
                    ? (Brush)FindResource("ElevatedBrush")
                    : new SolidColorBrush(Theme.Parse(current));
                swatch.Content = matchedPreset ? PlusGlyph() : null;
                swatch.BorderBrush = matchedPreset ? Brushes.Transparent : ring;
            }
        }
    }

    private System.Windows.Shapes.Path PlusGlyph() => new()
    {
        Data = Geometry.Parse("M 4,0 L 4,8 M 0,4 L 8,4"),
        Stroke = (Brush)FindResource("SubtleTextBrush"),
        StrokeThickness = 1.5,
        Width = 8,
        Height = 8
    };

    private void SetAccent(string hex)
    {
        settings.AccentColor = Theme.ToHex(Theme.Parse(hex));
        Theme.ApplyAccent(Theme.Parse(settings.AccentColor));
        RefreshAccentSwatches();
        CollectSettings();
        settings.Save();
    }

    private void PickCustomAccent()
    {
        var current = Theme.Parse(settings.AccentColor);
        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };
        var owner = new WinFormsOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) == WinForms.DialogResult.OK)
            SetAccent(Theme.ToHex(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
    }

    private sealed class WinFormsOwner(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
