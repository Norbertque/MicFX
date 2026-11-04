using System.Runtime.InteropServices;
using System.Windows.Input;

namespace MicFX;

/// <summary>
/// Readable hotkey names for the current keyboard layout, and the check for
/// hotkeys that collide with AltGr characters.
///
/// Names are always built from the stored modifier flags and virtual key, so
/// labels saved by older versions ("D1", "Oem3") no longer show up. Keys that
/// type a character show that character as the active layout prints it
/// (e.g. "ů" on Czech instead of "Oem1"). Digits are shown as digits even on
/// layouts whose number row types other characters unshifted (Czech "+ěšč…"),
/// because that is what is printed on the key.
/// </summary>
public static class HotkeyNames
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    // The output buffer is not always null-terminated: read exactly the
    // number of characters the call returns.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    private const uint MapVkToScanCode = 0;
    private const uint MapVkToChar = 2;
    private const uint DontChangeKeyboardState = 0x4; // ToUnicodeEx flag, Windows 10 1607+

    public static string Describe(uint mods, uint vk)
    {
        var parts = new List<string>(5);
        if ((mods & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((mods & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((mods & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((mods & HotkeyManager.ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= '0' and <= '9' or >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Num{vk - 0x60}";
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";

        string? named = vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock",
            0x1B => "Esc", 0x20 => "Space", 0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2C => "PrtSc",
            0x2D => "Insert", 0x2E => "Delete", 0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-",
            0x6E => "Num.", 0x6F => "Num/", 0x90 => "NumLock", 0x91 => "ScrollLock",
            _ => null
        };
        if (named != null) return named;

        // Punctuation and national keys: the character this layout types.
        // The top bit marks a dead key (an accent waiting for the next letter).
        uint ch = MapVirtualKeyEx(vk, MapVkToChar, GetKeyboardLayout(0)) & 0x7FFF_FFFF;
        if (ch is >= 0x21 and <= 0xFFFF && !char.IsControl((char)ch))
            return ((char)ch).ToString();

        return KeyInterop.KeyFromVirtualKey((int)vk).ToString();
    }

    /// <summary>
    /// The character a Ctrl+Alt hotkey would type on the current layout, or
    /// null if it types none. Windows treats Ctrl+Alt as AltGr, so a global
    /// Ctrl+Alt+1 also swallows AltGr+1 (~ on Czech) in every app while MicFX
    /// runs. Dead keys such as ˇ count too.
    /// </summary>
    public static string? AltGrCharacter(uint mods, uint vk)
    {
        const uint ctrlAlt = HotkeyManager.ModControl | HotkeyManager.ModAlt;
        if ((mods & ctrlAlt) != ctrlAlt || (mods & HotkeyManager.ModWin) != 0) return null;

        var state = new byte[256];
        state[0x11] = state[0xA2] = 0x80; // VK_CONTROL, VK_LCONTROL
        state[0x12] = state[0xA5] = 0x80; // VK_MENU, VK_RMENU: the AltGr combination
        if ((mods & HotkeyManager.ModShift) != 0)
            state[0x10] = state[0xA0] = 0x80; // VK_SHIFT, VK_LSHIFT

        var layout = GetKeyboardLayout(0);
        uint scanCode = MapVirtualKeyEx(vk, MapVkToScanCode, layout);
        var buffer = new char[8];
        // Leave the keyboard state alone, so probing a dead key doesn't change
        // what the user types next. A dead key returns -1 with its spacing
        // form (e.g. ˇ) in the first slot.
        int n = ToUnicodeEx(vk, scanCode, state, buffer, buffer.Length, DontChangeKeyboardState, layout);
        if (n == 0) return null;
        string typed = new string(buffer, 0, n < 0 ? 1 : Math.Min(n, buffer.Length));
        return typed.Length == 0 || typed.Any(char.IsControl) ? null : typed;
    }

    /// <summary>Tooltip text for a hotkey that blocks an AltGr character, or null.</summary>
    public static string? AltGrWarning(uint mods, uint vk)
    {
        string? typed = AltGrCharacter(mods, vk);
        if (typed == null) return null;
        string label = Describe(mods, vk);
        string altGr = ((mods & HotkeyManager.ModShift) != 0 ? "AltGr+Shift+" : "AltGr+") + KeyName(vk);
        return $"On your keyboard {label} is also {altGr}, which types {typed}. " +
               $"While MicFX runs you can't type {typed} anywhere. Pick another key if you need it.";
    }
}
