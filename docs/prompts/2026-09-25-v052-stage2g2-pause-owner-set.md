# v0.5.2 段 2g-2: 境界ホールドの解除は、ほかの持ち主が止めていれば再開しない（§6 の 12）（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`。**振る舞いを変えるのは §6 の 12 だけ。**
入力は 2g-1 の表 `docs/design/v0.5.2-pause-owners.md`。末尾の「親の判断と 2g-2 の範囲」を先に読むこと。

## やること

1. `[Flags] internal enum PauseOwners { None = 0, SignalLoss = 1, BoundaryHold = 2, Gap = 4, ProjectRestore = 8 }` を作る（置き場所は `SyncRules.cs` の隣の新しいファイルでよい）。
   **利用者（User）は入れない**（v0.5.3、§6 の 15）
2. 集合は**新しい状態として持たない**。今の記録から組み立てる読み取り専用の口を足す:
   - 信号断: `LtcSyncController` に `internal bool IsSignalLossPauseOwned => _signalLoss.IsPauseOwned;`
   - ギャップ: `GapFreezeHandler` に `_pauseOwnedByGap` の読み取り専用プロパティ（§4 のとおり中身は変えない。読む口を足すだけ）
   - プロジェクト復元: `MainWindow` の `_projectRestorePauseState.IsPending`
   - 境界ホールド: `SingleModeSyncCoordinator.IsBoundaryHeld`
3. 判定は `SyncRules` に純関数で足す: `ShouldResumeOnBoundaryHoldRelease(PauseOwners others)` → `others == PauseOwners.None`
4. `MainWindow` の `SetEndHold` の解除側（`held == false`）で、境界ホールド以外の持ち主を組み立てて 3 の関数に渡し、
   **false なら `SetPaused(false)` と `ApplyPauseState(false)` を呼ばない**。そのときだけ新しいログを 1 行出す:
   `Single mode: boundary hold released; playback stays paused owners={Owners}`（既存のログの文言は変えない）。
   ホールドする側（`held == true`）と、解除の後の処理（`OnBoundaryHoldReleased` など）は今のまま
5. テスト:
   - `BoundaryHoldPauseOwnerTests.BoundaryHoldRelease_WhileSignalLossPaused_DoesNotResumePlayback` の Skip を外し、緑になることを確かめる
   - `BoundaryHoldRelease_WhileUserPaused_DoesNotResumePlayback` は Skip のまま、理由を `"v0.5.3（利用者を一時停止の持ち主として記録してから。§6 の 15）"` に変える
   - 信号断が止めていて境界ホールドが解除された後、信号が戻ったら再開する（#2 の経路で）ことを確かめるテストを足す（直した後に止まったままにならないこと）
   - ほかの持ち主がいないときは今までどおり解除で再開するテスト（既存にあれば、それが緑のままであること）
   - `SyncRules.ShouldResumeOnBoundaryHoldRelease` の真理値表
   - テストのハーネス（`SyncScenarioHarness`）で `SetEndHold` を MainWindow と同じ条件にする必要があれば、その変更も含める（MainWindow と条件がずれないように、組み立ては 1 か所の関数にして両方から呼ぶ）
6. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま）、
   E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 2g-1 の表の #5 以外の経路（#2・#7・#8・#11 ほか）の振る舞いは変えない
- 終わったら、変えたファイル・テストの件数・赤かったテストが緑になったこと・新しいログを出す条件をこの画面に書く
