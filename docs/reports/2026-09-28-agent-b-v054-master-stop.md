# 報告: 候補 4（マスター停止の判定の共通化、確認した Jump が保持に入るときの補正）

作業ツリー `timecode-sync-player-v054b`。ブランチ `agent-b-v054-c4` を v0.5.4（21f1ea7）から切った（`git switch -c`）。
Jump の確認から同値の Duplicate を外す変更（agent-b-v054 の 61e595b・c5929e2・c4dbf53・6c97bb8）はこのブランチに入れていない。
agent-b-v054 のコミットは消していない。

## コミット（agent-b-v054-c4）

| コミット | 内容 |
|---|---|
| b4ba104 | 赤いテスト `MasterStoppedDefinitionTests`（agent-b-v054 の c86796a を cherry-pick） |
| 9e3b342 | 同テストに UI タイマーの送り直しの評価を足す（e142066 を cherry-pick） |
| b6a0f54 | (1) 修正と設計書 §10-1 の規則 4 の書き直し（3532ea6 を cherry-pick。衝突なし） |
| 734d243 | (1b) の守りのテスト 3 本（`ConfirmedJumpIntoHoldTests`、今のコードで緑）、先行量のテストの説明をこのブランチの経路に合わせた |

## (1) マスター停止の判定の共通化

変更の中身は agent-b-v054 の報告（`2026-09-28-agent-b-v054-jump-confirm-no-dup.md` の追補）と同じ。

- `SyncRules.IsMasterStopped(heldRunLength, minimumHeldFrames)` を置き、呼ぶのは 3 か所: 先行量 0（`MasterStoppedSource = IsMasterStopped(HeldRunLength, 1) || IsLost`）、
  RunThrough の入口の合わせ（2）、停止モードの U8（2）。
- D27-d の保持値の記録は変えていない。
- 設計書 `docs/design/v0.5.4-gate-unification.md` の §10-1 の規則 4 と §10-2 の表の行を書き直した。
- §10-0-2 の負債: L8 は定義が 1 つの関数になった（層 2 の出力を 1 つの型にする案まではしていない）。L3 はモードの分岐が残るので、枚数の判定が 1 か所になった分だけの一部解消。

このブランチでの経路の違い: 確認の定義は v0.5.4 のまま（同値の Duplicate も確認になる）なので、テストの列（化けた値 8.5 の Jump → 同値の 24fps の Duplicate）
では、2 枚目が保留した Jump の確認になり、確認した Jump の適用（`ApplyConfirmedJump`）がゲートで保留され、UI タイマーの送り直しで relocate する
（agent-b-v054 では保持値の変更の 1 回適用 D20-b の経路だった）。テストの説明の文をこの経路に直した（判定は同じ）。

### テスト

| テスト | 修正前（9e3b342） | 修正後（b6a0f54） |
|---|---|---|
| Fixed30: 化けた 24fps の Duplicate 1 枚の後、先行量は c（0.3）のまま、relocate の目標は 8.5 + c = 8.8 | 赤（先行量 0。先行量の行で止まる） | 緑 |
| 守り: 本物の保持（数える Duplicate 1 枚）で先行量 0（Fixed30） | 緑 | 緑 |
| 守り: 同上（Auto） | 緑 | 緑 |

修正前の赤は、9e3b342 を detached で出して回して確かめた（その後 agent-b-v054-c4 へ戻した）。

## (1b) 確認フレームが Duplicate のときの補正

**コードは変えていない。** 理由:

- `ApplyConfirmedJump` は、確認フレームが Duplicate なら先に保持値の記録（`MarkHeldEffective`）を立ててから `ApplyCorrection` を呼ぶ。
  `ApplyCorrection` は `IsCorrectionHeldOff()`（保持値の記録があるか、一時停止の持ち主がいる）で varispeed を評価せず、`RestoreRateForHold()` で
  掛かっていた倍率を 1.0 に戻して返る。つまり保持に入った確認の適用では、今でも補正（varispeed）はしていない。
- 指示どおり「確認フレームが Duplicate なら `ApplyCorrection` を呼ばない」を一時的に入れて非E2E を回すと、3 本が赤になった
  （`HeldLtcStopAndRunThroughTests.RunThroughMode_HoldEntry_DoesNotEvaluateRateCorrection_AndRestoresUnity`・`StopMode_HoldEntry_DoesNotEvaluateRateCorrection_AndRestoresUnity`
  が「最後の倍率 0.9、期待 1.0」、追加の守りの 1 本が「1.043、期待 1.0」）。呼ばないと、倍率を 1.0 に戻す処理も通らず、保持の間も varispeed の倍率が残る
  （保持の Duplicate の経路は補正を呼ばないため）。一時的な変更はファイルを控えから戻して消した（コミットしていない）。

### テスト（`tests/TimecodeSyncPlayer.Tests/Integration/Scenario/ConfirmedJumpIntoHoldTests.cs`、今のコードで緑）

| テスト | 結果 |
|---|---|
| Fixed30: 追従中に許容の中の値（8.2）へ Jump → 同値の Duplicate で確認 → relocate なし、1.0 以外の倍率を掛けない | 緑 |
| Auto: 同上 | 緑 |
| Fixed30: varispeed（1.043）が掛かった追従中に同じ列 → 最後の倍率が 1.0（戻す） | 緑（(1b) を字義どおり入れると赤） |

依頼の「赤から」は、今のコードが既にその振る舞いなので満たせていない。

## 非E2E

734d243 の時点で合計 2781、成功 2781、失敗 0。既存テストの期待は変えていない（区分 (a)(b)(c) とも 0 件）。

## 未解決の疑問

1. (1) の Auto の赤いテストは書けない。Auto では fps の疑わしさを判定しない（`IsDetectedFpsSuspect` は Auto で false）ので、化けた 24fps の Duplicate 1 枚は
   数える Duplicate 1 枚になり、本物の保持の 1 枚目と区別できない（先行量 0 のまま）。Auto は守りの 1 本だけにした。
2. (1b) で狙った事象（確認した Jump の適用で倍率が変わった）が検証機の証跡のどこにあったかを見ていない。今のコードでは保持に入った確認の適用で varispeed は
   評価されない（上の理由）。証跡に倍率の変化があったなら、別の経路（確認フレームが +1 フレームで Normal の場合など）の可能性がある。
3. 先行量の判定が保持値の記録から数える保持の連続に変わったので、保持の途中に Jump の保留や Reverse が挟まると relocate に c が付く（agent-b-v054 の追補と同じ）。実機では数えていない。
