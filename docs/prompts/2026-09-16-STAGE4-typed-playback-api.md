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

---

## 実装側メモ（順序 5 向け。2026-09-16、agent-b）

### コミット系列（agent-b）

- `6262fd7` 順序 1: 型付き API と GStreamer 実装を追加（既存の文字列経路は残す）
- `39b0a0a` 順序 2: ギャップ経路（`GapPlaybackCommandExecutor` / `GapFreezePathGuard`）を型付き API へ
- `3cebdb1` 順序 3: `PlaybackOperationsCoordinator` / `AudioControlCoordinator` を型付き API へ
- `0909ceb` 順序 4: `MainWindow` の読み取り系と残りの操作を型付き API へ
- 基点: main `d5af2a6`。各段の検証: ビルド 0/0、非E2E 全件、E2E 一部 8 件（`TestResults/s4-order2` / `s4-order3` / `s4-order4` の `e2e-subset.trx`）

### 順序 1〜4 で追加したもの（残す）

- `Contracts/IPlaybackApi.cs`（16 メソッド）、`Contracts/PlaybackResult.cs`（`bool Success, string? Error`）、`Contracts/IRenderUpdateSource.cs`
- `Gst/GstPlaybackApi.cs`、`Gst/GstRenderUpdateSource.cs`、`Gst/GstSeekingTracker.cs`（`GstBackendState.Seeking` が所有。`GstMpvApiAdapter` 削除後も `GstPlaybackApi` が使う）
- DI: `IPlaybackApi` / `IRenderUpdateSource` は登録済み。実装は `GstPlaybackApi` / `GstRenderUpdateSource`
- テスト: `GstPlaybackApiTests` / `GstRenderUpdateSourceTests` / `FakePlaybackApi.cs`（`MainWindow` の DI 差し替え用）。`FakeGstNative` は `internal` 化して共用中（残す）
- `GstPlaybackApi` は決定 2・4 により `StepFrame` / `SetDecodeMode` を含まない。`GstBackendState.ApplyDecodeMode` は初期化のまま残す
- `GstPlaybackApi.Load` のログ文言 `Gst loadfile path=...` は SH1 の計測互換で残している（改名するなら計測手順も確認）

### 順序 5 で消す・移す（在処つき）

- `MainWindow`: `_mpvApi` は `disposeMpv` の `TerminateDestroy` のみ。`_mpvStartupPropertyApplier` は代入だけで未使用。player 生成は `_mpvSessionInitializer.Initialize(_showDebugOsd)`。`_mpv` ハンドルは `assignMpv` / `RenderSession.Create` / `IsPlayerReady` / `IsNativeSeeking` の null 判定に残存
- `DebugOsdPolicy` とそのテストは製品から未参照（決定 1）。`OsdUpdateState` の DI 登録も `MainWindow` 未使用
- `RenderSession` はまだ `IMpvRenderApi` を使用（`MpvRenderApiTypeSw` / `RenderContextCreate(params)` / `MpvRenderUpdateFrame`）。`IRenderUpdateSource` へ切替後、`RenderContextParameterBuilder`（+Tests）と `RenderTypes.RenderParam` を削除できる。`RenderUpdateFn` は残す
- `GstBackendAdapterTests` は `GstMpvApiAdapterTests` / `GstMpvRenderApiAdapterTests` / `GstBackendStateTests` が同居。アダプタ削除に合わせて整理する
- `MpvStartupPropertyApplier` の `vo=libmpv` 行を削除し、`pause=yes` の初期化は `GstPlaybackApi` の初期化へ移す
- 改名対象の残メンバー例: `ResumeMpvPause`（`ContinueOnTrackEffects`）、`IsMpvReady`（`PlaybackOperations` / `GapEnter` effects）、`WindowLoadedSessionInitializer` の `initializeMpvSession` と `MpvSessionInitialization*`、`PlaybackControlState.MpvPauseValue`。`ReadMpvTimePos` と `IsCurrentMpvPathExpectedForGapFreeze` は順序 4 で改名済み。`MainWindowResourceDisposer` のユーザー可視ラベル「mpv／GStreamer 停止」も改名候補

### 最終検証（順序 5 の完了時に回す）

- E2E 全件: 基準は段 3 の 63/63
- V3 1 本（Smooth）: 段 3 の実測 sample 平均 **-28.9**、p95-p5 **38.3**（receipt +9.4 / 60.9、seek 収束 120ms、freeze-sweep latency 0・left-censored）
- 非E2E: 順序 4 終了時点で **1709 件合格**
- `check-shim-lock-rule.py` PASS
- E2E 一部（各段と同じ 8 件）: フィルタ `FullyQualifiedName~GStreamerBackend_SurvivesRepeatedTrackSwitches|FullyQualifiedName~ExitDialogE2ETests|FullyQualifiedName~SystemScenarioE2ETests`

### 注意・段外の事象

- shim の C ABI は変えない（同期担当が並行作業）。段 4 では触っていない
- shim 実素材テストの `lease pts within one frame of the seek target` は段 3 前から失敗（main の既存ビルドでも同一。SH1 の切り分け結果が main にある）。段 5 の対象外
- 段の切り分けに `git stash` は使わない（利用者の規則）。段ごとに作業して順次コミットするか、別ブランチの WIP コミットで退避する
- 実機（E2E 全件・V3・単発 run）は一報のうえ親の合図を待つ
