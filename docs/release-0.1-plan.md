# リリース v0.1.0 準備プラン

作成: 2026-07-15。main（機能完成時点: LTC信号断モード込み、テスト951件全グリーン）を
初の配布可能バージョン **v0.1.0（ベータ）** として整える。1.0 への昇格条件は
「本番ショー数回の無事故完走」であり、本リリースはその検証を始めるための配布物。

**実行ルールは docs/test-coverage-roadmap.md の「一括実行ルール」に従う。** 上書き事項:
- ブランチ: release/0.1-prep。push しない。**git タグの作成・GitHub Release の公開もしない**
  （人間レビュー後に実施する。成果物はローカルに用意するところまで）
- E2E ゲートは全件（実機 LTC 11件含む・Skip なし）
- R0 以外は挙動を変えない（R0 は仕様準拠、R5 のバージョン表示は追加のみ）

## R0: mpv DLL リネームの撲滅（最初にやる — 以降の文書はリネーム不要前提で書く）

現状 `Mpv.cs` / `MpvRenderNative.cs` の `DllImport("mpv-2.dll")` 固定のため、上流配布の
`libmpv-2.dll` をリネームしないと動かない。これを解消する:
- `NativeLibrary.SetDllImportResolver` をアプリ起動の最初期（P/Invoke 初回呼び出し前、
  App 静的コンストラクタ等）に登録し、`mpv-2.dll` 要求時に `mpv-2.dll` → `libmpv-2.dll`
  の順で `NativeLibrary.TryLoad` する。どちらも無ければ IntPtr.Zero を返し従来の失敗挙動に委ねる
- 候補順の決定ロジックは純関数（例: `MpvLibraryNameResolver`）に切り出しユニットテスト
- csproj の `native/` コピー条件（アプリ・テスト両プロジェクト）に `libmpv-2.dll` を追加
- **P/Invoke 宣言・デリゲート保持・構造体レイアウトには一切触れない**（CLAUDE.md の既知問題）
- 検証: 既存 E2E 全件（現在の mpv-2.dll で回帰なし）+ 可能なら native/mpv-2.dll を
  一時的に libmpv-2.dll にリネームして E2E 1件が通ることを確認し、元に戻す

## R1: mpv 導入自動化スクリプト

`scripts/get-mpv.ps1`（新規、Windows PowerShell 5.1 互換）:
- libmpv の Windows x64 ビルド（mpv.io/installation が案内する配布元。実装時に実在する
  配布 URL を確認すること）をダウンロード → 展開 → DLL を `native/` に配置（リネームしない）
- 7z アーカイブしか無い場合は 7-Zip 公式の `7zr.exe` を一時取得して展開する等、
  **実際にスクリプトを実行して端から端まで動くことを検証**してからコミット
- 進行状況表示・失敗時の明確なエラーメッセージ・再実行安全（既存 DLL は確認の上上書き）

## R2: ネイティブ DLL 案内の全面改訂

- `native/README.md`: 「推奨: `scripts/get-mpv.ps1` を1回実行」を先頭に。手動手順も
  配布ページ直リンク・アーカイブ内のファイル名・配置先まで迷わないステップバイステップに書き直す
- SpoutDX.dll: リリース zip に**同梱する**方針（BSD ライセンス、R4 の表記に含める）。
  README にも「zip 版には同梱済み、ソースからビルドする場合のみ用意」と明記

## R3: README 整備

- **リンク監査**: 日本語節に英語節と対称なリンク付きドキュメント一覧を追加
  （現状は日本語節にドキュメント一覧が無く、`docs/SETUP.md` 参照がバッククォート地の文のみ）。
  バッククォートのファイル参照は全てリンク化
- **使い方（ユーザー向け）節**: zip を展開 → get-mpv.ps1（またはリリースzipの手順）→
  起動 → LTC デバイス選択 → Sync ON、の最短経路。開発者向け節とは分離
- **クレジット節**（末尾）: `Developed by Studio Sandix` として `<picture>` タグで
  テーマ対応ロゴを表示（ライト: `assets/studio-sandix-logo.png` /
  ダーク: `assets/studio-sandix-logo-white.png`、幅 400px 程度）
- バッジ: 既存 CI バッジ維持。ライセンスバッジ（MIT）を追加してよい

