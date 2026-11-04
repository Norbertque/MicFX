using System.IO;
using System.Windows;
using System.Windows.Controls;
using MicFX.Audio;
using Microsoft.Win32;

namespace MicFX;

public partial class MainWindow
{
    private readonly Dictionary<SoundTile, object> loopingClips = new();
    private HotkeyManager? hotkeys;

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
            tile.HotkeyMods, tile.HotkeyVk, tile.HotkeyText,
            preview: volume => TryPreview(tile.Path, EffectiveVolume(tile, volume)));
        bool? ok = dialog.ShowDialog();
        engine.StopPreview();
        if (ok != true) return;

        tile.Name = dialog.SoundName;
        tile.Volume = dialog.ClipVolume;
        tile.Loop = dialog.Loop;
        tile.HotkeyMods = dialog.HotkeyMods;
        tile.HotkeyVk = dialog.HotkeyVk;
        tile.HotkeyText = dialog.HotkeyText;
        RefreshTileIndexes();
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
                icSounds.Items.Refresh();
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
                icSounds.Items.Refresh();
            }
        });
    }

    private void RefreshTileIndexes()
    {
        for (int i = 0; i < sounds.Count; i++)
            sounds[i].Index = i;
        icSounds.Items.Refresh();
    }

    private void RefreshHotkeys()
    {
        if (hotkeys == null) return;
        hotkeys.Clear();
        for (int i = 0; i < sounds.Count; i++)
        {
            var tile = sounds[i];
            if (tile.HotkeyVk != 0)
                hotkeys.Register(i, tile.HotkeyMods, tile.HotkeyVk);
            else if (i < 9)
                hotkeys.Register(i, HotkeyManager.ModControl | HotkeyManager.ModAlt, (uint)('1' + i));
        }
    }
}
