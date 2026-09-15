# TimecodeSyncPlayer

[English](README.en.md)

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![TimecodeSyncPlayer スクリーンショット](assets/screenshot.png)

Windows上でLTC（Linear Timecode）音声を受信し、プレイリスト内の動画を同期再生する
ライブショー向けビデオプレイヤーです。

## ダウンロード

**インストーラーとzipは[GitHub Releases](https://github.com/faio1230/timecode-sync-player/releases)からダウンロードできます。**

- 通常は、管理者権限不要の`TimecodeSyncPlayer-v0.2.0-setup.exe`を推奨します。
- 展開して使う場合は`TimecodeSyncPlayer-v0.2.0-win-x64.zip`を選択してください。
- GStreamer 1.28.2ランタイムは同梱しています。追加のインストールは不要です。

## 主な機能

- LTC音声入力に同期したフレーム単位の動画再生
- クリップごとのタイムラインオフセットを持つSingle／Continue同期モード
- タイムコードギャップ中のBlack／Freeze表示
- LTC信号断時のランスルー／停止動作切替
- 接続ディスプレイを選択できる外部モニターフルスクリーン出力
- VJツール連携用のSpout2出力
- プレイリストとプロジェクトの保存・読み込み
- 純C# LTCデコーダとGStreamerベースのGPU出力

## 動作要件

- Windows 10/11 x64
- 配布パッケージの実行には[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Microsoft Visual C++ 2015-2022 再頒布可能パッケージ（x64）**
  インストーラーが自動で導入します。zip版では未導入の場合に手動で導入してください
  （[vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe)）。
- LTC音声を入力できるオーディオデバイス

`SpoutDX.dll`は配布パッケージに含まれ、Spout出力を使う場合だけ利用されます。

## インストーラーの使い方

1. Releasesから`TimecodeSyncPlayer-v0.2.0-setup.exe`を実行します。ユーザー単位のため
   管理者権限は不要です。
2. スタートメニューからTimecodeSyncPlayerを起動します。

アンインストールではアプリ本体、ログ、ショートカットを削除します。
ユーザー設定`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`は再インストール時に復元できるよう
意図的に保持されます。完全に削除する場合は手動で削除してください。

## zipの使い方

1. `TimecodeSyncPlayer-v0.2.0-win-x64.zip`を、書き込み可能なフォルダーへ展開します。
2. Visual C++ 2015-2022 再頒布可能パッケージ（x64）が未導入なら
   [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe)を実行します。
3. `TimecodeSyncPlayer.exe`を起動します。

GStreamerランタイムは同梱されています。詳細は[セットアップ手順](docs/SETUP.md)を参照してください。

## 基本的な使い方

1. LTCを入力する録音デバイスを選択し、**START**を押します。
2. 動画を開くかプレイリストへ追加します。
3. Single／Continue、ギャップ動作、信号断時動作を選択します。
4. **Sync ON**を押してLTC同期を開始します。
5. 外部モニターへ出力する場合はDisplayを選択し、**FULLSCREEN**を押します。

## ソースからのビルド

[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)が必要です。

```powershell
git clone https://github.com/faio1230/timecode-sync-player.git
cd timecode-sync-player
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

ソースからのビルドではGStreamer 1.28 MSVC x64ランタイムと`tcs_gstreamer.dll`が必要です
（配布パッケージには同梱されます）。
Spout出力を使う場合は、x64版`SpoutDX.dll`も`native`フォルダーへ配置します。
詳細は[セットアップ手順](docs/SETUP.md)を参照してください。

## ドキュメント

- [セットアップとビルド](docs/SETUP.md)
- [ネイティブDLL](native/README.md)
- [アーキテクチャ](docs/ARCHITECTURE.md)
- [設定リファレンス](docs/settings.md)
- [手動検証チェックリスト](docs/verification-checklist.md)

## 実機LTCループE2Eテスト

実機E2Eは[VB-CABLE](https://vb-audio.com/Cable/)を使い、`CABLE Input`へLTCを出力して
`CABLE Output`から取り込みます。両端点が見えるWindowsローカル音声セッションで実行してください。
ffmpegも必要です。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

前提条件がない環境では、実機テストは自動的にスキップされます。

## ライセンス

TimecodeSyncPlayerは[MIT License](LICENSE)で提供されます。配布物に関係する第三者ライセンスは
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)に記載しています。

## クレジット

Studio Sandixが開発しています。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
