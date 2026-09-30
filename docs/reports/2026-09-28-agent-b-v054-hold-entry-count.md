# 報告: 規則 4 の入口の数え方（連続した同値の 2 枚、fps の疑わしい Duplicate は数えない）

作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v054`。始める前に `git merge --no-ff v0.5.4` をした（f635f53）。

## 事象（親から）

検証機（Continue、L-2、Fixed30）で、LTC の音声が乱れた最中に RunThrough の保持の入口の合わせが古い保持値へ後ろ向きの
シーク（真の位置から −0.37 s）を出し、戻す前向きのシークと合わせて位置が 0.618 s 止まって見えた（試験の上限 0.5 s）。
入口の「Duplicate 2 枚」の 1 枚目は確認した Jump の確認フレーム、2 枚目は化けた最中の fps の食い違う Duplicate で、
2 枚の間に化けた値（Jump の保留）が挟まっていた。

## 修正前の数え方（コードで確かめたこと）

- RunThrough の入口: `holdEntry = LastHeldEffectiveSeconds is not null && HeldLossLandingSeconds is null`。
  `LastHeldEffectiveSeconds` は Duplicate と確認フレーム（`ApplyConfirmedJump` の Duplicate）で立ち、値の進むフレーム
  （`ShouldApplySync`）でしか下りない。Jump の保留・Reverse のフレームが挟まっても残るので、離れた 2 枚が「2 枚目」になった。
- 停止モードの U8: `LtcSignalLossPolicy._consecutiveHeldFrames` は `ObserveHeldFrame` のたびに +1。下ろすのは値の進むフレーム・
  適用した Jump・損失の確定。Jump の保留は下ろさない。fps は見ていない。

## 変更の要約（定数・仕組みは足していない。入口の数え方だけ）

1. `LtcInputState.ObserveHeldRun(countedSeconds, sameValueSeconds)` と `HeldRunLength` を追加。受けたフレームごとに呼び、
   数える Duplicate なら「直前のフレームも数えた Duplicate で同値（半フレーム以内）」のとき連続を 1 伸ばし、そうでなければ 1。
   数えないフレーム（Normal・Jump の保留・Reverse・fps の疑わしい Duplicate）で 0 に戻す。監視の開始・停止
   （`ClearFrameHistory`）でも 0 に戻す。
2. `LtcSyncController.ReceiveProcessedFrame` の先頭（未確認 Jump の確認より前）で数える。数える Duplicate の判定
   `IsCountedHeldFrame` は Duplicate かつ `JumpConfirmationPolicy.IsDetectedFpsSuspect(mode, 検出 fps, 解決 fps)` でないこと
   （元のフレームが無い経路は疑わない）。同値の幅は `IsHeldValueChangedDuringLoss` と同じ半フレーム。
3. RunThrough の入口: `holdEntry` に `heldRun >= 2` を足した。
4. 停止モードの U8: `LtcSignalLossPolicy.ObserveHeldFrame` に省略できる `heldRunLength` を足し、渡されたらそれを
   `_consecutiveHeldFrames` にする。コントローラは Duplicate の経路と確認フレームの経路の両方で渡す。省略時は従来どおり +1
   （ポリシーの単体テストの呼び方はそのまま）。
5. D27-d の保持値の記録（`MarkHeldEffective`）、損失の理由の観測（`_lastHeldFrameAtMilliseconds`）は変えていない。
   fps の疑わしい Duplicate も今どおり保持値として記録し、`ObserveHeldFrame` も呼ぶ（数だけ 0）。
6. テスト用: `SyncScenarioHarness` に `FpsMode`（既定 Fixed25、今までの固定値と同じ）を足した。

## テスト

追加: `tests/TimecodeSyncPlayer.Tests/Integration/Scenario/HoldEntryCountTests.cs`（5 本）。実の受信経路
（`Controller.ReceiveFrame`、fps の解決と診断を通る）に 30fps の timecode を渡す。

今回の列（`GarbledSequence`）: 7.000〜8.000 で追従 → 8.2 の Jump（保留）→ 8.2 の確認フレーム（Duplicate、確認した Jump）→
化けた値 15.0（Jump の保留）→ 8.2（Jump の保留）→ 400ms 抜ける（確認の窓を過ぎる、映像は走る）→ 8.2 の Duplicate。

| テスト | 修正前 | 修正後 |
|---|---|---|
| Fixed30、今回の列、最後の Duplicate は検出 24fps → 入口の合わせ・シークを出さない | 失敗（入口の合わせのシーク 1 回） | 成功 |
| Auto、今回の列、最後の Duplicate は検出 30fps → 同上（2 の条件で弾く） | 失敗（同上） | 成功 |
| Fixed30、連続した同値の 2 枚の 2 枚目が検出 24fps → 数えない。続く疑わしくない連続 2 枚で合わせが出る | 失敗（疑わしい 2 枚目で合わせ） | 成功 |
| 守り: RunThrough の本物の保持（連続した同値の 2 枚）→ 今どおり入口の合わせ（8.0 へ 1 回） | 成功 | 成功 |
| 停止モード、今回の列を最後の進むフレームから 250ms 未満で → U8 の即時の損失確定（一時停止）をしない | 失敗（一時停止） | 成功 |

- 非E2E（`--filter "Category!=E2E"`）: 合計 2775、成功 2775、失敗 0、スキップ 0（修正後の 1 回）。
  ffmpeg を使う 2 本の間欠はこの回では出なかった。

### 既存テストの区分

| 区分 | 件数 | 内容 |
|---|---|---|
| (a) 仕組み | 0 | - |
| (b) 再発 → 新しい状態で緑 | 0 | - |
| (c) 赤のまま調査 | 0 | - |

既存テストの期待は変えていない。

## 未解決の疑問

1. 検証機のログの列そのもの（確認フレーム → 化けた値 → fps の食い違う Duplicate）は手元に無く、テストの列はコードの分岐から
   組んだ。特に、2 枚目の Duplicate が保留中の Jump の確認にならず入口の経路へ来たのは、テストでは確認の窓（100ms）を
   過ぎた形で作った。検証機で同じ理由だったかは確かめていない。
2. 停止モードの U8 は、以前は Reverse・Jump の保留を挟んでも数え続けていた。今は挟まると数え直すので、乱れた保持では
   即時の確定が減り、250ms の確認で止まる形が増え得る（止まるまでの遅れは最大で確認の 250ms）。実機では数えていない。
3. 確認フレームの経路（`ApplyConfirmedJump`）の同期の適用（`RequestSyncEffective`）は、今回の範囲外で変えていない。
   化けた値が確認されてしまう列（同値の 2 枚が窓の中で続く）では、この経路が後ろ向きのシークを出し得る。
