using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MicFX.Audio;
using MicFX.Models;
using WinForms = System.Windows.Forms;

namespace MicFX;

/// <summary>
/// The main window. Its code is split by area into MainWindow.*.cs partial
/// files: Devices, Processing, Soundboard, Settings, Updates and Tray.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AudioEngine engine = new();
    private readonly AppSettings settings = AppSettings.Load();
    private readonly ObservableCollection<SoundTile> sounds = new();
    private const double MinWindowHeight = 520;
    // The EQ grows with the window up to EqMaxHeight (enough slider travel for
    // fine adjustments); beyond that the soundboard gets the extra height.
    private const double EqMaxHeight = 400;
    private const double SoundboardMinHeight = 200;
    private bool startHeightSet;
    private bool initializing = true;
    private bool reallyExit;
    private bool trayTipShown;

    public MainWindow()
    {
        Theme.ApplyAccent(Theme.Parse(settings.AccentColor));
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Title = $"MicFX v{version.Major}.{version.Minor}.{version.Build} — Microphone Processor";
        txtVersion.Text = $"MicFX v{version.Major}.{version.Minor}.{version.Build}";
        CleanUpAfterUpdate();

        settings.EqBandCount = Math.Clamp(settings.EqBandCount,
            EqualizerSampleProvider.MinBands, EqualizerSampleProvider.MaxBands);
        if (settings.EqGainsDb.Length != settings.EqBandCount)
        {
            var resized = new float[settings.EqBandCount];
            Array.Copy(settings.EqGainsDb, resized, Math.Min(settings.EqGainsDb.Length, resized.Length));
            settings.EqGainsDb = resized;
        }
        eqFrequencies = EqualizerSampleProvider.BuildFrequencies(settings.EqBandCount);
        BuildEqSliders(settings.EqGainsDb);
        BuildSpectrumBars();
        BuildAccentSwatches();
        icSounds.ItemsSource = sounds;
        engine.LevelsAvailable += OnLevels;
        engine.ClipEnded += OnClipEndedUi;
        engine.MonitorFailed += _ => Dispatcher.BeginInvoke(OnMonitorFailed);
        engine.OutputFailed += ex => Dispatcher.BeginInvoke(() => OnOutputFailed(ex));

        comboEqPreset.Items.Add("Presets…");
        foreach (var name in EqPresets.Keys)
            comboEqPreset.Items.Add(name);
        comboEqPreset.SelectedIndex = 0;

        PopulateDevices();
        ApplySettingsToUi();
        try { chkRunOnBoot.IsChecked = StartupManager.IsEnabled(); } catch { }
        initializing = false;

        // Everything below must not depend on the window being visible —
        // with --minimized (run on boot) the window starts hidden in the tray.
        CreateTrayIcon();
        StartDeviceWatcher();
        hotkeys = new HotkeyManager(this);
        hotkeys.HotkeyPressed += id => Dispatcher.BeginInvoke(() => OnHotkey(id));
        RefreshHotkeys();
        // Key names and AltGr clashes depend on the keyboard layout.
        System.Windows.Input.InputLanguageManager.Current.InputLanguageChanged += (_, _) => RefreshHotkeys();
        StartSpectrumTimer();
        TryAutoStart();
        CheckCableSetup();
        PrepareClips();
        _ = CheckForUpdatesAsync();
        if (Environment.GetCommandLineArgs().Contains("--updated"))
            txtStatus.Text = $"Updated to v{version.Major}.{version.Minor}.{version.Build} ✓";

        mainArea.SizeChanged += (_, _) => FitEqHeight();
        // Run after the first full layout pass, so the left column has been
        // measured and DesiredSize is meaningful.
        Loaded += (_, _) => Dispatcher.BeginInvoke(UpdateMinHeight, DispatcherPriority.Loaded);
        noticeBar.IsVisibleChanged += (_, _) =>
            Dispatcher.BeginInvoke(UpdateMinHeight, DispatcherPriority.Loaded);

        Closing += (_, e) =>
        {
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
                if (!trayTipShown && trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2500, "MicFX is still running",
                        "Your mic keeps processing in the background. Right-click the tray icon to exit.",
                        WinForms.ToolTipIcon.Info);
                    trayTipShown = true;
                }
                return;
            }
            CollectSettings();
            settings.Save();
            autoSwitchTimer?.Stop();
            deviceDebounce?.Stop();
            deviceWatcher?.Dispose();
            probe.Dispose();
            hotkeys?.Dispose();
            engine.Dispose();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        };
    }

    // ---------- setup ----------

    /// <summary>
    /// The left column scrolls, so the window no longer has to be as tall as
    /// it: a modest floor keeps it usable, and the first layout opens it at
    /// the height that shows the whole column without taking over the screen.
    /// </summary>
    private void UpdateMinHeight()
    {
        if (!IsLoaded || ActualHeight <= 0 || mainArea.ActualHeight <= 0) return;
        double chrome = ActualHeight - mainArea.ActualHeight;
        if (chrome <= 0 || double.IsNaN(chrome)) return;

        MinHeight = MinWindowHeight;
        if (startHeightSet) return;
        startHeightSet = true;

        double natural = Math.Ceiling(leftPanel.DesiredSize.Height + chrome) + 2;
        // Prefer a height that shows the whole column, but never open larger
        // than this: anything that does not fit stays reachable by scrolling.
        double cap = Math.Max(MinWindowHeight, Math.Min(780, SystemParameters.WorkArea.Height * 0.85));
        if (double.IsNaN(natural) || natural <= 0) return;
        Height = Math.Clamp(natural, MinWindowHeight, cap);
    }

    private void FitEqHeight() =>
        eqRow.Height = new GridLength(Math.Clamp(mainArea.ActualHeight - SoundboardMinHeight, 0, EqMaxHeight));
}
