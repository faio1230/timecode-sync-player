# TimecodeSyncPlayer

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

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
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) for the release packages
- An x64 libmpv DLL, obtained separately because it is not included in either release package
- An audio input device carrying LTC

`SpoutDX.dll` is included in both release packages. It is only needed when using Spout output.

## Using the installer

1. Run `TimecodeSyncPlayer-v0.1.0-setup.exe`. Installation is per-user and does not require
   administrator privileges.
2. Leave **Download mpv now (run get-mpv.ps1)** selected on the completion screen.
3. Start TimecodeSyncPlayer from the Start menu.
4. Select the audio capture device carrying LTC and press **START**.
5. Load a video or playlist, then press **Sync ON**.

## Using the release zip

1. Extract `TimecodeSyncPlayer-v0.1.0-win-x64.zip` to a writable folder.
2. Open PowerShell in that folder and install libmpv:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1 -DestinationDirectory .
   ```

3. Start `TimecodeSyncPlayer.exe`.
4. Select the audio capture device carrying LTC and press **START**.
5. Load a video or playlist, then press **Sync ON**.

See the [native dependency guide](native/README.md) for manual libmpv installation.

## Building from source

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is required.

```powershell
git clone <this-repo-url>
cd timecode-sync-player
powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

Spout output additionally requires an x64 `SpoutDX.dll` in the `native` directory when building
from source. See the [setup guide](docs/SETUP.md) for details.

## Documentation

- [Setup and build](docs/SETUP.md)
- [Native dependencies](native/README.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Settings reference](docs/settings.md)
- [Manual verification checklist](docs/verification-checklist.md)

## Hardware LTC loop E2E tests

The hardware E2E suite sends LTC to `CABLE Input` and captures it from `CABLE Output` through
[VB-CABLE](https://vb-audio.com/Cable/). Run it from a local Windows audio session where both
endpoints are visible; an RDP session may replace local devices with Remote Audio. ffmpeg is also
required for synchronization tests.

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

Hardware tests skip automatically when their prerequisites are unavailable.

## License

TimecodeSyncPlayer is available under the [MIT License](LICENSE). Distribution-specific third-party
terms are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## 日本語

TimecodeSyncPlayerは、ライブショー向けのWindows用LTC同期ビデオプレイヤーです。
v0.1.0は、将来の1.0リリースに向けて実際のショーで検証するためのベータ版です。

### 主な機能

- LTC（Linear Timecode）音声入力に同期したフレーム単位の動画再生
- クリップごとのタイムラインオフセットを持つSingle／Continue同期モード
- タイムコードギャップ中のBlack／Freeze表示
- LTC信号断時のランスルー／停止動作切替
- VJツール連携用のSpout2出力
- 純C# LTCデコーダとlibmpvソフトウェアレンダリング

### 動作要件

- Windows 10/11 x64
- 配布パッケージの実行には[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- どちらの配布パッケージにも含まれないx64版libmpv DLL
- LTC音声を入力できるオーディオデバイス

`SpoutDX.dll`は両方の配布パッケージに同梱され、Spout出力を使う場合だけ利用されます。

### インストーラーの使い方

1. `TimecodeSyncPlayer-v0.1.0-setup.exe`を実行します。ユーザー単位のため管理者権限は不要です。
2. 完了画面の**mpvを今ダウンロードする（get-mpv.ps1を実行）**を選択したまま完了します。
3. スタートメニューからTimecodeSyncPlayerを起動します。
4. LTCを入力する録音デバイスを選択し、**START**を押します。
5. 動画またはプレイリストを読み込み、**Sync ON**を押します。

### 配布zipの使い方

1. `TimecodeSyncPlayer-v0.1.0-win-x64.zip`を、書き込み可能なフォルダへ展開します。
2. 展開先でPowerShellを開き、libmpvを導入します。

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1 -DestinationDirectory .
   ```

3. `TimecodeSyncPlayer.exe`を起動します。
4. LTCを入力する録音デバイスを選択し、**START**を押します。
5. 動画またはプレイリストを読み込み、**Sync ON**を押します。

手動でlibmpvを導入する場合は[ネイティブDLLガイド](native/README.md)を参照してください。

### ソースからのビルド

[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)が必要です。

```powershell
git clone <this-repo-url>
cd timecode-sync-player
powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

ソースビルドでSpout出力を使う場合は、x64版`SpoutDX.dll`も`native`フォルダへ配置します。
詳細は[セットアップ手順](docs/SETUP.md)を参照してください。

### ドキュメント

- [セットアップとビルド](docs/SETUP.md)
- [ネイティブDLL](native/README.md)
- [アーキテクチャ](docs/ARCHITECTURE.md)
- [設定リファレンス](docs/settings.md)
- [手動検証チェックリスト](docs/verification-checklist.md)

### 実機LTCループE2Eテスト

実機E2Eは[VB-CABLE](https://vb-audio.com/Cable/)を使い、`CABLE Input`へLTCを出力して
`CABLE Output`から取り込みます。両端点が見えるWindowsローカル音声セッションで実行してください。
RDPではローカルデバイスがリモートオーディオへ置き換わる場合があります。同期テストには
ffmpegも必要です。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

前提条件が無い環境では、実機テストは自動的にスキップされます。

### ライセンス

TimecodeSyncPlayerは[MIT License](LICENSE)で提供されます。配布物に関係する第三者ライセンスは
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)に記載します。

## Credits

Developed by Studio Sandix

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
