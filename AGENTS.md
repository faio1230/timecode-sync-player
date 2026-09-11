# AGENTS.md

LTC に同期して動画を再生する Windows WPF アプリ（.NET 8 / x64）。mpv で再生し、Spout2 へ映像を出力する。

## 作業の基本

- コミットメッセージは日本語で書く。
- このファイルと変更対象のコードを起点に、必要な資料だけ読む。過去の計画書・棚卸し・他エージェント用の規約を一括で読み込まない。
- 通常開発は Codex の標準機能で進める。Superpowers などの定型ワークフローは前提にしない。
- 変更に応じた検証を行う。実機・ネイティブ DLL が必要な検証は、実施できた範囲を報告する。

## ビルド・テスト

Windows ネイティブの PowerShell、Debug 構成を使用する。ソリューションは `TimecodeSyncPlayer.slnx`。

```powershell
dotnet build src/TimecodeSyncPlayer/TimecodeSyncPlayer.csproj
dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj
```

実行ファイルとログは `src/TimecodeSyncPlayer/bin/Debug/net8.0-windows/` 以下。
動画再生には `native/libmpv-2.dll`（従来名 `mpv-2.dll` も対応）、Spout 出力には `SpoutDX.dll` が必要。

## 実装上の要点

- mpv レンダー API は単一の専用スレッドで直列実行する。シーク発行と WriteableBitmap・Spout へのフレーム公開は UI スレッドで行う。
- mpv の設定は `vo=libmpv`。ネイティブ構造体の ABI とコールバックデリゲートの寿命を維持する。
- `Path`・`File`・`Directory` を使用するファイルでは `using System.IO;` を明記する。

必要時の参照先: [内部構造・mpv/LTC の制約](docs/ARCHITECTURE.md)、[セットアップ](docs/SETUP.md)、[DLL の入手](native/README.md)、[実機検証](docs/verification-checklist.md)。
