using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MicFX.Audio;
using NAudio.Dsp;

namespace MicFX;

public partial class MainWindow
{
    private static readonly Dictionary<string, float[]> EqPresets = new()
    {
        ["Flat"] = new float[10],
        ["Warm Broadcast"] = new float[] { 2, 3, 2, 1, -1, 0, 1, 3, 2, 1 },
        ["Bright & Clear"] = new float[] { -2, -1, 0, 0, 1, 2, 3, 4, 3, 2 },
        ["Cut The Mud"] = new float[] { -5, -4, -3, -3, -1, 0, 1, 2, 1, 0 },
        ["Bass Boost"] = new float[] { 5, 4, 3, 1, 0, 0, 0, 0, 0, 0 },
    };

    private readonly List<Slider> eqSliders = new();
    private readonly List<Border> spectrumBars = new();
    private readonly float[] spectrumSamples = new float[SpectrumTapSampleProvider.WindowSize];
    private readonly Complex[] spectrumFft = new Complex[SpectrumTapSampleProvider.WindowSize];

    private float[] eqFrequencies = EqualizerSampleProvider.BuildFrequencies(10);
    private DispatcherTimer? spectrumTimer;

    // ---------- EQ sliders and spectrum bars ----------

    private void BuildEqSliders(float[]? gains = null)
    {
        for (int i = 0; i < eqFrequencies.Length; i++)
        {
            int band = i;
            float freq = eqFrequencies[i];

            var valueLabel = new TextBlock
            {
                Text = "0 dB",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("SubtleTextBrush")
            };
            var freqLabel = new TextBlock
            {
                Text = freq >= 1000 ? $"{freq / 1000:0.#}k" : $"{freq:0}",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -15,
                Maximum = 15,
                Value = 0,
                TickFrequency = 3,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            slider.ValueChanged += (_, e) =>
            {
                valueLabel.Text = $"{e.NewValue:+0.#;-0.#;0} dB";
                if (initializing) return;
                if (band < settings.EqGainsDb.Length)
                    settings.EqGainsDb[band] = (float)e.NewValue;
                engine.SetEqGain(band, (float)e.NewValue);
            };
            if (gains != null && i < gains.Length)
                slider.Value = gains[i];

            var panel = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
            DockPanel.SetDock(valueLabel, Dock.Top);
            DockPanel.SetDock(freqLabel, Dock.Bottom);
            panel.Children.Add(valueLabel);
            panel.Children.Add(freqLabel);
            panel.Children.Add(slider);

            eqPanel.Children.Add(panel);
            eqSliders.Add(slider);
        }
    }

    private void BuildSpectrumBars()
    {
        for (int i = 0; i < eqFrequencies.Length; i++)
        {
            var bar = new Border
            {
                Opacity = 0.22,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Height = 2
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); // follows theme changes
            spectrumPanel.Children.Add(bar);
            spectrumBars.Add(bar);
        }
    }

    // Clip warnings stay lit briefly so a single hot peak is still noticeable.
    private static readonly TimeSpan ClipHold = TimeSpan.FromMilliseconds(700);
    private DateTime inputHotUntil;
    private DateTime outputLimitedUntil;

    // Set on the UI thread, read on the audio thread: while the window is
    // hidden in the tray, level reports are dropped instead of drawn.
    private volatile bool metersVisible;
    private bool inputWarningShown;
    private bool outputWarningShown;

    /// <summary>Called on the audio thread ~20 times a second.</summary>
    private void OnLevels(LevelReport r)
    {
        if (!metersVisible) return;
        Dispatcher.BeginInvoke(() =>
        {
            var now = DateTime.UtcNow;
            if (r.InputHot) inputHotUntil = now + ClipHold;
            if (r.OutputLimited) outputLimitedUntil = now + ClipHold;

            meterIn.Value = DbToPercent(r.InputDb);
            meterOut.Value = DbToPercent(r.OutputDb);
            SetMeterWarning(meterIn, ref inputWarningShown, now < inputHotUntil,
                "Input is clipping — lower Mic gain (or the mic's own gain in Windows).");
            SetMeterWarning(meterOut, ref outputWarningShown, now < outputLimitedUntil,
                "Output hit the limiter — it's being held under -1 dBFS so it won't distort, but you're louder than needed.");
        });
    }

    private void SetMeterWarning(ProgressBar meter, ref bool shown, bool warn, string why)
    {
        if (warn == shown) return;
        shown = warn;
        meter.Foreground = (Brush)FindResource(warn ? "ErrorBrush" : "MeterBrush");
        meter.ToolTip = warn ? why : null;
    }

    private static double DbToPercent(float db) => Math.Clamp((db + 60) / 60 * 100, 0, 100);

    // ---------- spectrum ----------

    private void StartSpectrumTimer()
    {
        spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        spectrumTimer.Tick += (_, _) => UpdateSpectrum();
        spectrumTimer.Start();
    }

    private static readonly float[] SpectrumWindow = Enumerable.Range(0, SpectrumTapSampleProvider.WindowSize)
        .Select(i => (float)FastFourierTransform.HannWindow(i, SpectrumTapSampleProvider.WindowSize))
        .ToArray();

    private void UpdateSpectrum()
    {
        if (!IsVisible) return;
        if (!engine.CopySpectrumSamples(spectrumSamples))
        {
            foreach (var bar in spectrumBars) bar.Height = 2;
            return;
        }

        int n = SpectrumTapSampleProvider.WindowSize;
        for (int i = 0; i < n; i++)
        {
            spectrumFft[i].X = spectrumSamples[i] * SpectrumWindow[i];
            spectrumFft[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, 11, spectrumFft);

        double maxHeight = Math.Max(spectrumPanel.ActualHeight - 4, 10);
        float step = eqFrequencies.Length > 1 ? MathF.Sqrt(eqFrequencies[1] / eqFrequencies[0]) : 1.5f;
        for (int b = 0; b < spectrumBars.Count && b < eqFrequencies.Length; b++)
        {
            float f = eqFrequencies[b];
            int lo = Math.Max(1, (int)(f / step * n / AudioEngine.SampleRate));
            int hi = Math.Min(n / 2 - 1, Math.Max(lo + 1, (int)(f * step * n / AudioEngine.SampleRate)));
            float sum = 0f;
            for (int i = lo; i <= hi; i++)
                sum += MathF.Sqrt(spectrumFft[i].X * spectrumFft[i].X + spectrumFft[i].Y * spectrumFft[i].Y);
            float avg = sum / (hi - lo + 1);
            float db = 20f * MathF.Log10(avg + 1e-9f);
            double t = Math.Clamp((db + 65) / 60.0, 0, 1);
            spectrumBars[b].Height = Math.Max(2, t * maxHeight);
        }
    }

    // ---------- levels, gate, noise suppression, leveler ----------

    private void MicGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblMicGain != null) lblMicGain.Text = $"{e.NewValue * 100:0}%";
        if (initializing) return;
        engine.SetMicGain((float)e.NewValue);
    }

