# v0.5.2 段 2e: 位置の持ち主の軸（読み込みと境界ホールド）を型にする（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`8b91cad` 以降）。**振る舞いは変えない。**
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §2（位置の持ち主の軸）・§4・§10。2b〜2d と同じ進め方。

位置の持ち主の軸は 3 つのクラスにまたがる。**ギャップ（`GapFreezeHandler`）は §4 のとおり触らない。**
残りの 2 つを、それぞれ独立した型にする。2 つを組み合わせた判断は次の段 2f（規則の関数）で扱うので、ここではまとめない。

**親が 2 コミットに分けて取り込めるように、A と B は別のファイルだけを触ること**（A: `TimecodeSyncService.cs` と `FileLoadState.cs`、B: `SingleModeSyncCoordinator.cs` と `BoundaryHoldState.cs`、それぞれのテスト）。

## A. 読み込みの状態 → `FileLoadState`（`TimecodeSyncService`）

対象: `_isLoadingFile`、`_fileLoadReleasePending`、`_fileLoadStartedAt`、`_fileLoadReleasedAt`、`_fileLoadStartPositionSeconds`、`_fileLoadStartedRenderedFrames`
（`_fileLoadEpoch` は単調に増える番号で、`SingleModeSyncCoordinator` が読む。段階とは別に残してよい）

1. `src/TimecodeSyncPlayer/FileLoadState.cs` に `internal sealed class FileLoadState` を作る。段階の候補は「なし／ロード中（開始時刻・開始位置・開始時の描画枚数）／解除の回収待ち（解除時刻）」
2. **先に確かめること**: 段階ごとのデータが、その段階の外で読まれていないか（例: ロード中でないときに開始時刻を読む、回収待ちでないときに解除時刻を読む）。
   また「ロード中」と「回収待ち」が同時に立つ書き込みの順番が無いか。**1 か所でもあれば、その値は段階の外に置く**。結果を値ごとに報告に書く
3. **`IsLoadingFile` は今 `volatile bool` から読まれている**。呼び出し元（`LtcSyncController`・`SingleModeSyncCoordinator` ほか）が UI スレッド以外から読むかを確かめ、
   確かめきれないときは `volatile bool` のまま（段階と一緒に更新する）残す。段階の型（nullable の struct など）を別スレッドから読ませない
4. ロード解除のログ（`Timecode sync: file load released`・`dropping stale file load release`）の文言・場所・順番は変えない
5. 時刻は今と同じく `TimeProvider` から、同じ回数だけ取る

## B. 境界ホールド → `BoundaryHoldState`（`SingleModeSyncCoordinator`）

対象: `_clipBoundaryHeld`、`_boundarySeekTarget`、`_boundarySeekEpoch`

1. `src/TimecodeSyncPlayer/BoundaryHoldState.cs` に `internal sealed class BoundaryHoldState` を作る。端へのシークの記録（目標と読み込み番号）は組の候補
2. **先に確かめること**: `_boundarySeekTarget` が null のときに `_boundarySeekEpoch` を読む箇所があるか。あれば組にしない。結果を報告に書く
3. `SetEndHold`・`OnBoundaryHoldReleased` などの副作用とログは `SingleModeSyncCoordinator` に残す（型は値と遷移だけ）
4. `ReleaseBoundaryHold` から `BoundaryHoldReleased` のできごとが出る流れ（段 1 の追加 `a59a53f`）は変えない

## 共通

- `LatchSnapshot()` のキー（`loadingFile`・`fileLoadReleasePending`・`clipBoundaryHeld`・`boundarySeekTarget` など）と意味は変えない
- ログの文言は 1 文字も変えない
- テスト: `FileLoadStateTests.cs` と `BoundaryHoldStateTests.cs`（各メソッドが変える値・段階の遷移を 1 件ずつ）
- 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま）、
  E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）
- **git の書き込み禁止**。親がレビューして A と B を別々にコミットする
- 迷ったら「今の振る舞いを変えない」側を選び、報告に書く
- 終わったら、A と B それぞれについて、変えたファイル・確認の結果・作ったメソッド・テストの件数をこの画面に書く
