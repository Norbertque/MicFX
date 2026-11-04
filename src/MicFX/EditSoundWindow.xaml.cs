using System.Windows;
using System.Windows.Input;

namespace MicFX;

/// <summary>
/// Per-clip editor: name, volume, loop, custom global hotkey, preview.
/// Hotkeys require a modifier (Ctrl/Alt/Shift/Win) unless an F-key is used,
/// so a bare letter can never be hijacked system-wide.
/// </summary>
public partial class EditSoundWindow : Window
{
    public string SoundName => txtName.Text.Trim();
    public float ClipVolume => (float)sliderVolume.Value;
    public bool Loop => chkLoop.IsChecked == true;
    public bool SkipSilence => chkSkipSilence.IsChecked == true;
    /// <summary>Custom hotkey; 0/0 means the tile uses its default.</summary>
    public uint HotkeyMods { get; private set; }
    public uint HotkeyVk { get; private set; }

    private readonly (uint Mods, uint Vk) defaultHotkey;
    private readonly Action<float, bool>? preview;

    /// <param name="startSeconds">Measured silent lead-in; null if not analysed yet.</param>
    /// <param name="preview">Plays the clip with (volume, skip silence).</param>
    public EditSoundWindow(Window owner, string name, float volume, bool loop,
        uint hotkeyMods, uint hotkeyVk, (uint Mods, uint Vk) defaultHotkey,
        bool skipSilence, double? startSeconds, Action<float, bool>? preview)
    {
        InitializeComponent();
        Owner = owner;
        this.preview = preview;
        this.defaultHotkey = defaultHotkey;
        txtName.Text = name;
        sliderVolume.Value = volume;
        chkLoop.IsChecked = loop;
        chkSkipSilence.IsChecked = skipSilence;
        chkSkipSilence.Content = startSeconds switch
        {
            null => "Skip silence at the start (not measured yet)",
            0 => "Skip silence at the start (none found)",
            double s => $"Skip silence at the start ({s:0.00} s)"
        };
        HotkeyMods = hotkeyMods;
        HotkeyVk = hotkeyVk;
        ShowHotkey();
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblVolume != null) lblVolume.Text = $"{e.NewValue * 100:0}%";
    }

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        switch (HotkeyCapture.Read(e, out uint mods, out uint vk))
        {
            case HotkeyCapture.Outcome.Cleared:
                HotkeyMods = 0;
                HotkeyVk = 0;
                ShowHotkey();
                break;
            case HotkeyCapture.Outcome.Captured:
                HotkeyMods = mods;
                HotkeyVk = vk;
                ShowHotkey();
                break;
            case HotkeyCapture.Outcome.Rejected:
                lblHotkeyHint.Text = HotkeyCapture.RejectedHint;
                break;
        }
    }

    /// <summary>The effective hotkey, plus a warning if it blocks an AltGr character on this keyboard.</summary>
    private void ShowHotkey()
    {
        bool custom = HotkeyVk != 0;
        var (mods, vk) = custom ? (HotkeyMods, HotkeyVk) : defaultHotkey;
        if (vk == 0)
        {
            txtHotkey.Text = "(none)";
            lblHotkeyHint.Text = "";
            return;
        }
        string label = HotkeyNames.Describe(mods, vk);
        txtHotkey.Text = custom ? label : $"{label} (default)";
        string? warning = HotkeyNames.AltGrWarning(mods, vk);
        lblHotkeyHint.Text = warning == null ? "" : "⚠ " + warning;
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => preview?.Invoke(ClipVolume, SkipSilence);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SoundName.Length == 0) return;
        DialogResult = true;
    }
}
