using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MicFX;

/// <summary>
/// One soundboard tile. The properties the tile template shows raise
/// PropertyChanged, so labels and playback progress update in place instead
/// of the whole list being rebuilt.
/// </summary>
public class SoundTile : INotifyPropertyChanged
{
    private string name = "";
    private bool isLooping;
    private string hotkeyLabel = "";
    private string? hotkeyWarning;
    private bool isPlaying;
    private double progress;

    public string Name { get => name; set => Set(ref name, value); }
    public string Path { get; set; } = "";
    public float Volume { get; set; } = 1f;
    public bool Loop { get; set; }
    /// <summary>Custom hotkey (Win32 MOD_* flags + virtual key); 0/0 = the slot default.</summary>
    public uint HotkeyMods { get; set; }
    public uint HotkeyVk { get; set; }
    public double? LoudnessLufs { get; set; }
    /// <summary>Silent lead-in to skip; null until the clip has been analysed.</summary>
    public double? StartSeconds { get; set; }
    public bool SkipSilence { get; set; } = true;
    public int Index { get; set; }

    /// <summary>Where playback starts: after the lead-in, if skipping is on and it was measured.</summary>
    public double PlaybackStart => SkipSilence ? StartSeconds ?? 0 : 0;

    public bool IsPlaying { get => isPlaying; set => Set(ref isPlaying, value); }

    /// <summary>Playback position of the most recent play, 0..1.</summary>
    public double Progress { get => progress; set => Set(ref progress, value); }

    public bool IsLooping
    {
        get => isLooping;
        set { if (Set(ref isLooping, value)) OnPropertyChanged(nameof(StatusLabel)); }
    }

    /// <summary>The effective hotkey as printed on this keyboard; empty when the tile has none.</summary>
    public string HotkeyLabel
    {
        get => hotkeyLabel;
        set { if (Set(ref hotkeyLabel, value)) OnPropertyChanged(nameof(StatusLabel)); }
    }

    /// <summary>Why the hotkey is a problem (blocks an AltGr character, taken by another app); null if fine.</summary>
    public string? HotkeyWarning
    {
        get => hotkeyWarning;
        set { if (Set(ref hotkeyWarning, value)) OnPropertyChanged(nameof(StatusLabel)); }
    }

    public string StatusLabel =>
        IsLooping ? "⟳ looping — click to stop"
        : HotkeyWarning != null ? $"{HotkeyLabel} ⚠"
        : HotkeyLabel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
}