## R4: ライセンス・表記

- `LICENSE`（新規）: MIT、`Copyright (c) 2026 Studio Sandix`
- `THIRD-PARTY-NOTICES.md`（新規）: 配布物に含まれる/リンクされるもののみ:
  - libmpv（LGPLv2.1+/GPL、**非同梱** — 入手方法の案内のみである旨を明記）
  - SpoutDX / Spout2（BSD-3、zip 同梱）
  - NAudio（MIT）、Serilog + Serilog.Sinks.File（Apache-2.0）、
    Microsoft.Extensions.DependencyInjection（MIT）
  - 各ライセンス全文または正確な参照。テスト専用依存（xUnit/FlaUI 等）は配布物外なので不要
- csproj に `PackageLicenseExpression` 相当のメタデータ（`Copyright`、`Authors` 等）

## R5: バージョニングと表示

- `TimecodeSyncPlayer.csproj`: `Version 0.1.0`、`Product`、`Copyright (c) 2026 Studio Sandix`
- タイトルバー末尾に `v0.1.0`（アセンブリバージョンから動的取得、ハードコード禁止）
- 起動ログの1行目付近にバージョンを出力（現場調査用）
- `docs/settings.md`（新規）: settings.json の全キー・型・既定値・クランプ範囲・
  「タイムアウト系はアプリ再起動が必要」を一覧化

## R6: Release 構成の検証

- `dotnet build -c Release` 警告0 → `dotnet test -c Release`（非E2E全件 + E2E全件・実機込み）
  全グリーンを確認。E2E が Debug パスを仮定している場合はテスト側を構成非依存に直してよい
  （プロダクション挙動は変えない）

## R7: 配布物の組み立て

- `scripts/package-release.ps1`（新規）: Release ビルド出力から配布 zip
  `TimecodeSyncPlayer-v0.1.0-win-x64.zip` を組み立てる:
  アプリ一式 + SpoutDX.dll 同梱 + LICENSE + THIRD-PARTY-NOTICES.md + 簡単な README.txt
  （mpv の入手手順 = get-mpv.ps1 同梱 or 手動手順）。mpv-2.dll は同梱しない
- `CHANGELOG.md`（新規）: v0.1.0 の変更点（初回なので主要機能の箇条書き + 既知の制限）
- 実際にスクリプトで zip を生成し、**別フォルダに展開して mpv を配置すれば起動することを確認**
  （E2E ではなく手動起動確認レベルでよい。Spout 送信は SpoutDX 同梱で有効になること）

## R8: インストーラー作成

- Inno Setup 6を使用する。`ISCC.exe`は環境変数等で上書き可能にし、PATH上の実行ファイルに
  加えて`C:/Users/codea/AppData/Local/Programs/Inno Setup 6/ISCC.exe`を既定候補として解決する
- `scripts/installer.iss`（新規）: `PrivilegesRequired=lowest`のユーザー単位インストールとし、
  Releaseビルド出力一式、`SpoutDX.dll`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`CHANGELOG.md`、
  mpv取得用`get-mpv.ps1`を含める。`mpv-2.dll`／`libmpv-2.dll`は同梱しない
- スタートメニューショートカットとアンインストーラを作成する。インストール完了画面には
  「mpvを今ダウンロードする」チェック項目を表示し、選択時に同梱`get-mpv.ps1`を実行して
  インストール先へlibmpvを配置する
- 出力名は`TimecodeSyncPlayer-v0.1.0-setup.exe`。`scripts/package-release.ps1`の1回の実行で
  zipとsetup.exeの両方を生成でき、ISCCパスは環境変数または引数で上書き可能にする
- 実際のsetup.exeでインストールし、mpvダウンロードオプション込みで起動できること、
  アンインストール後にインストール先・ショートカット等の痕跡が残らないことを確認する

## 実行順とゲート

R0 → R1 → R2 → R3 → R4 → R5 → R6 → R7 → R8。R0 と R5 はコード変更を含むため個別に
非E2E + E2E 全件ゲート。R6 で Release 構成ゲート。完了時に progress へ記録。
タグ付け・GitHub Release 公開・実機チェックリスト（docs/verification-checklist.md）一巡は
人間側の作業として残す。
