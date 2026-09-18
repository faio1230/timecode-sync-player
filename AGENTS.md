# AGENTS.md

LTC に同期して動画を再生する Windows WPF アプリ（.NET 8 / x64）。GStreamer で復号し、
D3D11 で合成して全画面と Spout2 へ出力する。

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
動画再生には `native/tcs_gstreamer.dll`（GStreamer shim）と GStreamer ランタイム、
Spout 出力には `SpoutDX.dll` が必要。**mpv は v0.4 で完全に除去した。**

## 実装上の要点

- **GPU 出力は D3D11.4 が必須**。`ID3D11Device5` / `ID3D11DeviceContext4` が無い環境では
  起動時にダイアログを出して再生だけを無効にする。CPU 合成へのフォールバックは無い。
- **shim のロック規則（I13）**: GStreamer の状態変更・シークを `frame_lock` 保持中に呼ばない。
  検査は `scripts/check-shim-lock-rule.py`。
- **レンダー更新コールバックのデリゲートはフィールドで保持する**（ローカル変数だけだと GC で回収されクラッシュ）。
- **再生 API は型付き・失敗は結果型**。相対シークは無い（`Seek` は絶対秒）。
- `Path`・`File`・`Directory` を使用するファイルでは `using System.IO;` を明記する。

必要時の参照先: [内部構造・LTC の制約](docs/ARCHITECTURE.md)、[セットアップ](docs/SETUP.md)、[DLL の入手](native/README.md)、[現場準備ガイド](docs/USER-MANUAL.md)、[実機検証](docs/verification-checklist.md)。
