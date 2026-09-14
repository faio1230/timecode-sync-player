# TimecodeSyncPlayer

[日本語](README.md)

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![TimecodeSyncPlayer screenshot](assets/screenshot.png)

A live-show video player for Windows that receives LTC (Linear Timecode) audio and synchronizes
video clips in a playlist.

## Download

**Download the installer or zip from [GitHub Releases](https://github.com/faio1230/timecode-sync-player/releases).**

- For most users, the per-user `TimecodeSyncPlayer-v0.2.0-setup.exe` installer is recommended and
  does not require administrator privileges.
- Choose `TimecodeSyncPlayer-v0.2.0-win-x64.zip` for a portable extracted copy.
- The GStreamer 1.28.2 runtime is included; no separate GStreamer installation is required.

## Features

- Frame-based video playback synchronized to LTC audio input
- Single and Continue synchronization modes with per-clip timeline offsets
- Black or Freeze display across timecode gaps
- Run-through or Stop behavior when the LTC signal is lost
- Selectable full-screen output to a connected external display
- Spout2 output for VJ tool integration
- Playlist and project save/load workflows
- Pure C# LTC decoder and GStreamer-based GPU output

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) for release packages
- **Microsoft Visual C++ 2015-2022 Redistributable (x64)**
  The setup installs it automatically. For the zip, install it manually when missing
  ([vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe)).
- An audio input device carrying LTC

`SpoutDX.dll` is included in release packages and is only needed when using Spout output.

## Using the installer

1. Run `TimecodeSyncPlayer-v0.2.0-setup.exe` from Releases. It installs per user and does not
   require administrator privileges.
2. Start TimecodeSyncPlayer from the Start menu.

Uninstalling removes the application, logs, and shortcuts. Per-user preferences
in `%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json` are intentionally retained for future
reinstallation. Delete that file manually to remove the preferences completely.

## Using the zip

1. Extract `TimecodeSyncPlayer-v0.2.0-win-x64.zip` to a writable folder.
2. If the Visual C++ 2015-2022 Redistributable (x64) is missing, run
   [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe).
3. Start `TimecodeSyncPlayer.exe`.

The GStreamer runtime is included. See the [setup guide](docs/SETUP.md) for details.

## Basic usage

1. Select the audio capture device carrying LTC and press **START**.
2. Open a video or add clips to the playlist.
3. Select Single or Continue mode, gap behavior, and signal-loss behavior.
4. Press **Sync ON** to start LTC synchronization.
5. For external output, select a Display and press **FULLSCREEN**.

## Building from source

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is required.

```powershell
git clone https://github.com/faio1230/timecode-sync-player.git
cd timecode-sync-player
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

Building from source requires the GStreamer 1.28 MSVC x64 runtime and `tcs_gstreamer.dll`
(release packages bundle them).
Spout output additionally requires an x64 `SpoutDX.dll` in the `native` folder when building from
source. See the [setup guide](docs/SETUP.md) for details.

## Documentation

- [Setup and build](docs/SETUP.md)
- [Native dependencies](native/README.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Settings reference](docs/settings.md)
- [Manual verification checklist](docs/verification-checklist.md)

## Hardware LTC loop E2E tests

The hardware E2E suite sends LTC to `CABLE Input` and captures it from `CABLE Output` through
[VB-CABLE](https://vb-audio.com/Cable/). Run it from a local Windows audio session where both
endpoints are visible. ffmpeg is also required.

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

Hardware tests skip automatically when prerequisites are unavailable.

## License

TimecodeSyncPlayer is available under the [MIT License](LICENSE). Distribution-specific third-party
terms are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

Developed by Studio Sandix.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
