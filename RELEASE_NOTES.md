### Downloads

- `MicFX-Setup-<version>.exe` - installer (Start Menu entry, uninstaller)
- `MicFX.exe` - portable single-file build
- `MicFX-<version>-win-x64.zip` - portable build, zipped, with the license files

The binaries are unsigned, so SmartScreen may warn on first run
(More info > Run anyway). In-app updates are not affected.

### Soundboard

- Drag and drop: drop audio files, or a folder of them, onto the soundboard
  to add them.
- MicFX keeps its own copy of every clip (in `%APPDATA%\MicFX\Sounds`), so
  moving, renaming or deleting the original file no longer breaks the tile.
  Existing clips are copied once in the background after updating.
- Clips start instantly: a silent lead-in at the start of a clip is detected
  once and skipped (also for loops and preview). Can be turned off per clip
  in Edit, which shows the measured time.
- Playing clips show a progress bar on their tile.
- New stop-all hotkey, Ctrl+Alt+0 by default; change it in the settings menu.

### Hotkeys

- Hotkeys show as printed on your keyboard. Labels saved by older versions,
  such as "Ctrl+Alt+D1" or "Ctrl+Alt+Oem3", now read "Ctrl+Alt+1" or the
  actual key.
- Windows treats Ctrl+Alt as AltGr, so a Ctrl+Alt hotkey also blocks typing
  that AltGr character everywhere while MicFX runs (on a Czech keyboard
  Ctrl+Alt+1 is AltGr+1, which types ~). Such hotkeys are marked with ⚠ on
  the tile, with the blocked character in the tooltip, and the hotkey editor
  warns while you set one. Hotkeys another app already owns are marked too.
- Setting a hotkey that MicFX already uses now records it instead of playing
  the clip.

### Fixes

- Unplugging the microphone, or it starting late after boot, made MicFX
  switch to another mic and forget yours. The mic now stays selected as
  "(not connected)", MicFX waits, and it starts by itself when the mic is
  back.
- The soundboard toolbar hint no longer gets cut off.

### License

- MicFX is now licensed under the GNU GPL, version 3 or later. Copyright
  (C) 2026 Norbertque.
- The licenses of the bundled components (NAudio, RNNoise, .NET runtime) ship
  with every download and are shown in the app under Settings > About.
- The GitHub account was renamed to Norbertque. The in-app updater now finds
  releases by the repository's permanent ID, so it keeps working through
  future renames.

### First-time setup

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (run as
   administrator, reboot).
2. In MicFX: select your microphone as input and "CABLE Input" as output,
   press Start.
3. In Discord/OBS: select "CABLE Output" as the microphone.

See the [README](https://github.com/Norbertque/MicFX#readme) for full
instructions and troubleshooting.
