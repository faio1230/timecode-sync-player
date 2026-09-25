# v0.5.3 段 3c: 境界ホールドのラッチを、読み込み・モード切替・同期の無効化・停止で消す（§6 の 1）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`05b56f4` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 1 と §3 段 3（赤いテストから 1 件 1 コミット、段 0 の表の「現状」をその行だけ書き換える）。

## 直す行（段 0 の表 `LatchLifetimeTable.cs`）

`ClipBoundaryHeld` × `FileLoad` / `SyncModeChanged` / `SyncEnabledOff` / `StopPlayback` の 4 行。今は「現状 Keeps・意図 Clear」。
（`ClipBoundaryHeld` と `BoundarySeekTarget` の `FileLoadWithoutBegin` の行は §6 の 2 のギャップの経路の判断待ちなので**変えない**）

## やること

1. **先に赤を確かめる**: 上の 4 行の「現状」を `Clears` に書き換え、根拠の文を「v0.5.3 段 3c で消すようにした（…）」に直す。今のコードでその 4 行のテストが赤になることを確かめる
2. 直す（親の決め方）:
   - **ラッチだけを消し、一時停止の状態は変えない**（`SetEndHold(false)` は呼ばない。止まっている映像は利用者の再生で動く。勝手に再生を始めない）
   - `FileLoad`: v0.5.1 で `_boundarySeekTarget` を読み込み番号で無効にしたのと同じく、ホールドを立てたときの読み込み番号を覚え、番号が変わったら立っていないとみなす
     （`BoundaryHoldState` に番号を持たせる。`IsBoundaryHeld` と `LatchSnapshot` の `clipBoundaryHeld` も同じ判定にする）
   - `SyncModeChanged` / `SyncDisabled` / `PlaybackStopped`: `SingleModeSyncCoordinator` に `OnLifecycle(SyncLifecycleEvent)` を足し、`LtcSyncController` の該当の入口
     （`SyncModeChanged()`・`SyncEnabledChanged()` の無効化・`PlaybackStopped()`）から呼ぶ。ホールドと端へのシークの記録を消す
   - ラッチを消したときだけログを 1 行: `Single mode: clip boundary hold cleared by {Event}`（既存のログの文言は変えない）
   - `BoundaryHoldReleased` のできごと（`NotifyClipBoundaryHoldReleased`）は**出さない**（それは LTC がクリップに戻ったときの解除。ここはできごとでの破棄）
3. テスト: 1 の 4 行が緑になること。加えて「同期を切った後もホールドの一時停止はそのまま（再生を始めない）」ことを確かめるテストを 1 件
4. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の表は書き換えた 4 行を含めて全緑）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**。親がレビューしてコミットする
- 終わったら、表で書き換えた行・赤→緑になったこと・変えたファイル・テストの件数をこの画面に書く
