# 段 4: 型付き再生 API への置き換えと名前の整理

作成: 2026-09-16 23:40、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（段 3 を統合した後）** を `agent-b` へ通常マージしてから始める。
材料: `docs/V04-STAGE4-TYPED-API-SURVEY-2026-09-16.md`（棚卸し・案・衝突表。**10 節が親の判断**）。計画: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` 段 4（案 B）。

## 1. 決定（調査文書 10 節の再掲。変えない）

| # | 決定 |
| --- | --- |
| 1 | `osd-msg3` のデバッグ OSD は消す |
| 2 | `StepFrame` は API に含めない（テストも削除） |
| 3 | `seeking` はアダプタ内に残し、API では `IsSeeking` として表現 |
| 4 | `SetDecodeMode` は再生 API に含めない（`GstBackendState` の初期化のまま） |
| 5 | 相対シークはクライアント計算（`Seek(absolute)` ＋呼び出し側で加算）。文字列書式の欠陥はこれで消える |
| 6 | 失敗の表現は結果型 `PlaybackResult(bool Success, string? Error)` を `Load` / `Seek` / `SetPaused` / `Stop` / `SetRate` に。`SetVolume` / `SetMute` は void（失敗はアダプタ内でログ） |
| 7 | 移行は段階（呼び出し側ごと）。下の順序 |

## 2. 順序（各段でビルド・非E2E・E2E の一部を通し、コミットを分ける）

1. **型付き API と実装を足す（既存の文字列経路は残す）**: `IPlaybackApi`（調査 4-1 のメソッド案 17 個から、決定 2・4 で外すものを除く）と
   `GstPlaybackApi`（shim を直接呼ぶ。`GstCommandTranslator` を経由しない）。`IRenderUpdateSource`（`IMpvRenderApi` の後継。`RenderUpdateFn` の契約は不変）。
   DI に登録。単体テストは `IGstNativeApi` の偽物で
2. `GapPlaybackCommandExecutor` / `GapFreezePathGuard` を型付き API へ
3. `PlaybackOperationsCoordinator` / `AudioControlCoordinator` を型付き API へ
4. `MainWindow` の読み取り系（time-pos、duration、pause、seeking ほか）と残りの操作を型付き API へ
5. **削除と改名**: `MpvPlaybackCommandBuilder`、`GstCommandTranslator`、`MpvStartupPropertyApplier`（`pause=yes` の初期化は `GstPlaybackApi` の初期化へ）、
   `MpvSessionInitializer`（役割を `GstPlaybackApi` の生成へ）、`IMpvApi` / `IMpvRenderApi` / `GstMpvApiAdapter` / `GstMpvRenderApiAdapter` / `MpvRenderFrameExecutor` と
   そのテスト。残る型名・ファイル名から `Mpv` を無くす（役割で命名。ランタイム名を入れない）。`vo=libmpv` の 1 行もここで消える

## 3. 守ること

- 挙動を変えない（決定 6 の失敗表現と、決定 1・2 の削除、7-4 の欠陥の解消を除く）。同期の目標や補正には触らない
- 不変条件 `docs/OUTPUT-GPU-INVARIANTS.md` I1〜I13（I12 は失効済み）。I6（コールバックでブロックしない）と I13（`frame_lock` 保持中に GStreamer の状態変更・シークを呼ばない）は shim 側の規則。**shim の C ABI は変えない**（`tcs_player_seek` 等の既存関数で足りるはず。足りなければ質問）
- 型名にランタイム名を入れない
- **公開リポジトリ**: ローカルの絶対パスを書かない
- 実機を使う前に一報。同期担当と重ねない。順番は親が決める
- main への書き込みはしない。コミットは `agent-b`、日本語

## 4. 検証

| 項目 | 内容 |
| --- | --- |
| 各段 | ビルド 0/0、非E2E 全件、E2E の一部（`GStreamerBackend_SurvivesRepeatedTrackSwitches`、`ExitDialogE2ETests`、`SystemScenarioE2ETests`） |
| 最終 | E2E 全件（基準は段 3 の全件合格）、V3 1 本（Smooth。sample 平均 / p95-p5 が段 3 の -28.9 / 38.3 から大きく外れないこと）、`check-shim-lock-rule.py` PASS |
| grep（最終） | `IMpvApi`、`IMpvRenderApi`、`GstMpv`、`MpvPlaybackCommandBuilder`、`GstCommandTranslator`、`MpvStartupPropertyApplier`、`MpvSessionInitializer`、`MpvRenderFrameExecutor`、`libmpv`、`no-osd`、`absolute+exact` → src / tests で 0 件。型名・ファイル名の `Mpv` → 0 |
| 報告 | 段ごとのコミット、対応表（旧文字列呼び出し → 新メソッド）、非E2E の増減、E2E、V3、grep、設計差異、未検証。合否は書かない |
