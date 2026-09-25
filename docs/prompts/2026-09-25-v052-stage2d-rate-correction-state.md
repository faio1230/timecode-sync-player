# v0.5.2 段 2d: 速度補正の軸を RateCorrectionState にまとめる（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`316f6e3` 以降）。**振る舞いは変えない。**
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §2（速度補正の軸）と §10。2b・2c と同じ進め方。

## 対象（すべて `LtcSyncController`）

`_lastAppliedRate`、`_rateRestorePending`、`_smoothAvailable`、`_correctionPausedForPosition`、`_correctionResidualGate`、`_correctionRejectedLogged`
（`_correction`（`SyncCorrectionController`）はすでに独立した型なので**移さない**）

## やること

1. 新しいファイル `src/TimecodeSyncPlayer/RateCorrectionState.cs` に `internal sealed class RateCorrectionState` を作り、上のフィールドを移す。
   `LtcSyncController` は `private readonly RateCorrectionState _rate` を持つ
2. **副作用（`_effects.ApplyRateInstant`・`_effects.SeekTo`・`_syncService.ReportSeekSent`・`_effects.SetCorrectionStatus`）とログは `LtcSyncController` に残す**。
   `RateCorrectionState` は値と遷移だけを持つ（例: `MarkRateApplied(rate)`、`MarkRestorePending()`、`MarkRestored()`、`MarkSmoothUnavailable()`、
   `ResetSmoothAvailability()`、`EnterPositionPause()` / `ExitPositionPause()`（戻り値で「今回切り替わったか」を返すと、ログを 1 回にする今の書き方を保てる）など）。
   名前は意味で付けてよい。**どの順番で何を書くかは今と同じにする**
3. 倍率と戻し待ちを 1 つの段階（例: `Unity` / `Applied(rate)` / `RestorePending(rate)`）にまとめてよいか、**先に確かめること**:
   - 「`_rateRestorePending == true` なら `_lastAppliedRate` は 1.0 から 0.0005 以上離れている」が、すべての書き込みの後で成り立つか
     （`ResetCorrection`、`RestoreRateBeforePolicyPause`、`ApplyCorrection` の戻しと SetRate・Seek の分岐、ほか grep で全部）
   - 成り立てば段階にまとめる。**1 か所でも成り立たない書き込みの順番があれば、2 つのフィールドのまま**にする
   - 確かめた結果（書き込みごとに成り立つ理由、または崩れる箇所）を報告に書く
4. `LatchSnapshot()` のキー（`rateRestorePending`・`smoothUnavailable`・`rateNotUnity`・`correctionPausedForPosition`）と意味は変えない。
   `rateNotUnity` の判定幅（0.0005）もそのまま
5. ログの文言は 1 文字も変えない（`Smooth correction rate=` などは E2E と解析スクリプトが読む）。出す場所と順番も変えない
6. 触るのは `LtcSyncController.cs` と新しい `RateCorrectionState.cs`（とテストの追加）だけ

## テスト

- `tests/TimecodeSyncPlayer.Tests/RateCorrectionStateTests.cs`: 各メソッドが変える値を 1 件ずつ（段階にまとめた場合は、段階の遷移も）
- 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま。`T7ContinueCorrectionTests` も）、
  E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 迷ったら「今の振る舞いを変えない」側を選び、報告に書く
- 終わったら、変えたファイル・3 番の確認結果・作ったメソッドの一覧・テストの件数をこの画面に書く
