# TimecodeSyncPlayer

[English](README.en.md)

[![CI](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml/badge.svg)](https://github.com/faio1230/timecode-sync-player/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![TimecodeSyncPlayer スクリーンショット](assets/screenshot.png)

Windows上でLTC（Linear Timecode）音声を受信し、プレイリスト内の動画を同期再生する
ライブショー向けビデオプレイヤーです。

## ダウンロード

**インストーラーとzipは[GitHub Releases の最新の安定版（Latest）](https://github.com/faio1230/timecode-sync-player/releases/latest)からダウンロードできます。**

- 安定版は v0.6.x の最新です（v0.6.1 から）。「Pre-release」の版は検証中の版なので、本番には使わないでください。

- 通常は、管理者権限不要のインストーラー`TimecodeSyncPlayer-v<版>-setup.exe`を推奨します。
- 展開して使う場合は`TimecodeSyncPlayer-v<版>-win-x64.zip`を選択してください。
- `<版>`は最新の安定版の版番号です（例: `v0.6.4`）。
- GStreamer 1.28.2ランタイムは同梱しています。追加のインストールは不要です。
- 本番の前に[現場準備ガイド](docs/USER-MANUAL.md)を読んでください。素材の書き出し方しだいで同期の安定性が大きく変わります。

## 主な機能

- LTC音声入力に同期したフレーム単位の動画再生
- クリップごとのタイムラインオフセットを持つSingle／Continue同期モード
- タイムコードギャップ中のBlack／Freeze表示
- LTC信号断時のランスルー／停止動作切替
- 接続ディスプレイを選択できる外部モニターフルスクリーン出力
- VJツール連携用のSpout2出力
- プレイリストとプロジェクトの保存・読み込み
- 純C# LTCデコーダとGStreamerベースのGPU出力
  （H.264 を推奨、GPU でデコード。VP9・AV1・H.265 なども読めますが推奨外で、読み込み時に警告を出します。形式ごとの推奨は[現場準備ガイド](docs/USER-MANUAL.md)）
- HAP（Hap / Hap Alpha / Hap Q）の GPU デコード（v0.5.0 から）
- ProRes の GPU デコード（NVIDIA の GPU、v0.6.0 から。[gst-prores-d3d11](https://github.com/faio1230/gst-prores-d3d11) による。ほかの GPU では CPU でデコード）

## 動作要件

- Windows 10/11 x64
- **Direct3D 11.4 に対応した GPU とドライバ**（映像の合成と出力に使います）。
  対応していない環境では、起動時にそのことを知らせ、再生だけを無効にします
  （アプリは開いたままで、CPU での合成に切り替えることはしません）
- 配布パッケージの実行には[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Microsoft Visual C++ 2015-2022 再頒布可能パッケージ（x64）14.50 以上**
  （ProRes の GPU デコードのプラグインが必要とします）。インストーラーは、入っていないか古いときだけ導入し、
  そのときにインストールの途中で一度だけ管理者の確認（UAC）が出ます。zip版では、入っていないか古い場合に
  手動で導入してください（[vc_redist.x64.exe](https://aka.ms/vc14/vc_redist.x64.exe)）。
- ProRes の GPU デコードは NVIDIA（RTX 級）の GPU で検証しています。ほかの GPU では既定で CPU でデコードします
  （画面の「ProRes の GPU 復号」で切り替えられます）。
- LTC音声を入力できるオーディオデバイス

`SpoutDX.dll`は配布パッケージに含まれ、Spout出力を使う場合だけ利用されます。

## インストーラーの使い方

1. Releasesから`TimecodeSyncPlayer-v<版>-setup.exe`を実行します。ユーザー単位のため
   管理者権限は不要です。
2. スタートメニューからTimecodeSyncPlayerを起動します。

アンインストールではアプリ本体、ログ、ショートカットを削除します。
ユーザー設定`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`は再インストール時に復元できるよう
意図的に保持されます。完全に削除する場合は手動で削除してください。

## zipの使い方

1. `TimecodeSyncPlayer-v<版>-win-x64.zip`を、書き込み可能なフォルダーへ展開します。
2. Visual C++ 2015-2022 再頒布可能パッケージ（x64）14.50 以上が入っていなければ
   [vc_redist.x64.exe](https://aka.ms/vc14/vc_redist.x64.exe)を実行します。
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

- **[現場準備ガイド](docs/USER-MANUAL.md)** — 推奨する素材の形式（キーフレーム間隔・コーデック・fps）、タイムコード信号の条件、警告の読み方
- [セットアップとビルド](docs/SETUP.md)
- [ネイティブDLL](native/README.md)
- [アーキテクチャ](docs/ARCHITECTURE.md)
- [開発ロードマップ（v0.5.x〜v0.8.0）](docs/ROADMAP.md)
- [設定リファレンス](docs/settings.md)
- [手動検証チェックリスト](docs/verification-checklist.md)

## 関連プロジェクト

- **[gst-prores-d3d11](https://github.com/faio1230/gst-prores-d3d11)** — ProRes を Direct3D 11 で GPU デコードする
  GStreamer プラグイン（同じ開発者が開発、LGPL-2.1 以降）。TimecodeSyncPlayer の配布物に同梱しています。

## 実機LTCループE2Eテスト

実機E2Eは[VB-CABLE](https://vb-audio.com/Cable/)を使い、`CABLE Input`へLTCを出力して
`CABLE Output`から取り込みます。両端点が見えるWindowsローカル音声セッションで実行してください。
ffmpegも必要です。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop"
```

前提条件がない環境では、実機テストは自動的にスキップされます。

## ライセンス

TimecodeSyncPlayerは[MIT License](LICENSE)で提供されます。配布物に関係する第三者ライセンス（同梱する
GStreamer ランタイムと gst-prores-d3d11 を含む）は[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)に記載しています。

## クレジット

Studio Sandixが開発しています。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/studio-sandix-logo-white.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/studio-sandix-logo.png">
  <img alt="Studio Sandix" src="assets/studio-sandix-logo.png" width="400">
</picture>
