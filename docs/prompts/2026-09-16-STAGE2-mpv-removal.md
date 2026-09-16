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
