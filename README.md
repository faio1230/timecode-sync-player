# TimecodeSyncPlayer

An LTC-synchronized video player for live shows (Windows / WPF).

## Features

- Frame-accurate playback synchronized to an incoming LTC (Linear Timecode) audio signal
- Playlist with per-clip timeline offsets
- Continue and Single sync modes, with configurable Black/Freeze display on timecode gaps
- Spout2 output for integration with VJ tools
- Pure C# LTC decoder (no dependency on libltc)
- Video rendering via libmpv's software rendering API

## Requirements

- Windows 10/11 x64
- .NET 8 SDK
- `mpv-2.dll` (required, obtained separately — see below)
- `SpoutDX.dll` (optional, required only for Spout2 output)

## Quick Start

```powershell
git clone <this-repo-url>
cd timecode-sync-player
# Place mpv-2.dll (and, optionally, SpoutDX.dll) into native/
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

See `docs/SETUP.md` for detailed setup instructions, including where to obtain the native DLLs.

## Documentation

- [docs/SETUP.md](docs/SETUP.md) — setup and build instructions
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — architecture overview
- [docs/verification-checklist.md](docs/verification-checklist.md) — manual verification checklist

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.

Native dependencies such as `mpv-2.dll` and `SpoutDX.dll` are not bundled with this repository.
Users must obtain them separately and comply with their respective licenses (LGPL/GPL for mpv,
depending on build; the Spout2 license for SpoutDX).

## 日本語

TimecodeSyncPlayer は、LTC（Linear Timecode）音声信号を受信し、プレイリスト上の動画クリップを
タイムコードにフレーム単位で同期させて再生する Windows / WPF 製アプリケーションです。
ライブショーでの使用を想定した堅牢な設計になっています。

主な特徴:

- LTC音声入力に同期したフレーム単位の再生
- プレイリストとクリップごとのタイムラインオフセット
- Continue / Single の同期モードと、タイムコード欠落時のBlack/Freeze表示
- Spout2出力によるVJツールとの連携
- libltcに依存しない純C#実装のLTCデコーダ
- libmpvのソフトウェアレンダリングAPIによる映像描画

動作には .NET 8 SDK と Windows 10/11 x64 環境が必要です。また `mpv-2.dll` は必須、
`SpoutDX.dll` はSpout出力を使う場合のみ必要です。詳細なセットアップ手順は `docs/SETUP.md` を参照してください。