    private void Master_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (lblMaster != null) lblMaster.Text = $"{e.NewValue * 100:0}%";
        if (initializing) return;
        engine.SetMasterVolume((float)e.NewValue);
    }

    private void Gate_Changed(object sender, RoutedEventArgs e)
    {
        if (lblGate != null) lblGate.Text = $"{sliderGate.Value:0} dB";
        if (initializing) return;
        engine.SetGate(chkGate.IsChecked == true, (float)sliderGate.Value);
    }

    private string CurrentDenoiseMode() =>
        (comboDenoiseMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ai";

    // The strength slider is shared by both engines but means something
    // different in each (AI: 6-60 dB cap, 60 = none; spectral: 6-30 dB), so
    // each keeps its own stored value and the slider is reloaded on switch.
    // sliderDenoiseMode records which engine the slider currently holds.
    private string sliderDenoiseMode = "Ai";
    private bool denoiseUiUpdating;

    private void Denoise_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing || denoiseUiUpdating || sliderDenoise == null || chkVoiceGate == null) return;
        string mode = CurrentDenoiseMode();
        StoreDenoiseSlider();                      // value belongs to the engine it was shown for
        if (mode != sliderDenoiseMode) LoadDenoiseSlider(mode);

        settings.DenoiseEnabled = chkDenoise.IsChecked == true;
        settings.DenoiseMode = mode;
        settings.VoiceGateEnabled = chkVoiceGate.IsChecked == true;
        RefreshDenoiseLabels();
        PushDenoiseToEngine();

        if (chkDenoise.IsChecked == true && mode == "Ai" && !AudioEngine.AiDenoiseAvailable)
            txtStatus.Text = "AI suppression unavailable, using spectral instead " +
                             $"({AudioEngine.AiDenoiseUnavailableReason ?? "unknown reason"}).";
    }

    /// <summary>Saves the slider into the setting of the engine it is showing.</summary>
    private (float SpectralDb, float AiDb) StoreDenoiseSlider()
    {
        if (sliderDenoiseMode == "Ai") settings.AiMaxReductionDb = (float)sliderDenoise.Value;
        else settings.DenoiseStrengthDb = (float)sliderDenoise.Value;
        return (settings.DenoiseStrengthDb, settings.AiMaxReductionDb);
    }

    private void LoadDenoiseSlider(string mode)
    {
        denoiseUiUpdating = true;   // range changes coerce Value; don't store those
        bool ai = mode == "Ai";
        sliderDenoise.Maximum = ai ? RnNoiseSampleProvider.MaxReductionUnlimited : 30;
        sliderDenoise.Minimum = 6;
        sliderDenoise.Value = ai ? settings.AiMaxReductionDb : settings.DenoiseStrengthDb;
        sliderDenoiseMode = mode;
        denoiseUiUpdating = false;
    }

    private void RefreshDenoiseLabels()
    {
        bool ai = sliderDenoiseMode == "Ai";
        chkVoiceGate.Visibility = ai ? Visibility.Visible : Visibility.Collapsed; // needs the AI's speech detection
        lblDenoise.Text = ai && sliderDenoise.Value >= RnNoiseSampleProvider.MaxReductionUnlimited - 0.5
            ? "Max"
            : $"{sliderDenoise.Value:0} dB";
    }

    /// <summary>Puts denoise controls in a given state without echoing back through the handlers.</summary>
    private void ShowDenoiseSettings(bool enabled, string mode, bool voiceGate)
    {
        denoiseUiUpdating = true;
        chkDenoise.IsChecked = enabled;
        foreach (ComboBoxItem item in comboDenoiseMode.Items)
            if ((string)item.Tag == mode) { item.IsSelected = true; break; }
        chkVoiceGate.IsChecked = voiceGate;
        denoiseUiUpdating = false;
        LoadDenoiseSlider(mode);
        RefreshDenoiseLabels();
    }

    private void PushDenoiseToEngine() =>
        engine.SetDenoise(settings.DenoiseEnabled, settings.DenoiseStrengthDb, settings.DenoiseMode,
            settings.AiMaxReductionDb, settings.VoiceGateEnabled);

    private void Comp_Changed(object sender, RoutedEventArgs e)
    {
        if (lblComp != null) lblComp.Text = $"{sliderComp.Value:0}";
        if (initializing) return;
        engine.SetCompressor(chkComp.IsChecked == true, (float)sliderComp.Value);
    }

    // ---------- equalizer ----------

    private void EqPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        if (comboEqPreset.SelectedItem is not string name || !EqPresets.TryGetValue(name, out var gains))
            return;
        var mapped = MapGainsToBands(gains, eqFrequencies);
        for (int i = 0; i < eqSliders.Count && i < mapped.Length; i++)
            eqSliders[i].Value = mapped[i];
    }

    /// <summary>Re-maps a gain curve onto a different set of band centres (linear in log-frequency).</summary>
    private static float[] MapGainsToBands(float[] srcGains, float[] dstFreqs)
    {
        var srcFreqs = EqualizerSampleProvider.BuildFrequencies(srcGains.Length);
        if (srcFreqs.Length == dstFreqs.Length) return srcGains;
        return MapGainsToBandsFrom(srcFreqs, srcGains, dstFreqs);
    }

    private void EqBands_Changed(object sender, RoutedEventArgs e)
    {
        if (lblEqBands != null) lblEqBands.Text = $"{sliderEqBands.Value:0}";
        if (initializing) return;
        int count = (int)sliderEqBands.Value;
        if (count == eqFrequencies.Length) return;

        var newFreqs = EqualizerSampleProvider.BuildFrequencies(count);
        var newGains = MapGainsToBandsFrom(eqFrequencies, settings.EqGainsDb, newFreqs);
        eqFrequencies = newFreqs;
        settings.EqBandCount = count;
        settings.EqGainsDb = newGains;
        engine.SetEqBands(count, newGains);

        eqPanel.Children.Clear();
        eqSliders.Clear();
        spectrumPanel.Children.Clear();
        spectrumBars.Clear();
        BuildEqSliders(newGains);
        BuildSpectrumBars();
    }

    private static float[] MapGainsToBandsFrom(float[] srcFreqs, float[] srcGains, float[] dstFreqs)
    {
        var result = new float[dstFreqs.Length];
        for (int i = 0; i < dstFreqs.Length; i++)
        {
            double f = Math.Log(dstFreqs[i]);
            if (srcGains.Length == 0) break;
            if (f <= Math.Log(srcFreqs[0])) { result[i] = srcGains[0]; continue; }
            if (f >= Math.Log(srcFreqs[Math.Min(srcGains.Length, srcFreqs.Length) - 1]))
            {
                result[i] = srcGains[Math.Min(srcGains.Length, srcFreqs.Length) - 1];
                continue;
            }
            int j = 1;
            while (j < srcFreqs.Length && Math.Log(srcFreqs[j]) < f) j++;
            double f0 = Math.Log(srcFreqs[j - 1]);
            double f1 = Math.Log(srcFreqs[j]);
            double t = (f - f0) / (f1 - f0);
            float g0 = srcGains[Math.Min(j - 1, srcGains.Length - 1)];
            float g1 = srcGains[Math.Min(j, srcGains.Length - 1)];
            result[i] = (float)(g0 * (1 - t) + g1 * t);
        }
        return result;
    }

    private void EqReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var slider in eqSliders)
            slider.Value = 0;
        comboEqPreset.SelectedIndex = 0;
    }
}
