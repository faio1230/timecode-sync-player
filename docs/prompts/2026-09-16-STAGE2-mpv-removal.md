# 段 2 の後半: mpv の削除

作成: 2026-09-16 19:45、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（段 2 前半 `10137dc` と T2 を統合した後）** を `agent-b` へ通常マージしてから始める。
計画: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` の「段 2: mpv の除去」の「そのうえで消す」。**CPU 合成（段 3）はまだ消さない。**

## 1. 親の決定（前半の項目 5 の回答）

**GStreamer ソースが未接続の間（起動〜接続、復旧で player 再生成待ち）は、mpv 分岐をやめて明示的な NotReady として扱う**（案 1）。
`ComposeLayerPolicy.Decide` により Held があれば Held、無ければギャップ規則どおり（黒／フリーズ）。I7 と一致し、見た目は現状と同じ。
`EnsureSourceGeneration` の mpv 側呼び出しも整理する。

## 2. 消すもの（計画どおり）

1. mpv 実装: `Mpv.cs`、`MpvApi.cs`、`MpvRenderApi.cs`、`SpoutOutput.cs`（定数は前半で移設済み）、`MpvRenderNative` の P/Invoke 本体（型は前半で中立化済み）
2. `PlayerBackend` の設定・分岐・DI（`App.xaml.cs` の 3 か所）、`MainWindow` の mpv + GPU 分岐（`GpuFrameSink`、`PositionSecondsProvider`、`mpv.frame` トレース）
3. `OutputEngine` の mpv 経路: `mpvSource`、`CreateMpvSnapshotSource`、`snapshotInput`、`SubmitFrame`、`UploadPendingSnapshot`、`WaitUntilOrStopOrSignal` の signal 引数、`SnapshotInputMailbox` 型、`Output/MpvSnapshotSource.cs`。置き換えは 1 節のとおり
4. csproj の `libmpv-2.dll` / `mpv-2.dll` の Content Include、mpv 専用テスト、`docs/SETUP.md` の mpv 記述、`native/README.md` の mpv 行
5. player 生成失敗時の文言「mpv_create 失敗。mpv-2.dll を確認してください。」→ GStreamer 前提の文言（何を確認すべきかを書く: GStreamer ランタイム、`tcs_gstreamer.dll`）
6. **v0.3 の設定ファイルに `"backend":0` や `"outputBackend":0` が保存されていても起動できること。** 値は無視して警告ログ 1 行、設定ファイルは書き換えない。テストで固定する

## 3. 消してはいけないもの（GStreamer 経路が使っている。計画 1-2）

`MpvSessionInitializer`、`MpvStartupPropertyApplier`、`MpvPlaybackCommandBuilder`、`MpvRenderFrameExecutor`（`RenderSession` が生成）、
`IMpvApi` / `IMpvRenderApi` とその GStreamer 実装、`GstCommandTranslator`。**名前の整理は段 4。** 迷ったら「消す前に質問」。
`RenderSession` はファイルごと消さない（フレーム通知の駆動と寿命管理を GStreamer + GPU でも使っている。計画 1-3）。

## 4. 検証（計画 4 節）

| 項目 | 内容 |
| --- | --- |
| ビルド | shim（触らないはず）、アプリ、テスト。警告 0・エラー 0 |
| 非E2E | 全件（mpv 専用テストを消した分だけ減る。減った件数と名前を報告） |
| `check-shim-lock-rule.py` | PASS |
| grep | `mpv-2.dll` / `libmpv` への参照、mpv の `DllImport`、`PlayerBackend`、`mpvSource`、`CreateMpvSnapshotSource`、`snapshotInput`、`SnapshotInputMailbox`、`UploadPendingSnapshot`、`SubmitFrame`、`WaitUntilOrStopOrSignal` の signal 引数 → **0 件**（`docs/` の過去記録は除く） |
| E2E | **全件**（出荷構成）。基準は D8 統合後の 62 実行・61 合格・失敗 1（`ProjectRoundTrip` は Q1 で別担当が調査中） |
| 設定互換 | `backend:0` / `outputBackend:0` を含む settings.json で起動して警告 1 行・再生可（非E2E で固定。E2E は 1 本あれば十分） |
| 実機 | V3 を Smooth で 1 本（`scripts/run-v3-accuracy.ps1 -Backends gst -Label s2-<SHA>`）。**親の合図の後。** ロード完了が切替ごとに出ること、定常誤差が T2 段 3 の値（receipt 平均 +7〜+9ms、p95-p5 61〜65ms）から大きく外れないこと |

## 5. 守ること

- 不変条件 `docs/OUTPUT-GPU-INVARIANTS.md` I1〜I13。I12（Cpu backend は不変）はまだ有効: **`OutputBackend=Cpu` の経路は触らない**
- 型名にランタイム名を入れない（新しく作る型だけ。既存の改名は段 4）
- **公開リポジトリ**: 文書・コミット・コメントにローカルの絶対パスを書かない
- 実機を使う前に一報。同期担当が Q1 で E2E を回すので、順番は親が決める
- main への書き込みはしない。コミットは `agent-b`、日本語。大きいので **2〜3 コミットに分けてよい**（例: OutputEngine の経路置換 / mpv 実装と DI の削除 / 設定互換と文書）
- 報告: コミット、削除ファイル一覧、非E2E の増減、grep の結果、E2E 全件の件数（失敗の名前）、設計差異、未検証。合否は書かない

---

## 実装側メモ（2026-09-16、agent-b。後任が同じことを調べ直さないための記録）

### コミットと検証結果

- `c9d62ee` refactor: mpv の再生経路・実装・DI を削除し、未接続中は明示的な NotReady にする
- `0f1fd09` feat: v0.3 の backend/outputBackend 設定を無視して出荷構成で起動する
- `da1600c` merge: main（Q1 修正 `ffc2dde` 含む）取り込み
- 検証: ビルド 0 警告 0 エラー / 非E2E 1977 成功・失敗 0 / `check-shim-lock-rule.py` PASS /
  E2E 全件 69 検出・63 実行・63 合格・失敗 0・スキップ 6（opt-in 5 + D8 ハーネス）/
  V3 1 本 `s2-da1600c-ltc25-gst`（sample 平均 -28.5ms・p95-p5 35.0ms、receipt 平均 +8.6ms・p95-p5 62.7ms、
  ロード完了 10/10、デバイス消失 0）。V3 の analysis / analysis-receipt はどちらも INCOMPLETE 表記
  （同レポートに "Accuracy acceptance limits are not defined" の注記）

### 段 2 で気づいた注意点

1. 未接続中は `ComposeTick` の else 節が明示的な NotReady。trace は `compose.acquire`（ImageId=0）と
   `source.acquire`（detail=NotReady）、skip は **`compose.sourceNotConnected`**（従来の
   `compose.sourceNotReady` とは別 detail）。起動直後・player 再生成待ちの解析はこの detail を見る
2. `VblankWaitTimer.WaitUntilOrStopOrSignal` と `LoopWaitResult` を削除し、GPU ループは
   `WaitUntilOrStop` のみ（signal は `SnapshotInputMailbox.ReadyHandle` だった）
3. `RenderSession.GpuFrameSink` / `PositionSecondsProvider` / `mpv.frame` トレースは削除。
   `MpvRenderFrameExecutor` と `IMpvRenderApi` 経路は残置
4. E2E の前提を `tcs_gstreamer.dll` + GStreamer ランタイムに変更（`E2EAppRunner.ResolvePrereqs` /
   `TimecodeSyncPlayerFixture`）。**bin に古い `libmpv-2.dll` が残っていても前提は通る**（csproj はコピーしない）
5. `SpoutOutput.cs` と同時に `ISpoutNativeApi` が消え、`SpoutFrameTransfer` / `SpoutGpuCompletion` は
   製品コードから参照ゼロ（`SpoutFrameTransferTests` だけが触る）。段 3 の CPU Spout 除去対象
6. 設定互換は生 JSON を `JsonDocument` で見る: `backend` は**キーの存在**で警告、`outputBackend==0` は
   Gpu へ上書き、ファイルは書き換えない。`OutputBackend.Cpu` の enum・`OutputBackendResolver`・
   `OutputBackendState` は I12 のため残置（設定からは到達不能。`OutputBackendState` は初期化前
   プレースホルダ `Effective=Cpu` のまま）
7. `scripts/run-v3-accuracy.ps1` は今も `{"backend":1,...}` を書くため、V3 の起動ログに廃止キー警告が
   1 行出る（動作に影響なし。段 3/5 でキーを落としてよい）
8. `MpvStartupPropertyApplier` の `vo=libmpv` 1 行は段 4 まで存置（親の指示）
9. 削除テスト 38 ケース: `MpvRenderNativeTests` 1 / `MpvSnapshotSourceTests` 7 /
   `MpvLibraryNameResolverTests` 3 / `SpoutOutputTests` 26（Fact 15 + Theory 11）/
   `AppSettingsTests.ValidateSettings_RejectsInvalidBackend` 1。追加 3: `AppSettingsCompatibilityTests` 2 /
   `LegacySettingsE2ETests` 1。非E2E 2013 → 1977
10. `MpvLibraryNameResolver` を削除し `NativeLibraryResolver` は GStreamer 専用に。
    `App.xaml.cs` の DI は GStreamer 実装を直接解決

### 段 3 で引っかかりそうな箇所

1. **D4 ロード安定ゲート**: `RenderedFrameCounter` が CPU=WriteableBitmap 数 /
   GPU=`PublishedFrameCount` の 2 経路。CPU 合成を消すときは GPU 固定にし、`gpuCompositing=false`
   分岐と関連テスト（`RenderedFrameCounterTests`、`TimecodeSyncServiceTests` の `gpuPublishedFrames`）を整理する
2. `RenderSession.PublishSnapshot` から段 2 で GpuFrameSink の早期 return を消した。CPU 経路
   （snapshot → `FrameRenderer` → `PreviewFramePresenter` / Spout、フリーズバッファ）を消しても、
   **フレーム通知の駆動と寿命管理**（`NativeCallback_*` / `Callback_*` / `Dispose_*`）、
   世代・sequence 逆行防止、`afterFrameProcessed`、`SourceFrameReady`（D4 の `ObserveFrameReady`）の
   呼び出し元は残す
3. `OutputBackendState` の初期化前プレースホルダ `Effective = Cpu`（MainWindow を初期化せず構築する
   単体テスト用）は段 3 で必ず引っかかる。初期化必須にするか、テスト側を直す
4. shim の `tcs_player_leased_cpu_copy` 削除（C ABI・I13 の管轄）の参照箇所:
   `native/gst-shim/include/tcs_gstreamer.h`、`src/tcs_gstreamer.cpp`、`test/shim_test.cpp`、`README.md`、
   `GstNative.Imports`、`IGstNativeApi.LeasedCpuCopy`、`GstNativeApi.LeasedCpuCopy`、
   `GstBackendState`（`RenderInto` 経由）
5. CPU Spout 系（`SpoutFrameTransfer` / `SpoutGpuCompletion` / `SpoutOutputPolicy.InitializeCpuSpout`）は
   製品から参照ゼロ。計画 3 節の一覧と合わせて削除単位を決める
6. `ExitDialogE2ETests.ForceExit_WhileFullscreenAndSpout_ExitsWithCodeTwo` は Spout 有効が前提
   （無効環境は Skip）。段 3 で Spout 経路を触ったらこのテストの前提を再確認する
