# v0.5.3 段 3i: 信号断モードを停止からランスルーに変えたら、信号断の一時停止を解く（§6 の 9）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`07ed334` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 9（停止→ランスルーに変えても、信号が戻るまで止まったまま）と §7（本番中にモードを切り替える運用がある）、
v0.5.2 段 2g の一時停止の持ち主の集合（`docs/design/v0.5.2-pause-owners.md`、`SyncRules.CollectOtherPauseOwners`・`ShouldResumeOnBoundaryHoldRelease`、`MainWindow.ApplyBoundaryHold`）。

## 直す行（段 0 の表）

`PausedByPolicy` × `SignalLossModeChanged` の 1 行（今は「現状 Keeps・意図 Clear」）。ほかの行は、連動して変わるもの以外は変えない。

## やること

1. **先に赤を確かめる**: その行の「現状」を `Clears` に書き換え、根拠を直し、赤を確かめる
2. 直す（親の決め方）:
   - `LtcSignalLossPolicy` に、モードの変更を受ける口を足す（例: `OnSignalLossModeChanged(LtcSignalLossMode newMode)`、戻り値で「信号断の一時停止を解いたか」）。
     **新しいモードがランスルーで、信号断が止めているとき（`_pausedByPolicy`）だけ**、`_pausedByPolicy` と `_manualResumeSuppressesPause` を下ろす。
     損失の印（`_isLost`・理由）は**残す**（ランスルーでは損失中でも同期は止まらない。`ShouldSuppressSync` は損失かつ信号断が止めているときだけ）
   - `LtcSyncController.SignalLossModeChanged()` から呼び、解いたときだけ再開を試みる。**再開はほかの持ち主がいないときだけ**:
     境界ホールド・ギャップ・プロジェクト復元のどれかが止めていれば再開しない（2g の `ApplyBoundaryHold` と同じ考え方。判定は `SyncRules` に純関数で足し、
     持ち主の組み立ては 1 か所の関数にして MainWindow とハーネスの両方から使う）
   - ログを 1 行: 再開したら `LTC signal loss mode changed: policy pause released, playback resumed`、ほかの持ち主で止めたままなら `... policy pause released, playback stays paused owners={Owners}`
   - ランスルー→停止の切り替えでその場で止めるかは**扱わない**（表の意図は未定）
3. **既存のテストの期待値は変えない。** 変えないと通らないものが出たら、変えずに名前と理由をこの画面に書いて止まる
4. テスト: 1 の行が緑。加えて「停止モードで信号断により一時停止中にランスルーへ変えると再生が再開する」「境界ホールドでも止まっているときは再開しない」の 2 件
5. 判定: ビルドの警告 0、非 E2E 全件合格（trx に書き出す）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行（連動した行も）・赤→緑・変えたファイル・テストの件数をこの画面に書く
