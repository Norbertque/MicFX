# MicFX

Real-time microphone processing for Windows: EQ, noise suppression, noise
gate, compressor, an AI voice changer and a soundboard on a single mic input.
The processed signal is routed to a virtual audio device so other
applications (Discord, OBS, games) can use it as their microphone.

Pipeline (WASAPI shared mode, 48 kHz float):

```
mic -> gain -> noise suppression -> gate -> AI voice -> EQ -> compressor
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
- AI voice: speaks with the voice of an RVC voice model you load (see
  [AI voice](#ai-voice)), with pitch and index controls and three quality
  settings, on an NVIDIA GPU (CUDA) or the CPU. Adds a fixed delay of about
  150, 210 or 310 ms (settings menu, plus 30 or 60 ms at higher quality);
  when the computer cannot keep up, the voice fades to silence and a warning
  shows instead of stuttering or drifting later
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

## AI voice

The AI voice replaces your voice with the voice of a voice model, using RVC
(Retrieval-based Voice Conversion). MicFX does not come with voices: you load
a model file. Only use voices you have the right to use, such as your own or
one whose owner agreed.

### Requirements

- An NVIDIA graphics card with driver 580 or newer, for real time. The CPU
  works too, but full RVC models are usually too slow on it.
- The Microsoft Visual C++ Redistributable (x64), which most PCs with games
  already have: <https://aka.ms/vs/17/release/vc_redist.x64.exe>.

### First use

Tick VOICE > AI voice. MicFX asks before downloading, once, into
`%APPDATA%\MicFX\ai`:

| Download | Size | From |
|---|---|---|
| ONNX Runtime 1.32.0 (runs the models) | 155 MB | Microsoft, nuget.org |
| Helper models: ContentVec and FCPE | 426 MB | this repository's `models-1` release |
| NVIDIA CUDA 13.0 and cuDNN 9.16 libraries (GPU only) | 1.06 GB | NVIDIA |

The NVIDIA libraries are NVIDIA's proprietary software. MicFX downloads them
from NVIDIA only after you accept NVIDIA's license in the prompt; answering
"No" runs the voice on the CPU instead. Every file is checked against a
pinned SHA-256 before it is used, and interrupted downloads continue where
they stopped.

While a model loads the voice is silent, so your real voice does not slip
through. The status under the pitch slider shows how long the model takes per
block and how much time was left before the block had to play (for example
"GPU: 20 ms per block, 30 ms to spare"). A minute after the AI voice is
switched off, the model is unloaded to free GPU memory.

### Getting a voice model

MicFX runs RVC v1 and v2 models (32, 40 or 48 kHz, with or without pitch) in
ONNX format. To make one from a .pth:

1. Train a model of your own voice, or of a voice you have permission to
   use, with the [RVC WebUI](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI).
   Training writes the finished model to `assets/weights/<name>.pth`; that is
   the file to convert (not `G_*.pth` from the logs folder, and the `.index`
   file is not used).
2. Convert it with `tools/convert_rvc.py` from this repository. It needs
   Python 3.11 to 3.14 (ONNX Runtime has no build for 3.15 yet; `py --list`
   shows which versions are installed):

   ```
   py -3.14 -m venv rvc-convert
   rvc-convert\Scripts\pip install torch --index-url https://download.pytorch.org/whl/cpu
   rvc-convert\Scripts\pip install numpy scipy onnx onnxruntime
   rvc-convert\Scripts\python convert_rvc.py myvoice.pth myvoice.onnx
   ```

   The script downloads RVC's network code from a pinned commit (checked by
   SHA-256), converts the model and prints how closely the ONNX file matches
   PyTorch.
3. In MicFX: VOICE > Load..., choose `myvoice.onnx`, tick AI voice. Keep the
   model's `.index` file next to it (same name, or the only `.index` in the
   folder), or choose it with Load... as well.

Models exported to ONNX by [Applio](https://github.com/IAHispano/Applio)
(often published next to the .pth) load directly, without converting; a
converted .pth costs somewhat less GPU time per block, because MicFX's format
skips more of the context. MicFX also recognises the ONNX exports of older
RVC WebUI versions and of w-okada's voice changer, but those have only been
tested with stand-in files, not real ones.

### Using it

- Pitch moves your voice into the model's range: about +12 semitones from a
  low voice to a high model, -12 the other way.
- Index (needs the model's `.index` file) pulls your voice towards the
  model's own recordings, as RVC's "index rate" does: higher sounds more like
  the model, very high can blur consonants. 75% is the usual starting point.
  It runs on the CPU and takes a few milliseconds per block.
- Settings > AI voice quality lets the models see more around each block:
  High (about 1.3x the GPU time, +30 ms delay) and Best (about 1.7x, +60 ms)
  sound closer to converting the whole recording at once. Measured against
  that, High removes about 6% of the difference and Best about 12%. A model
  whose frame count leaves no room for a setting runs at the next one down,
  and the status says so.
- Settings > AI voice delay: Short (about 150 ms) needs a GPU with time to
  spare; Safe (about 310 ms) leaves the most room next to a game or VR. The
  delay is fixed; it never grows.
- "Can't keep up" means blocks were not ready in time: the voice fades to
  silence and comes back once the model keeps up again with time to spare.
  If it repeats, choose a longer delay or reduce the load on the GPU.
- Settings > Open the AI voice log shows what happened: downloads, which
  backend runs, the index, start-up errors and a timing summary every 10
  seconds.

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
with "dry run" checked builds and tests everything without publishing; with
"cuda probe" checked it also downloads the pinned ONNX Runtime and NVIDIA
libraries and checks that all of them load.

The AI voice tests run ONNX Runtime on the CPU with tiny models from
`tools/make_test_models.py`. With `MICFX_MODELS_DIR` pointing at a folder
with `contentvec.onnx`, `fcpe.onnx` and a voice model `voice.onnx` (for
example `convert_rvc.py --untrained v2 40000 voice.onnx`), one more test runs
the real models and prints the time per block.

The helper models are made by `tools/export_contentvec.py` and
`tools/export_fcpe.py`, which check their output against PyTorch. The
"Helper models" workflow runs both and publishes the files as a pre-release
(never marked latest, since the in-app updater reads the latest release).

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Error: ... device in use" on Start | Another application holds the device in exclusive mode. Disable exclusive mode in the device's sound settings or close the other application. |
| Discord hears nothing | Check MicFX output is CABLE Input, Discord input is CABLE Output, and the engine is running (meters move when you talk). |
| Crackling or dropouts | Switch the latency setting back to Normal in the settings menu, or close CPU-heavy applications. |
| Hotkeys do not fire | Another application (or another tile) owns the combination; the tile shows a ⚠ with the reason in its tooltip. Assign a different one per clip (right-click, Edit). |
| Can't type ~ ^ ` or other AltGr characters | A Ctrl+Alt hotkey uses the same keys (Windows treats Ctrl+Alt as AltGr). Tiles that block a character show a ⚠; give them another key. |
| Callers hear an echo of themselves | Disable "Listen in headphones", or make sure the monitor device is headphones rather than speakers. |
| AI voice: "Can't keep up" | The model did not finish a block in time. Choose a longer delay in Settings, close other GPU work, or check the log (Settings > Open the AI voice log) for the time per block. |
| AI voice: "NVIDIA GPU not used" or "could not start" | The message says why: no NVIDIA driver, a driver older than 580 (update it), or a missing library. The voice then runs on the CPU. |
| AI voice: "Visual C++ Redistributable is not installed" | Install it from <https://aka.ms/vs/17/release/vc_redist.x64.exe> and switch the AI voice on again. |
| AI voice sounds robotic or off-key | Adjust Pitch until your voice sits in the model's range; a model trained on little or noisy audio sounds rough whatever the settings. |
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
    AiVoiceSampleProvider.cs     AI voice stage: dry/converted crossfade, silence while loading
    VoiceStream.cs               block timeline, SOLA joins, deadlines and drop-outs
    RvcPipeline.cs               RVC steps around the models (features, pitch, generator)
    RationalResampler.cs         zero-phase resampler (48 <-> 16/32/40 kHz)
    FeatureIndex.cs              RVC .index files (faiss IVFFlat) and the index blend
  AiVoice/
    AiVoiceHost.cs               downloads, model loading, GPU/CPU fallback, log
    AiVoiceFiles.cs              pinned downloads (URLs, sizes, SHA-256)
    PinnedDownloader.cs          resumable, checked downloads and extraction
    OrtNative.cs                 loads ONNX Runtime and the NVIDIA libraries by full path
    OnnxHelperModels.cs          ContentVec and FCPE sessions
    OnnxVoiceGenerator.cs        RVC voice model sessions (four ONNX formats)
    OnnxSessions.cs              CUDA/CPU session options
  Models/AppSettings.cs          JSON persistence, profiles
  Models/SoundLibrary.cs         MicFX's own copies of soundboard clips
  HotkeyManager.cs               global hotkey registration
  HotkeyNames.cs                 layout-aware key names, AltGr clash check
  HotkeyCapture.cs               key press -> hotkey, shared by the hotkey boxes
  MainWindow.xaml(.cs)           main window: fields, constructor, layout
  MainWindow.*.cs                the rest of it by area: Devices, Processing,
                                 AiVoice, Soundboard, Settings, Updates, Tray
  SoundTile.cs                   soundboard tile state (labels, progress)
  Theme.cs                       accent color presets, runtime recoloring
  EditSoundWindow.xaml(.cs)      per-clip editor (volume, loop, hotkey, preview)
  AboutWindow.xaml(.cs)          version, license, third-party notices
  App.xaml                       theme
