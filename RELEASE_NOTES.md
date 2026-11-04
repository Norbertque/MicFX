### Downloads

- `MicFX-Setup-<version>.exe` - installer (Start Menu entry, uninstaller)
- `MicFX.exe` - portable single-file build
- `MicFX-<version>-win-x64.zip` - portable build, zipped, with the license files

The binaries are unsigned, so SmartScreen may warn on first run
(More info > Run anyway). In-app updates are not affected.

### Performance

- The audio threads now run in Windows' real-time "Pro Audio" scheduling
  class (MMCSS). Before, they ran at normal priority, so a game keeping
  every core busy could delay the audio and cause crackles or dropouts,
  most likely in the Low and Lowest latency modes.
- Garbage collection no longer does blocking full collections, which also
  pause the audio threads.
- Less CPU. Measured with MicFX's own audio code on a 2.1 GHz core
  (percent of one core, 30 s of audio):

  | Main audio chain                       | 1.11.0 | 1.11.1 |
  |----------------------------------------|--------|--------|
  | Mono mic, AI suppression, EQ, leveler  | 3.8%   | 2.7%   |
  | Mono mic, spectral suppression         | 2.2%   | 1.2%   |
  | Stereo mic, AI suppression             | 6.0%   | 5.2%   |

  A mono mic now stays mono through gain, gate, EQ and leveler instead of
  being processed twice as identical stereo, and the safety limiter (on the
  output and the headphone monitor) only does its lookahead work when
  something actually comes near the ceiling. Both produce exactly the same
  sound as before.
- With an auto-switch mic set up, MicFX no longer asks Windows for the full
  device list every second; it reuses the list until a device changes.
- While MicFX sits in the tray, the level meters are no longer updated.

### First-time setup

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (run as
   administrator, reboot).
2. In MicFX: select your microphone as input and "CABLE Input" as output,
   press Start.
3. In Discord/OBS: select "CABLE Output" as the microphone.

See the [README](https://github.com/Norbertque/MicFX#readme) for full
instructions and troubleshooting.
