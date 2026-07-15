# TimecodeSyncPlayer

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)

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

### 実機 LTC ループテスト

実機 LTC ループ E2E テストは、従来の DAW と VB-Audio Matrix を使った手動確認を、
VB-CABLE 経由で自動化します。テストが LTC 信号を `CABLE Input` へ直接出力し、
アプリが `CABLE Output` から取り込んで、タイムコード表示と動画の同期シークを検証します。

[VB-CABLE](https://vb-audio.com/Cable/)（無印版）をインストールしてください。
インストール後、`CABLE Input` と `CABLE Output` がオーディオデバイス一覧に現れない場合は、
Windows の再起動が必要なことがあります。RDP 接続ではローカルのオーディオデバイスが
「リモート オーディオ」に置き換わる場合があるため、両デバイスが見えるローカルコンソールの
音声セッションで実行してください。同期シークのテストには ffmpeg も必要です。

リポジトリのルートで次のコマンドを実行します。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

VB-CABLE が未導入の環境では自動的にスキップされます。オーディオデバイスを持たない CI でも
実機テストは実行されません。
