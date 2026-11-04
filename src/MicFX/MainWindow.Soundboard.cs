using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MicFX.Audio;
using MicFX.Models;
using Microsoft.Win32;

namespace MicFX;

public partial class MainWindow
{
    private readonly Dictionary<SoundTile, object> loopingClips = new();
    // Every clip that is playing, oldest first: the newest play of a tile drives its progress bar.
    private readonly List<(object Handle, SoundTile Tile)> playingClips = new();
    private DispatcherTimer? progressTimer;
    private readonly SoundLibrary library = SoundLibrary.Default;
    private readonly object libraryLock = new();
    private HotkeyManager? hotkeys;
    private const int StopAllHotkeyId = 900; // tile hotkeys use their index, 0..n-1

    /// <summary>Formats the Add dialog offers and drag and drop accepts.</summary>
    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".m4a", ".wma", ".aiff", ".aif" };

    // ---------- soundboard volume ----------

    private void SoundVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initializing) return;
        engine.SetSoundboardVolume((float)e.NewValue);
    }

    // ---------- adding clips ----------

    private void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"Audio files|{string.Join(";", AudioExtensions.Select(x => "*" + x))}|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            AddSounds(dialog.FileNames);
    }

    private void AddSounds(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            sounds.Add(new SoundTile
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path
            });
            added++;
        }
        if (added == 0) return;
        RefreshTileIndexes();
        RefreshHotkeys();
        PrepareClips();
    }

    private static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Dropped audio files, plus the audio files directly inside dropped folders.</summary>
    private static List<string> DroppedAudioFiles(IDataObject data)
    {
        var files = new List<string>();
        if (data.GetData(DataFormats.FileDrop) is not string[] items) return files;
        foreach (var item in items)
        {
            if (Directory.Exists(item))
                files.AddRange(Directory.EnumerateFiles(item).Where(IsAudioFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (IsAudioFile(item))
                files.Add(item);
        }
        return files;
    }

    private void Soundboard_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        dropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Soundboard_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between the tiles: only hide the
        // hint once the pointer has really left the soundboard.
        var p = e.GetPosition(soundboardBox);
        if (p.X <= 0 || p.Y <= 0 || p.X >= soundboardBox.ActualWidth || p.Y >= soundboardBox.ActualHeight)
            dropHint.Visibility = Visibility.Collapsed;
    }

    private void Soundboard_Drop(object sender, DragEventArgs e)
    {
        dropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        var files = DroppedAudioFiles(e.Data);
        if (files.Count == 0)
        {
            txtStatus.Text = $"Nothing to add: the soundboard takes {string.Join(", ", AudioExtensions)} files.";
            return;
        }
        AddSounds(files);
    }

    // ---------- clip upkeep ----------

    /// <summary>
    /// Background upkeep, once per clip: copies it into MicFX's library (so
    /// moving or deleting the original doesn't break the tile) and analyses
    /// it for loudness matching and the silent lead-in. Runs at startup, which
    /// also covers clips added by older versions, and after adding clips;
    /// saves when done.
    /// </summary>
    private void PrepareClips()
    {
        var pending = sounds
            .Where(t => File.Exists(t.Path)
                        && (!library.Contains(t.Path) || t.LoudnessLufs == null || t.StartSeconds == null))
            .Select(t => (Tile: t, Path: t.Path, Analyse: t.LoudnessLufs == null || t.StartSeconds == null))
            .ToList();
        if (pending.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var (tile, path, analyse) in pending)
            {
                string copy = path;
                try
                {
                    lock (libraryLock) copy = library.Import(path);
                }
                catch
                {
                    // Disk full, file locked...: keep playing the original.
                }
                var analysis = analyse ? LoudnessMeter.AnalyzeFile(copy) : null;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!sounds.Contains(tile))
                    {
                        // Removed while this ran: drop the copy unless another tile uses it.
                        if (copy != path) ReleaseCopy(copy);
                        return;
                    }
                    if (tile.Path == path) tile.Path = copy;
                    if (analysis is { } a)
                    {
                        tile.LoudnessLufs = a.Lufs;
                        tile.StartSeconds = a.StartSeconds;
                    }
                });
            }
            Dispatcher.BeginInvoke(() =>
            {
                CollectSettings();
                settings.Save();
            });
        });
    }

    /// <summary>Deletes a library copy that no tile plays any more.</summary>
    private void ReleaseCopy(string path)
    {
        if (sounds.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            lock (libraryLock) library.Release(path);
        }
        catch
        {
            // Still open somewhere: it stays, unused.
        }
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
        if (settings.MatchClipLoudness) PrepareClips();
    }

    // ---------- editing and removing ----------

    private void EditSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not SoundTile tile) return;

        var dialog = new EditSoundWindow(this, tile.Name, tile.Volume, tile.Loop,
            tile.HotkeyMods, tile.HotkeyVk, DefaultHotkey(tile.Index), tile.SkipSilence, tile.StartSeconds,
            preview: (volume, skipSilence) => TryPreview(tile.Path, EffectiveVolume(tile, volume),
                skipSilence ? tile.StartSeconds ?? 0 : 0));
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
            tile.SkipSilence = dialog.SkipSilence;
        }
        RefreshHotkeys();
    }

    private void TryPreview(string path, float volume, double startSeconds)
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
            engine.PreviewClip(path, volume, device, startSeconds);
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Preview error: " + ex.Message;
        }
    }

    private void RemoveSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not SoundTile tile) return;
        // Stop it first: a playing clip keeps its file open.
        foreach (var (handle, _) in playingClips.Where(c => c.Tile == tile).ToList())
            engine.StopClip(handle);
        sounds.Remove(tile);
        ReleaseCopy(tile.Path);
        RefreshTileIndexes();
        RefreshHotkeys();
        CollectSettings();
        settings.Save();
    }

    // ---------- playback ----------

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
            var handle = engine.PlayClip(tile.Path, EffectiveVolume(tile, tile.Volume), tile.Loop, tile.PlaybackStart);
            if (handle == null) return;
            playingClips.Add((handle, tile));
            tile.Progress = 0;
            tile.IsPlaying = true;
            StartProgressTimer();
            if (tile.Loop)
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
            int index = playingClips.FindIndex(c => ReferenceEquals(c.Handle, handle));
            if (index >= 0)
            {
                var tile = playingClips[index].Tile;
                playingClips.RemoveAt(index);
                if (!playingClips.Any(c => c.Tile == tile))
                {
                    tile.IsPlaying = false;
                    tile.Progress = 0;
                }
            }
            var entry = loopingClips.FirstOrDefault(kv => ReferenceEquals(kv.Value, handle));
            if (entry.Key != null)
            {
                entry.Key.IsLooping = false;
                loopingClips.Remove(entry.Key);
            }
        });
    }

    private void StartProgressTimer()
    {
        if (progressTimer == null)
        {
            progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
            progressTimer.Tick += (_, _) => UpdateProgress();
        }
        progressTimer.Start();
    }

    /// <summary>Moves the progress bars (~15 fps); the timer stops itself when nothing plays.</summary>
    private void UpdateProgress()
    {
        if (playingClips.Count == 0)
        {
            progressTimer?.Stop();
            return;
        }
        if (!IsVisible) return;
        foreach (var (handle, tile) in playingClips) // newest last, so it wins
        {
            if (engine.ClipProgress(handle) is double progress)
                tile.Progress = progress;
        }
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
