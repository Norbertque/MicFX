# MicFX

Real-time microphone processing for Windows: EQ, noise suppression, noise
gate, compressor and a soundboard on a single mic input. The processed signal
is routed to a virtual audio device so other applications (Discord, OBS,
games) can use it as their microphone.

Pipeline (WASAPI shared mode, 48 kHz float):

```
mic -> gain -> noise suppression -> gate -> EQ -> compressor
    -> (+ soundboard) -> master volume -> output device (virtual cable)
```

## Features

- Graphic EQ with adjustable band count (10-20, log-spaced 31 Hz-16 kHz,
  +/-15 dB), presets, live spectrum display behind the sliders
- Noise suppression with two engines: AI (RNNoise 0.2, a recurrent neural
  network that removes everything that is not voice, including keyboard
  clicks) and spectral (STFT with decision-directed Wiener gains, for steady
  noise, without the "musical noise" artifacts). Both have a strength
  control. Applied to the mic before the soundboard is mixed in, so clips are
  never affected
- Optional voice gate that silences the mic between phrases using the AI
  engine's speech detection, so loud non-voice sounds do not open it
- Noise gate with adjustable threshold
- Compressor ("voice leveler") with automatic make-up gain, which lifts the
  voice but not the silence between words
- Soundboard mixed into the mic stream: per-clip volume, loop mode, custom
  global hotkeys (Ctrl+Alt+1..9 by default, Ctrl+Alt+0 stops everything),
  headphone-only preview, progress bars on playing clips. Add clips with the
  Add button or by dropping files or folders onto the soundboard; MicFX keeps
  its own copy of each (`%APPDATA%\MicFX\Sounds`), so moving or deleting the
  original does not break the tile. Silent lead-ins are skipped, so clips
  react instantly
- Hotkey labels show keys as printed on your keyboard layout, and MicFX warns
  when a Ctrl+Alt hotkey also blocks typing an AltGr character (Windows treats
  Ctrl+Alt as AltGr, e.g. Ctrl+Alt+1 is AltGr+1 = ~ on a Czech keyboard)
- Profiles for saving and switching complete configurations, also from the
  tray menu
- The chosen mic stays selected while it is unplugged (or still starting up
  after boot); MicFX waits and starts by itself when it is back
- Two microphone inputs: a primary mic plus an optional auto-switch mic that
  takes over whenever it is actually carrying audio (e.g. a VR headset mic once
  the headset connects) and hands back when it goes idle — applications
  downstream keep reading the same virtual cable. Virtual microphones stay
  listed in Windows whether or not anything is streaming through them, so the
  switch is driven by audio activity rather than by device presence
- Monitoring on a separate output device with independent levels for your own
  voice and the soundboard, so you can hear clips loudly while keeping your
  own voice quiet. The monitor reconnects on its own when the headphones are
  switched on later or reset, and stays selected while they are off
- Input/output level meters that turn red when the mic input clips or the
  output limiter engages; mic gain, master volume
- Safety limiter (-1 dBFS, 1 ms lookahead) on the output and on the headphone
  monitor, so voice plus a loud clip cannot distort or blast your ears
- Soundboard loudness matching: each clip is measured once (ITU-R BS.1770)
  and played at a similar loudness, with per-clip volume on top
- Light on the CPU: the full chain with AI noise suppression, a 20-band EQ
  and the leveler uses about 2.7% of one 2.1 GHz CPU core for a mono mic.
  The audio threads run in Windows' real-time "Pro Audio" class (MMCSS), so
  a busy game is less likely to cause dropouts
- Latency modes in the settings menu: Normal (~90 ms mouth-to-app, safest),
  Low (smaller buffers), Lowest (additionally opens the mic in WASAPI
  exclusive mode, bypassing the Windows audio engine; falls back to shared
  if the device refuses)
- Closing the window minimizes to the system tray; optional start with
  Windows (minimized)
- In-app auto-update from GitHub releases, checked at startup or on demand
  from the settings menu
- Theme color: preset accent colors or a custom one
- Settings persisted in `%APPDATA%\MicFX\settings.json`

## Setup

MicFX needs a virtual audio cable so other applications can read the
processed signal:

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/). Run the
   installer as administrator and reboot. This adds a playback device
   ("CABLE Input") and a recording device ("CABLE Output").
2. Download `MicFX-Setup-<version>.exe` (installer) or `MicFX.exe` (portable)
   from the [releases page](../../releases). The binaries are unsigned, so
   SmartScreen may warn on first run.
3. In MicFX: select your microphone as input, "CABLE Input" as output, press
   Start. Optionally pick your headphones under MONITOR and enable "Listen in
   headphones".
4. Point the target application at the cable:

| Application | Setting |
|---|---|
| Discord | Settings > Voice & Video > Input Device > CABLE Output |
| OBS | Audio Input Capture source > CABLE Output |
| Other | Set CABLE Output as default recording device in Windows Sound settings |

