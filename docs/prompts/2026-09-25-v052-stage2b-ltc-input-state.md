# v0.5.2 段 2b: 入力（LTC）の軸を LtcInputState にまとめる（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`ebc4ce9` 以降）。**振る舞いは変えない。**
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §2（入力の軸）、§10、「2b の設計判断」。

## 対象のフィールド（すべて `LtcSyncController`）

`_pendingJumpSeconds` / `_pendingJumpReceivedAt` / `_pendingJumpFrameEndTimestamp`、
`_lastAcceptedLtcSeconds` / `_lastAcceptedRawSeconds` / `_lastAcceptedFrameEndTimestamp`、
`_pendingSyncSeconds` / `_pendingSyncRawSeconds` / `_pendingSyncFrameEndTimestamp`、
`_lastAppliedLtcSeconds`、`_lastHeldEffectiveSeconds`、`_heldLossLandingSeconds`、`_jumpAppliedOnce`、`_heldReapplyDone`、`_followStartPending`

## やること

1. 新しいファイル `src/TimecodeSyncPlayer/LtcInputState.cs` に `internal sealed class LtcInputState` を作り、上のフィールドを移す。
   `LtcSyncController` は `private readonly LtcInputState _input = new();` を持つ
2. 3 つ組を record（`readonly record struct` でよい）にする:
   - `PendingJump(double Seconds, long ReceivedAt, long FrameEndTimestamp)?`
   - `AcceptedFrame(double EffectiveSeconds, double RawSeconds, long FrameEndTimestamp)?`（最後に受けたフレーム）
   - `PendingSync(double EffectiveSeconds, double RawSeconds, long FrameEndTimestamp)?`
3. **record にまとめる前に必ず確かめること**: 組の先頭の値（`_pendingJumpSeconds` など）が null のときに、残りの値（Raw・FrameEnd・ReceivedAt）を
   読む箇所が無いか。**1 か所でもあれば、その組は record にしない**（null の後も残りの値が残るのが今の振る舞いなので、まとめると変わる）。
   確かめた結果を組ごとに報告に書く
4. 状態の書き換えは `LtcInputState` のメソッドに集める。名前はできごと・意味で付ける（例: `ClearFrameHistory()`、`DiscardPendingJump()`、
   `OnNormalFrame()`（1 回適用のラッチ 2 つと保持値 2 つを下ろす）、`MarkJumpApplied()` など）。**どの順番で何を書くかは今と同じにする**。
   読み取りはプロパティでよい
5. `LatchSnapshot()` のキーと意味は変えない（中身を `_input` から読むように変えるだけ）
6. ログの文言は 1 文字も変えない
7. 触るのは `LtcSyncController.cs` と新しい `LtcInputState.cs`（とテストの追加）だけ。ほかのクラスは変えない

## テスト

- `LtcInputState` の単体テストを足す（`tests/TimecodeSyncPlayer.Tests/LtcInputStateTests.cs`）: 各メソッドが下ろす／立てるものを 1 件ずつ
- 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま）、
  E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 迷ったら「今の振る舞いを変えない」側を選び、報告に書く
- 終わったら、変えたファイル・3 つ組ごとの確認結果（record にしたか、しなかったならその理由の行）・作ったメソッドの一覧・テストの件数をこの画面に書く
