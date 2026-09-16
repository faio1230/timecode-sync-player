# LTC シナリオの 1 コマンド実行（検証機向けランナー）と D18

作成: 2026-09-17 09:40、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（`9087633` 以降）** を `agent-a` へ通常マージしてから。製品コードには触らない（テスト基盤とスクリプトだけ）。
仕様: `docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md` 4 節。除去担当が並行して `LtcScenarioE2ETests` を作る（`tests/E2E/LtcScenarioE2ETests.cs`、`make-e2e-media.ps1` の A/B/C 追加、`Fixtures/ltc-scenario.tsp`）。**それらのファイルには触らない**（衝突するため）。ランナーはフィルタで両クラスを対象にする。

## 1. やること

| # | 内容 |
| --- | --- |
| D18 | `tests/…/Helpers/E2EAppRunner.cs` の `ResolvePrereqs`: `TIMECODE_SYNC_PLAYER_E2E_APP_PATH` が指す exe と同じディレクトリに `gstreamer\bin\gstreamer-1.0-0.dll` があれば、それを同梱ランタイム（`Bundled`）として認める。環境変数 `GSTREAMER_1_0_ROOT_MSVC_X86_64` は不要にする。`TimecodeSyncPlayerFixture` 側にも同じ検査があれば揃える。単体テストで両経路（env あり / 同梱）を固定 |
| ランナー | `scripts/run-ltc-scenarios.ps1`（PowerShell 5.1、CRLF、`.ps1` の罠: バックスラッシュと制御文字に注意）。引数 `-AppExe`（省略時は Debug ビルド）、`-ReportDir`（既定 `TestResults\ltc-scenarios\<UTC>`）、`-Cycles`（`TIMECODE_LTC_SCENARIO_CYCLES` へ）、`-Filter`（既定 `FullyQualifiedName~LtcHardwareLoopE2ETests|FullyQualifiedName~LtcScenarioE2ETests`）、`-SkipBuild`。手順: 前提検査（CABLE Input / Output が Active、ffmpeg、dotnet、対象 exe、同梱 GStreamer か開発環境の GStreamer）→ `make-e2e-media.ps1` → tests のビルド → `dotnet test --logger trx` → 対象アプリの `logs\`（両ログ）とテストのジャーナル・画像を `ReportDir` へ複製 → 要約（合格 / 失敗 / スキップ、失敗テスト名、アプリログの ERR/FTL 行数、残プロセス）を標準出力に。終了コードはテストの結果に従う |
| 手順書 | `docs/RELEASE-PROCEDURE-0.4.md` 1.5 節の暫定記述（PATH を足す）を、ランナーの 1 行に置き換える |

## 2. 検証

- 非E2E 全件（D18 の単体を含む）
- ランナーの空振り（`-Filter` に存在しない名前を渡して、前提検査と要約が動くこと）と、`LtcHardwareLoopE2ETests` だけを対象にした実行 1 回（実機。一報のうえ親の合図。除去担当と重ねない）
- `grep -c $'\x07\|\x08'` で制御文字 0、CRLF

## 3. 報告

コミット、ランナーの使い方 3 行、実行結果（件数、要約の写し）、証跡パス。**合否は書かない。ローカルの絶対パスは書かない。**