## Building

Requires the .NET 8 SDK.

```
dotnet run --project src/MicFX
```

`publish.cmd` produces a self-contained single-file `dist\MicFX.exe`.

Tests cover the DSP (suppression, gate, limiter, loudness meter against EBU
Tech 3341 cases, drift compensation, input selection), clip lead-in
detection and the clip library:

```
dotnet test tests/MicFX.Tests
```

RNNoise tests need the native library; pass one with
`-p:RnNoiseLib=<path>` or they are skipped. The release workflow builds it
and runs the suite with `MICFX_REQUIRE_RNNOISE=1`, so a broken native build
fails the release.

Releases are built by `.github/workflows/release.yml` (on a `v*` tag push or
manual dispatch): it publishes the exe, zips it, compiles the Inno Setup
installer and creates the GitHub release with all three assets. A manual run
with "dry run" checked builds and tests everything without publishing.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Error: ... device in use" on Start | Another application holds the device in exclusive mode. Disable exclusive mode in the device's sound settings or close the other application. |
| Discord hears nothing | Check MicFX output is CABLE Input, Discord input is CABLE Output, and the engine is running (meters move when you talk). |
| Crackling or dropouts | Switch the latency setting back to Normal in the settings menu, or close CPU-heavy applications. |
| Hotkeys do not fire | Another application (or another tile) owns the combination; the tile shows a ⚠ with the reason in its tooltip. Assign a different one per clip (right-click, Edit). |
| Can't type ~ ^ ` or other AltGr characters | A Ctrl+Alt hotkey uses the same keys (Windows treats Ctrl+Alt as AltGr). Tiles that block a character show a ⚠; give them another key. |
| Callers hear an echo of themselves | Disable "Listen in headphones", or make sure the monitor device is headphones rather than speakers. |
| Soundboard sounds broken or muffled to others | Discord's Krisp/noise suppression filters out non-voice audio, which includes soundboard clips. Disable noise suppression in Discord and enable MicFX's instead (CLEAN-UP > Noise suppression) — MicFX suppresses noise before the soundboard is mixed in. |

## Project layout

```
src/MicFX/
  Audio/
    AudioEngine.cs               real-time graph, start/stop, live parameters
    AudioDevices.cs              WASAPI endpoint enumeration and input selection
    DeviceWatcher.cs             endpoint add/remove notifications for auto-switching
    DriftCompensatingSampleProvider.cs  bounds latency against clock drift
    RnNoiseSampleProvider.cs     RNNoise (ML) denoiser, strength blend, P/Invoke wrapper
    VoiceGate.cs                 speech-probability gate
    PeakLimiterSampleProvider.cs lookahead safety limiter
    LoudnessMeter.cs             BS.1770 integrated loudness for clip matching
    NoiseSuppressionSampleProvider.cs  spectral denoiser
    NoiseGateSampleProvider.cs   envelope-follower gate
    CompressorSampleProvider.cs  compressor with auto make-up
    EqualizerSampleProvider.cs   10-20 band biquad peaking EQ
    SpectrumTapSampleProvider.cs sample window for the spectrum display
    TeeSampleProvider.cs         split-off for self-monitoring
  Models/AppSettings.cs          JSON persistence, profiles
  Models/SoundLibrary.cs         MicFX's own copies of soundboard clips
  HotkeyManager.cs               global hotkey registration
  HotkeyNames.cs                 layout-aware key names, AltGr clash check
  HotkeyCapture.cs               key press -> hotkey, shared by the hotkey boxes
  MainWindow.xaml(.cs)           main window: fields, constructor, layout
  MainWindow.*.cs                the rest of it by area: Devices, Processing,
                                 Soundboard, Settings, Updates, Tray
  SoundTile.cs                   soundboard tile state (labels, progress)
  Theme.cs                       accent color presets, runtime recoloring
  EditSoundWindow.xaml(.cs)      per-clip editor (volume, loop, hotkey, preview)
  AboutWindow.xaml(.cs)          version, license, third-party notices
  App.xaml                       theme
installer/MicFX.iss              Inno Setup script (built in CI)
tests/MicFX.Tests/               xUnit tests for the audio code and the clip library
```

## Credits

Uses [NAudio](https://github.com/naudio/NAudio) (MIT) and
[RNNoise](https://github.com/xiph/rnnoise) (BSD-3-Clause, compiled from the
pinned official source during the release build; local dev builds without the
native library fall back to the spectral suppressor). Their licenses, and the
.NET runtime's, are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
which ships with every download and is shown in the app under Settings >
About. VB-Audio Virtual Cable is donationware by VB-Audio Software.

## License

Copyright (C) 2026 Norbertque

MicFX is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; see [LICENSE](LICENSE) for details.
