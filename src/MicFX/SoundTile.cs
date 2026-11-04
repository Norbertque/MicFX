namespace MicFX;

public class SoundTile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public float Volume { get; set; } = 1f;
    public bool Loop { get; set; }
    public uint HotkeyMods { get; set; }
    public uint HotkeyVk { get; set; }
    public string? HotkeyText { get; set; }
    public double? LoudnessLufs { get; set; }
    public int Index { get; set; }
    public bool IsLooping { get; set; }

    public string StatusLabel =>
        IsLooping ? "⟳ looping — click to stop"
        : HotkeyText ?? (Index < 9 ? $"Ctrl+Alt+{Index + 1}" : "");
}
