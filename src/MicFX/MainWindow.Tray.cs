using System.Windows;
using WinForms = System.Windows.Forms;

namespace MicFX;

public partial class MainWindow
{
    private WinForms.NotifyIcon? trayIcon;
    private WinForms.ToolStripMenuItem? trayProfilesMenu;

    // ---------- tray ----------

    private void CreateTrayIcon()
    {
        using var iconStream =
            Application.GetResourceStream(new Uri("pack://application:,,,/Assets/micfx.ico"))!.Stream;
        trayIcon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Visible = true,
            Text = "MicFX — microphone processor"
        };
        trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open MicFX", null, (_, _) => ShowFromTray());
        trayProfilesMenu = new WinForms.ToolStripMenuItem("Profiles");
        menu.Items.Add(trayProfilesMenu);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ForceExit());
        menu.Opening += (_, _) => RebuildTrayProfiles();
        trayIcon.ContextMenuStrip = menu;
    }

    private void RebuildTrayProfiles()
    {
        if (trayProfilesMenu == null) return;
        trayProfilesMenu.DropDownItems.Clear();
        if (settings.Profiles.Count == 0)
        {
            trayProfilesMenu.DropDownItems.Add(
                new WinForms.ToolStripMenuItem("(no profiles yet)") { Enabled = false });
            return;
        }
        foreach (var profile in settings.Profiles)
        {
            string name = profile.Name;
            trayProfilesMenu.DropDownItems.Add(name, null,
                (_, _) => Dispatcher.BeginInvoke(() => comboProfile.SelectedItem = name));
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Bypasses close-to-tray and really shuts the app down.</summary>
    public void ForceExit()
    {
        reallyExit = true;
        Close();
    }
}
