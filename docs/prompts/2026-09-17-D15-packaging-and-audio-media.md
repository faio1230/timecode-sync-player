# D15 と v0.4.1 の同梱・素材・E2E・アプリ側（v0.4.1）

作成: 2026-09-17 05:50、親。担当: 除去担当（`w5:p6`、作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（`452a828` 以降）** を `agent-b` へ通常マージしてから。
背景: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` 末尾「D12〜D15」。shim 側（D12〜D14、O1）は同期担当が直す。**shim のソースには触らない**（衝突するため）。

## 1. やること

| # | 内容 |
| --- | --- |
| D15 | `scripts/package-release.ps1` の `$gstPluginDlls` に **`gstaudioresample.dll`** と **`gsttypefindfunctions.dll`** を足す。依存の閉包（両 DLL が要求する bin の DLL）とライセンス文書の一覧に不足が無いか、`dumpbin /dependents` か既存の閉包チェックで確認し、足りなければ追加。同梱一覧の根拠（「実ロード 17 プラグイン」）を記した文書があれば更新 |
| 素材 | `scripts/make-e2e-media.ps1` に音声付き素材を 2 本追加: (a) H.264 720p30 + AAC **44.1kHz**、(b) 同じで **48kHz**・映像トラック先頭（ffmpeg 既定）。相対パス、作業ツリー内に生成 |
| E2E | 上の 2 本を読み込んで **再生が進む**（位置が進む、`all video profiles failed` が出ない）ことを確認するテストを 1 本ずつ追加。shim 修正前は失敗し、修正後に通る（**修正前に失敗することを 1 回記録**し、main への同期担当の統合後に再実行） |
| V1 行列 | `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の V1 の表に「AAC 44.1kHz」「映像トラック先頭 + 音声」の行を足す（結果欄は空で。親が埋める） |
| アプリ側 O1 | 起動時（GStreamer 初期化の前、`GstNative.cs` の環境変数設定と同じ場所）に **`TCS_LOG_FILE`** を `logs\tcs-gst-YYYYMMDD.log`（Serilog と同じ logs ディレクトリ）へ設定する。利用者が既に設定していれば上書きしない。ログの肥大を避けるため、起動時に 7 日より古い `tcs-gst-*.log` を消す（Serilog の保持と同程度）。`SETUP.md` の「ログの場所」に 1 行足す |
| 手順 | `docs/RELEASE-PROCEDURE-0.4.md` 1 節の「展開して起動確認」に**音声付き素材（44.1kHz）を 1 本**含めるよう追記 |

## 2. 検証

- ビルド 0/0、非E2E 全件、追加した E2E 2 本（shim 修正前は失敗の記録、統合後は成功）
- `package-release.ps1` を **実行して**（Release の shim を `native/tcs_gstreamer.dll` に置く手順は `docs/RELEASE-PROCEDURE-0.4.md`）、zip の `lib\gstreamer-1.0` に 19 個の DLL があること。作成した配布物は **公開しない**（親が v0.4.1 で作り直す）
- `TCS_LOG_FILE` の設定で `logs\tcs-gst-*.log` ができること（shim 統合前は空ファイルでよい）

## 3. 報告

コミット、同梱一覧の差分と閉包の確認結果、E2E の結果（修正前/後）、証跡パス。**合否は書かない。ローカルの絶対パスは書かない。** 実機（E2E）は一報のうえ親の合図を待つ（同期担当と重ねない）。
