# v0.5.3 段 3b: GPU 復旧と自動送り（MediaIn > 0）の読み込みを BeginFileLoad に通す（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`97e0c19` 以降）。
背景は段 3a の表 `docs/design/v0.5.3-load-paths.md`（とくに所見 1・2・5）。**直すのは経路 #1 と #2 だけ。** #3・#4（ギャップ）は利用者の判断待ちなので触らない。

## やること

1. `MainWindow` の 2 か所で、位置つきの読み込みが成功した後に、同期側の読み込みの入口を呼ぶ:
   - #1 GPU 復旧（`LoadFile(track.FilePath, position)` の後）: source は `"gpu-recovery"`
   - #2 Continue の自動送り（`TryAdvanceContinueMode` の `LoadFile(nextTrack.FilePath, startPosition: …)` が成功し、**位置つき（startPos > 0）のとき**）: source は `"auto-advance"`。
     位置なし（MediaIn = 0）は今も `PlaybackOperationsCoordinator.LoadFile` の中で `BeginSyncFileLoad(0)` を通っているので、二重に呼ばない
   - 呼ぶのは `TimecodeSyncService.BeginFileLoad(startPosition, 描画枚数, loadIssuedQpc: 0, source)`（internal の source つき）。描画枚数はほかの呼び出しと同じ `_syncGateRenderedFrames.Read()`
2. **`PlaybackOperationsCoordinator.LoadFile` の共通の入口には入れない**（入れると Continue のトラック切替 #5 が二重になる。所見 5）
3. テスト:
   - `V053LoadPathTests` の #1（`GpuRecoveryPositionLoad_ClearsLoadLatches`）と #2（`AutoAdvanceLocatedLoad_ClearsLoadLatches`）の Skip を外し、緑になることを確かめる。
     #3・#4 は Skip のまま、理由を `"v0.5.3（利用者の判断待ち: ギャップの読み込みは別の口が要る）"` に変える
   - 自動送りで MediaIn = 0 のときに `FileLoad` が 1 回だけ（二重にならない）ことを確かめるテストを足す
   - `Sync lifecycle: FileLoad source=gpu-recovery` / `source=auto-advance` が 1 行出ることを確かめる（`SyncLifecycleLogTests` の形でよい）
4. 段 0 の表（`LatchLifetimeTable.cs`）は変えない（表の `FileLoadWithoutBegin` はハーネスの読み込みで、ギャップの 2 経路はまだ通らないため）
5. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行が緑のまま）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**。親がレビューしてコミットする
- 終わったら、変えたファイル・赤かったテストが緑になったこと・テストの件数（非 E2E / E2E）をこの画面に書く
