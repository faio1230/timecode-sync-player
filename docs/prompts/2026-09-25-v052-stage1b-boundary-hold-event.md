# v0.5.2 段 1 の追加: 境界ホールドの解除をできごとにする（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`f6c73fc` 以降）。**振る舞いは変えない。**

## 背景

段 1（`d0646cb`）の独立レビューで、Single の境界ホールドの解除が、ラッチをまとめて消すのに
できごとになっていないと分かった（設計書 `docs/design/v0.5.2-sync-state.md` §9）。

- `SingleModeSyncCoordinator.ReleaseBoundaryHold` → `_effects.OnBoundaryHoldReleased` →
  `LtcSyncController.NotifyClipBoundaryHoldReleased` が、保持着地の値・保持値の 1 回適用・同期の保留・
  シークの保留状態を消し、追従開始の着地を終える
- 保持（Duplicate）のフレームの `ApplyClipBoundaryHoldOnly` からも起きる

段 2 の前後でできごとの列を突き合わせるとき、D33×D35 の型の干渉（境界ホールド × 保持着地）が見えるように、ここもできごとにする。

## やること

1. `SyncLifecycleEvent` に `BoundaryHoldReleased` を足す（コメントは既存に合わせる）
2. `LtcSyncController.NotifyClipBoundaryHoldReleased` を、ほかの入口と同じ形にする:
   `SyncLifecycle.Record(BoundaryHoldReleased, <source>)` → `OnLifecycle(BoundaryHoldReleased)`。
   `OnLifecycle` の新しい分岐に、今の 5 つの処理を**同じ順番で**移す。Service 側の処理（`SeekState.Clear`、
   `EndFollowStartLanding("boundary hold released")`）は今の呼び方のままでよい（`TimecodeSyncService.OnLifecycle` に移すなら順番を保つ）
3. **既存のログの行（`Single mode: boundary hold released; pending seek state and held landing latch cleared`）は文言も順番も変えない**
4. source は解除の理由が分かるもの。`ReleaseBoundaryHold` の `suffix` を渡せるなら渡す（無理なら固定の文字列でよい。
   公開 API は増やさない、internal で足りる）
5. テスト: `tests/TimecodeSyncPlayer.Tests/LatchLifetime/SyncLifecycleLogTests.cs` に、Single で境界ホールドを立てて解除したときに
   `BoundaryHoldReleased/<source>` が 1 行出るテストを足す（ホールドの立て方は `LatchArrangements.cs` の `ClipBoundaryHeld` の手順（:174 付近）を使う）

## 判定

- ビルドの警告 0
- 非 E2E が全件合格（`--filter "FullyQualifiedName!~E2ETests"`）。段 0 の 425 行が緑のまま
- E2E は `--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"` で全件合格（**LTC シナリオは外すこと。L-3 は既定 12 時間**）

## 規則

- **git の書き込み禁止**（add / commit / stash / reset / checkout / clean）。親がレビューしてコミットする
- 触るのは上のファイルだけ。段 0 の表（`LatchLifetimeTable.cs`）は変えない
- 終わったら、変えたファイル・テストの件数（非 E2E / E2E）・判断したこと（source に何を使ったか、Service 側に移したか）をこの画面に書く
