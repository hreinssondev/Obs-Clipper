# OBS Clipper

Save the last few seconds of gameplay with a hotkey, using OBS's replay buffer, and have the clips show up in [Medal](https://medal.tv) automatically.

OBS does the recording; OBS Clipper sits in the tray, listens for your hotkeys, asks OBS to save its replay buffer, trims the clip to the length you picked (with ffmpeg, no re-encoding), names it by date and time, and drops it in a folder Medal imports from.

## Features

- **Hotkeys per clip length** – e.g. one key for the last 15 seconds, another for 30, another for 60. Keyboard keys, combos (Ctrl/Alt/Shift) and mouse side buttons.
- **No input lag** – hotkeys are read by polling key state, the same way OBS does. Nothing hooks your keyboard.
- **Clean clips folder** – OBS writes to a staging folder; finished, trimmed clips are moved into the clips folder, named like `12.14.26 - 14꞉20.mp4`.
- **First-launch setup** – installs ffmpeg and OBS (via winget) if missing, connects to OBS, adds a screen capture source, turns on the replay buffer and makes sure game audio is recorded. A brand-new OBS gets tuned recording settings (NVENC/AV1 where available, 1080p60, 60 s buffer); an OBS you already use keeps its settings.
- **Tray app** – runs quietly in the tray; "Done" sends both OBS and OBS Clipper there, "Show OBS" brings OBS back.
- **Get help** – "Copy diagnostics" copies a report (audio sources, output settings, the latest clip's streams, log lines) to send to whoever's helping; "Remote help" opens Windows Quick Assist.
- **Dark UI**.

## Install

1. Download `OBS-Clipper-vX.Y.Z.zip` from [Releases](../../releases), unzip it and run `OBSClipper.exe`.
   - Windows SmartScreen may warn about an unknown app (it isn't code-signed): click **More info → Run anyway**.
2. OBS Clipper installs itself to `%LOCALAPPDATA%\Programs\OBS Clipper` and adds a Start menu shortcut.
3. Click **Set up** and wait for the checks to go green. Click **Yes** if Windows asks for permission to install OBS.
4. **In Medal** (the one manual step): Settings → PC Storage → External Recorders Import Locations → **Sync an External Recorder**, and pick the clips folder (`Videos\OBS Clips` by default). Don't make it Medal's own capture folder.

To update, download a newer release and run it; it replaces the installed copy and restarts.

## Requirements

- Windows 10 or 11 (64-bit) with [winget](https://learn.microsoft.com/windows/package-manager/winget/) (App Installer), used to install OBS and ffmpeg.
- [OBS Studio](https://obsproject.com) 28 or newer (for its built-in WebSocket server) – installed by setup if missing.

## Good to know

- OBS's own settings decide what clips look like (encoder, quality, resolution, sources, audio). Change them in OBS any time.
- A clip can't be longer than OBS's replay buffer (Settings → Output → Replay Buffer).
- Keep the recording format **MP4** – Medal only imports MP4.
- Don't also set a "Save Replay" hotkey inside OBS, or you'll get two clips per press.
- Settings and a log live in `%APPDATA%\OBSClipper`.

## Building

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Run from source
dotnet run -c Release

# Self-contained single exe (what releases ship)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

## License

[MIT](LICENSE)
