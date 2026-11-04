using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicFX.Audio;
using Microsoft.Win32;

namespace MicFX;

public partial class MainWindow
{
    private readonly Dictionary<SoundTile, object> loopingClips = new();
    private HotkeyManager? hotkeys;
    private const int StopAllHotkeyId = 900; // tile hotkeys use their index, 0..n-1

    // ---------- soundboard volume ----------

    private void SoundVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initializing) return;
        engine.SetSoundboardVolume((float)e.NewValue);
    }

    // ---------- soundboard ----------

    private void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.wma;*.aiff;*.aif|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames)
        {
            sounds.Add(new SoundTile
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path
            });
        }
        RefreshTileIndexes();
        RefreshHotkeys();
        MeasureUnmeasuredClips();
    }

    /// <summary>
    /// Measures the loudness of any clip that has not been measured yet, off
    /// the UI thread (a long file takes a moment to read), then saves. Runs at
    /// startup for clips added by older versions and after adding new ones.
    /// </summary>
    private void MeasureUnmeasuredClips()
    {
        var pending = sounds.Where(t => t.LoudnessLufs == null && File.Exists(t.Path)).ToList();
        if (pending.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var tile in pending)
            {
                double? lufs = LoudnessMeter.MeasureFile(tile.Path);
                Dispatcher.BeginInvoke(() => tile.LoudnessLufs = lufs);
            }
            Dispatcher.BeginInvoke(() =>
            {
                CollectSettings();
                settings.Save();
            });
        });
    }

    /// <summary>Clip volume including loudness matching, when that is on and the clip is measured.</summary>
    private float EffectiveVolume(SoundTile tile, float volume) =>
        chkMatchLoudness.IsChecked == true && tile.LoudnessLufs is double lufs
            ? volume * LoudnessMeter.NormalizationGain(lufs)
            : volume;

    private void MatchLoudness_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        settings.MatchClipLoudness = chkMatchLoudness.IsChecked == true;
        if (settings.MatchClipLoudness) MeasureUnmeasuredClips();
    }

    private void EditSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not SoundTile tile) return;

        var dialog = new EditSoundWindow(this, tile.Name, tile.Volume, tile.Loop,
            tile.HotkeyMods, tile.HotkeyVk, DefaultHotkey(tile.Index),
            preview: volume => TryPreview(tile.Path, EffectiveVolume(tile, volume)));
        // Released while the dialog is open, so pressing an existing hotkey in
        // the hotkey box records it instead of playing a sound.
        hotkeys?.Clear();
        bool? ok = dialog.ShowDialog();
        engine.StopPreview();
        if (ok == true)
        {
            tile.Name = dialog.SoundName;
            tile.Volume = dialog.ClipVolume;
            tile.Loop = dialog.Loop;
            tile.HotkeyMods = dialog.HotkeyMods;
            tile.HotkeyVk = dialog.HotkeyVk;
        }
        RefreshHotkeys();
    }

    private void TryPreview(string path, float volume)
    {
        if (!File.Exists(path))
        {
            txtStatus.Text = $"File not found: {path}";
            return;
        }
        try
        {
            var device = comboMonitor.SelectedItem is AudioDeviceInfo info
                ? AudioDevices.GetDevice(info.Id)
                : null;
            engine.PreviewClip(path, volume, device);
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Preview error: " + ex.Message;
        }
    }

    private void RemoveSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is SoundTile tile)
        {
            if (loopingClips.TryGetValue(tile, out var handle))
                engine.StopClip(handle);
            sounds.Remove(tile);
            RefreshTileIndexes();
            RefreshHotkeys();
        }
    }

    private void SoundTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SoundTile tile)
            PlayTile(tile);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => engine.StopAllClips();

    private void OnHotkey(int id)
    {
        if (id == StopAllHotkeyId) engine.StopAllClips();
        else PlaySlot(id);
    }

    private void PlaySlot(int index)
    {
        if (index >= 0 && index < sounds.Count)
            PlayTile(sounds[index]);
    }

    private void PlayTile(SoundTile tile)
    {
        // A looping tile acts as a toggle.
        if (tile.IsLooping && loopingClips.TryGetValue(tile, out var running))
        {
            engine.StopClip(running);
            return;
        }

        if (!engine.IsRunning)
        {
            txtStatus.Text = "Start the engine to play sounds.";
            return;
        }
        if (!File.Exists(tile.Path))
        {
            txtStatus.Text = $"File not found: {tile.Path}";
            return;
        }
        try
        {
            var handle = engine.PlayClip(tile.Path, EffectiveVolume(tile, tile.Volume), tile.Loop);
            if (tile.Loop && handle != null)
            {
                loopingClips[tile] = handle;
                tile.IsLooping = true;
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Can't play \"{tile.Name}\": {ex.Message}";
        }
    }

    private void OnClipEndedUi(object handle)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var entry = loopingClips.FirstOrDefault(kv => ReferenceEquals(kv.Value, handle));
            if (entry.Key != null)
            {
                entry.Key.IsLooping = false;
                loopingClips.Remove(entry.Key);
            }
        });
    }

    private void RefreshTileIndexes()
    {
        for (int i = 0; i < sounds.Count; i++)
            sounds[i].Index = i;
    }

    // ---------- hotkeys ----------

    /// <summary>The first nine tiles default to Ctrl+Alt+1..9; the rest have none unless set.</summary>
    private static (uint Mods, uint Vk) DefaultHotkey(int index) =>
        index is >= 0 and < 9 ? (HotkeyManager.ModControl | HotkeyManager.ModAlt, (uint)('1' + index)) : (0u, 0u);

    private static (uint Mods, uint Vk) EffectiveHotkey(SoundTile tile) =>
        tile.HotkeyVk != 0 ? (tile.HotkeyMods, tile.HotkeyVk) : DefaultHotkey(tile.Index);

    private (uint Mods, uint Vk) StopAllHotkey =>
        settings.StopAllHotkeyVk != 0
            ? (settings.StopAllHotkeyMods, settings.StopAllHotkeyVk)
            : (HotkeyManager.ModControl | HotkeyManager.ModAlt, (uint)'0');

    /// <summary>
    /// Registers each tile's hotkey and the stop-all hotkey, and labels the
    /// tiles. A hotkey that is taken, or that blocks typing an AltGr character
    /// on this keyboard, gets a ⚠ with the reason in the tile's tooltip.
    /// </summary>
    private void RefreshHotkeys()
    {
        hotkeys?.Clear();
        for (int i = 0; i < sounds.Count; i++)
        {
            var tile = sounds[i];
            var (mods, vk) = EffectiveHotkey(tile);
            if (vk == 0)
            {
                tile.HotkeyLabel = "";
                tile.HotkeyWarning = null;
                continue;
            }
            tile.HotkeyLabel = HotkeyNames.Describe(mods, vk);
            tile.HotkeyWarning = hotkeys != null && !hotkeys.Register(i, mods, vk)
                ? $"{tile.HotkeyLabel} is already taken by another tile or app, so it does nothing here. " +
                  "Right-click the tile and choose Edit to pick another key."
                : HotkeyNames.AltGrWarning(mods, vk);
        }

        var (stopMods, stopVk) = StopAllHotkey;
        bool stopRegistered = hotkeys == null || hotkeys.Register(StopAllHotkeyId, stopMods, stopVk);
        ShowStopAllHotkey(stopRegistered ? null : "Already taken by another app, so it does nothing.");
    }

    private void ShowStopAllHotkey(string? problem)
    {
        var (mods, vk) = StopAllHotkey;
        txtStopAllHotkey.Text = HotkeyNames.Describe(mods, vk);
        string? hint = problem ?? HotkeyNames.AltGrWarning(mods, vk);
        lblStopAllHint.Text = hint == null ? "" : "⚠ " + hint;
        lblStopAllHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StopAllHotkey_KeyDown(object sender, KeyEventArgs e)
    {
        switch (HotkeyCapture.Read(e, out uint mods, out uint vk))
        {
            case HotkeyCapture.Outcome.Cleared:
                settings.StopAllHotkeyMods = 0;
                settings.StopAllHotkeyVk = 0;
                break;
            case HotkeyCapture.Outcome.Captured:
                settings.StopAllHotkeyMods = mods;
                settings.StopAllHotkeyVk = vk;
                break;
            case HotkeyCapture.Outcome.Rejected:
                lblStopAllHint.Text = HotkeyCapture.RejectedHint;
                lblStopAllHint.Visibility = Visibility.Visible;
                return;
            default:
                return;
        }
        ShowStopAllHotkey(null); // registered (and checked) again when the box loses focus
    }

    // While a hotkey box has focus, MicFX's own hotkeys are released, so the
    // combination being set is recorded instead of triggering a clip.
    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => hotkeys?.Clear();

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => RefreshHotkeys();
}
