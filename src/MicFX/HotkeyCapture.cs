using System.Windows.Input;

namespace MicFX;

/// <summary>
/// Turns a key press in a hotkey box into Win32 modifier flags and a virtual
/// key. Shared by the clip editor and the stop-all setting. A modifier
/// (Ctrl/Alt/Shift/Win) is required unless an F-key is used, so a bare letter
/// can never be taken over system-wide.
/// </summary>
public static class HotkeyCapture
{
    public enum Outcome { Ignored, Cleared, Rejected, Captured }

    public static Outcome Read(KeyEventArgs e, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Escape or Key.Back or Key.Delete)
            return Outcome.Cleared;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return Outcome.Ignored; // modifier alone: wait for the real key

        var modifiers = Keyboard.Modifiers;
        bool isFKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFKey)
            return Outcome.Rejected;

        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyManager.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyManager.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyManager.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyManager.ModWin;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return Outcome.Captured;
    }

    public const string RejectedHint = "Combine with Ctrl/Alt/Shift, or use an F-key.";
}
