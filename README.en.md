# TimecodeSyncPlayer

[日本語](README.md)

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![TimecodeSyncPlayer screenshot](assets/screenshot.png)

An LTC-synchronized video player for live shows on Windows. v0.1.0 is a beta release intended
for real-show validation before a future 1.0 release.

## Features

- Frame-accurate video playback synchronized to incoming LTC (Linear Timecode) audio
- Single and Continue sync modes with per-clip timeline offsets
- Configurable Black/Freeze display across timecode gaps
- Configurable Run-through/Stop behavior when the LTC signal is lost
- Spout2 output for VJ tool integration
- Pure C# LTC decoder and libmpv software rendering

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) for release packages
- An x64 libmpv DLL, obtained separately because it is not included in either release package
- An audio input device carrying LTC

`SpoutDX.dll` is included in both release packages and is only needed for Spout output.

## Using the installer

1. Run `TimecodeSyncPlayer-v0.1.0-setup.exe`.
2. Leave **Download mpv now (run get-mpv.ps1)** selected on the completion screen.
3. Start TimecodeSyncPlayer from the Start menu.
4. Select the audio capture device carrying LTC and press **START**.
5. Load a video or playlist, then press **Sync ON**.

## Using the release zip

1. Extract `TimecodeSyncPlayer-v0.1.0-win-x64.zip` to a writable folder.
2. Run `powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1 -DestinationDirectory .`.
3. Start `TimecodeSyncPlayer.exe`.

## Building from source

```powershell
git clone https://github.com/faio1230/timecode-sync-player.git
cd timecode-sync-player
powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

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

## License

TimecodeSyncPlayer is available under the [MIT License](LICENSE). Distribution-specific third-party
terms are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

Developed by Studio Sandix

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