installer/MicFX.iss              Inno Setup script (built in CI)
tests/MicFX.Tests/               xUnit tests for the audio code, the AI voice and the clip library
tools/                           convert_rvc.py (voice models), helper model exports, test models
```

## Credits

Uses [NAudio](https://github.com/naudio/NAudio) (MIT) and
[RNNoise](https://github.com/xiph/rnnoise) (BSD-3-Clause, compiled from the
pinned official source during the release build; local dev builds without the
native library fall back to the spectral suppressor). Their licenses, and the
.NET runtime's, are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
which ships with every download and is shown in the app under Settings >
About. VB-Audio Virtual Cable is donationware by VB-Audio Software.

The AI voice uses [ONNX Runtime](https://github.com/microsoft/onnxruntime)
(MIT), follows the real-time inference of
[RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI)
(MIT), and downloads [ContentVec](https://github.com/auspicious3000/contentvec)
(MIT) and [FCPE](https://github.com/CNChTu/FCPE) (MIT) exported to ONNX. The
NVIDIA CUDA and cuDNN libraries are downloaded from NVIDIA under NVIDIA's
license, only if you choose the GPU; MicFX does not distribute them.

## License

Copyright (C) 2026 Norbertque

MicFX is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; see [LICENSE](LICENSE) for details.

Additional permission under GNU GPL version 3 section 7: If you modify this
Program, or any covered work, by linking or combining it with the NVIDIA CUDA
and cuDNN runtime libraries, or with ONNX Runtime including its CUDA
execution provider, or with modified versions of those libraries, containing
parts covered by the terms of their licenses, the licensors of this Program
grant you additional permission to convey the resulting work. In plain
words: the GPL of MicFX does not extend to those libraries.
