# TimecodeSyncPlayer

[日本語](README.md)

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![TimecodeSyncPlayer screenshot](assets/screenshot.png)

TimecodeSyncPlayer is a live-show video player for Windows. It receives LTC (Linear Timecode) audio and plays the clips in a playlist in sync with the timecode. The development code name is ERYTHEIA.

## Download

The installer and the zip are on the [latest stable release (Latest) on GitHub Releases](https://github.com/faio1230/timecode-sync-player/releases/latest). The stable release is the latest v0.6.x, starting with v0.6.1. Releases marked "Pre-release" are still being verified. Do not use them for a show.

For most users, use the per-user installer `TimecodeSyncPlayer-v<version>-setup.exe`, which does not require administrator privileges. For a portable extracted copy, choose `TimecodeSyncPlayer-v<version>-win-x64.zip`. `<version>` is the latest stable release, for example `v0.6.5`. The GStreamer 1.28.2 runtime is included, so no separate GStreamer installation is needed.

Read the [field preparation guide](docs/USER-MANUAL.md) (Japanese) before a show. How the material is exported makes a large difference to sync stability.

## Features

- Frame-based video playback synchronized to LTC audio input
- Single and Continue synchronization modes with per-clip timeline offsets
- Black or Freeze display across timecode gaps
- Run-through or Stop behavior when the LTC signal is lost
- Selectable full-screen output to a connected external display
- Spout2 output for VJ tool integration
- Playlist and project save/load
- Pure C# LTC decoder and GStreamer-based GPU output
- GPU decoding of HAP (Hap / Hap Alpha / Hap Q) (since v0.5.0)
- GPU decoding of ProRes (since v0.6.0, by [gst-prores-d3d11](https://github.com/faio1230/gst-prores-d3d11). Verified and recommended on NVIDIA GPUs. Since v0.6.5, other GPUs also decode on the GPU by default, but they are unverified)

The recommended formats are H.264 (keyframe interval about 1 second, at most 2 seconds) and ProRes. HAP also loads without a warning. VP9, AV1, H.265 and others load but are not recommended, and a warning is shown when they are loaded. See the [field preparation guide](docs/USER-MANUAL.md) (Japanese) for the recommendation per format.

## Requirements

You need Windows 10/11 x64, a GPU and driver supporting Direct3D 11.4, the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), and an audio input device carrying LTC. The app is verified with a discrete (RTX-class) GPU as the premise. On an integrated GPU, decoding of 4K60 material may not keep up.

Direct3D 11.4 is used to composite and output the picture. On a system without it, the app says so at startup and disables playback only. The app stays open, and there is no fallback to CPU compositing.

The ProRes GPU decoding plugin requires the Microsoft Visual C++ v14 Redistributable (x64) 14.50 or later (formerly named Microsoft Visual C++ 2015-2022). The setup installs it only when it is missing or older, and then asks once for administrator approval (UAC) during installation. For the zip, run [vc_redist.x64.exe](https://aka.ms/vc14/vc_redist.x64.exe) manually when it is missing or older.

ProRes GPU decoding is verified and recommended on NVIDIA (RTX class) GPUs. The plugin runs on generic Direct3D 11 shaders, so since v0.6.5 the default (auto) decodes on the GPU on any GPU. GPUs other than NVIDIA are verified only as far as the test machine's integrated AMD GPU. On an integrated GPU, 4K60 ProRes decoding may fall behind the CPU; in that case set the "ProRes の GPU デコード" (ProRes GPU decoding) selector in the app to 無効 (off). A change takes effect after the app is restarted. The UI is in Japanese.

`SpoutDX.dll` is included in release packages and is only used for Spout output.

## Using the installer

1. Run `TimecodeSyncPlayer-v<version>-setup.exe` from Releases. It installs per user and does not require administrator privileges. On a PC where the Visual C++ Redistributable is missing or older, it asks once for administrator approval (UAC) to install it.
2. Start TimecodeSyncPlayer from the Start menu.

Uninstalling removes the application, logs, and shortcuts. The per-user settings file `%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json` is intentionally kept so that it can be restored on reinstallation. Delete it manually to remove the settings completely.

## Using the zip

1. Extract `TimecodeSyncPlayer-v<version>-win-x64.zip` to a writable folder.
2. If the Microsoft Visual C++ v14 Redistributable (x64) 14.50 or later is not installed, run [vc_redist.x64.exe](https://aka.ms/vc14/vc_redist.x64.exe).
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

Building from source also requires the GStreamer 1.28.2 MSVC x64 runtime and `tcs_gstreamer.dll` (release packages bundle them). The ProRes GPU decoding plugin is fetched with `scripts\get-prores-plugin.ps1`. Spout output additionally requires an x64 `SpoutDX.dll` in the `native` folder. See the [setup guide](docs/SETUP.md) for details.

## Documentation

- **[Field preparation guide](docs/USER-MANUAL.md)** (Japanese): recommended material formats (keyframe interval, codec, fps), timecode signal conditions, how to read the warnings
- [Setup and build](docs/SETUP.md)
- [Native dependencies](native/README.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Roadmap](docs/ROADMAP.md)
- [Settings reference](docs/settings.md)
- [Manual verification checklist](docs/verification-checklist.md)

## Related projects

[gst-prores-d3d11](https://github.com/faio1230/gst-prores-d3d11) is a GStreamer plugin that decodes ProRes on the GPU with Direct3D 11, by the same developer. It is licensed under LGPL-2.1 or later and bundled with TimecodeSyncPlayer releases.

## Hardware LTC loop E2E tests

The hardware E2E suite sends LTC to `CABLE Input` and captures it from `CABLE Output` through [VB-CABLE](https://vb-audio.com/Cable/). Run it from a local Windows audio session where both endpoints are visible. ffmpeg is also required.

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

Hardware tests skip automatically when the prerequisites are unavailable.

## License

TimecodeSyncPlayer is licensed under the [MIT License](LICENSE). Third-party terms that apply to the distribution, including the bundled GStreamer runtime and gst-prores-d3d11, are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

Developed by Studio Sandix.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
