### Downloads

- `MicFX-Setup-<version>.exe` - installer (Start Menu entry, uninstaller)
- `MicFX.exe` - portable single-file build
- `MicFX-<version>-win-x64.zip` - portable build, zipped, with the license files

The binaries are unsigned, so SmartScreen may warn on first run
(More info > Run anyway). In-app updates are not affected.

### AI voice fixes

- The AI voice stopped after a moment with real speech ("The AI voice
  stopped: ... Gather ... out of data bounds"). The pitch tracker reports
  frames it is unsure about (pauses, breaths) as not-a-number, which the
  voice model cannot take; those frames now count as unvoiced. Found by
  running recorded speech through a trained voice model: the converted voice
  now follows the speaker's pitch within a semitone in 94% of voiced frames,
  with no clicks between blocks.
- Voice models exported to ONNX by Applio load directly, without converting
  the .pth first.

1.12.0 added the AI voice; its notes follow.

### AI voice

New VOICE card: speak with the voice of an RVC voice model you load, live,
next to the rest of the chain (after the gate, before the EQ).

- MicFX comes with no voices. Load a voice model file (.onnx); only use
  voices you have the right to use. RVC models (.pth, v1 or v2, 32/40/48 kHz)
  are converted with `tools/convert_rvc.py`; the
  [README](https://github.com/Norbertque/MicFX#ai-voice) has the steps.
- Pitch slider (-24 to +24 semitones) to move your voice into the model's
  range.
- Runs on an NVIDIA GPU through CUDA (driver 580 or newer), or on the CPU.
  If CUDA cannot start, MicFX says why and uses the CPU.
- Fixed delay of about 150, 210 or 310 ms, chosen in the settings menu, and
  included in the delay shown in the status line. If a block is not ready
  in time, the voice fades to silence and "Can't keep up" shows; it comes
  back once the model keeps up with time to spare. The delay never grows,
  and a late block is never played.
- The voice is silent while a model loads, so your real voice does not slip
  through, and the model is unloaded a minute after the AI voice is
  switched off, to free GPU memory.
- First use asks before downloading, once, into `%APPDATA%\MicFX\ai`:
  ONNX Runtime 1.32.0 (155 MB, from nuget.org), the ContentVec and FCPE
  helper models (426 MB, from this repository's `models-1` release) and, for
  the GPU only, NVIDIA's CUDA 13.0 and cuDNN 9.16 libraries (1.06 GB, from
  NVIDIA, after you accept NVIDIA's license). Every file is checked against a
  pinned SHA-256; interrupted downloads continue where they stopped.
- The switch, model and pitch are saved in profiles.
- A log in `%APPDATA%\MicFX\ai\log.txt` (Settings > Open the AI voice log)
  records downloads, the backend, errors and the time per block every 10 s.

Cost on the audio thread, measured with MicFX's own audio code on a 2.1 GHz
core: off, nothing (the stage passes audio through untouched); on, about
0.01% of one core, because the model runs on its own thread. The model
itself is the heavy part: on a 4-core 2.1 GHz test CPU an RVC v2 model took
about 220 ms per 100 ms block, so the CPU is mainly a fallback. GPU timings
depend on the card and on what else runs on it; the status line under the
pitch slider shows the figure for your PC.

### License

MicFX stays GPL-3.0-or-later, with an additional permission (GPLv3
section 7) to combine it with the NVIDIA CUDA and cuDNN libraries and with
ONNX Runtime. MicFX does not distribute either; both are downloaded on first
use. Details are in THIRD-PARTY-NOTICES.txt.

### First-time setup

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (run as
   administrator, reboot).
2. In MicFX: select your microphone as input and "CABLE Input" as output,
   press Start.
3. In Discord/OBS: select "CABLE Output" as the microphone.

See the [README](https://github.com/Norbertque/MicFX#readme) for full
instructions and troubleshooting.
