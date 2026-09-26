# v0.5.4 K5: 同期の状態の残り（§6 の 1 の読み込みの行・6・7・15）と U-1 の試験の直し（担当 w5:p3）

作業ツリー: `timecode-sync-player-v053`（ブランチ `agent-a-v054`、v0.5.4 に早送り済み）。絶対パスは `docs/local/LOCAL-PATHS.md`（公開しない）。
git への書き込みは禁止（親がコミットする）。**公開文書・コード・テストにローカルの絶対パスを書かない**（手元の pre-commit フックが止める）。

根拠: `docs/design/v0.5.2-sync-state.md` §6 の表（1・6・7・15）、`docs/release-0.5-plan.md` の「潰す一覧」K5 と K7、
`docs/design/v0.5.4-u1-cause.md` §6、`docs/design/v0.5.2-pause-owners.md`（一時停止の持ち主の集合）。

## やること（1 件ずつ、赤いテストを先に書き、修正の前に赤になることを確かめて報告に書く）

1. **§6 の 15（利用者の決定 2026-09-26: 利用者が止めている間は再開しない）**: 一時停止の持ち主の集合に「利用者」を足し、
   境界ホールドの解除・信号断の復帰・ギャップの解除の 3 経路が同じ判定を通るようにする。テストは集合の単体 1 本と、3 経路の配線の確認を 1 本ずつ。
   Skip で置いてある `BoundaryHoldPauseOwnerTests.BoundaryHoldRelease_WhileUserPaused_DoesNotResumePlayback` の Skip を外して緑にする
2. **§6 の 1 の読み込みの行**: Single の境界ホールドのラッチが、読み込みの後に残らないこと（段 3c で直さなかった行）。
   段 3c の教訓: 「無効とみなす」直し方で、解除が持っていた副作用（再開・片付け）を落とさない。既存の S-4 のテストの期待値が変わったら止めて報告
3. **§6 の 6**: Single の読み込みで、一度レート変更を断られた後に Smooth が使えないまま残らない
4. **§6 の 7**: 同期の無効化・一時停止中に、速度を 1.0 に戻す保留が残らない
5. **U-1 の試験の直し（K7）**: `LtcScenarioE2ETests.U1_Continue_HighRateUiaAudit_KeepsSyncAndOutput` が、トラックの間にギャップのあるプロジェクトでは判定せず Invalid（または Skip）にする。
   ギャップの有無はプロジェクトのトラックの隣接で見る（`docs/design/v0.5.4-u1-cause.md` §6 の候補 1b）。ランナーで U-1 をギャップ 0 の周で回す手順も `scripts/run-ltc-scenarios.ps1` の説明に 1 行書く

## 確かめること

- 非E2E（`--filter "FullyQualifiedName!~E2ETests" --logger trx`）全件、E2E（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）全件
- 重い素材でギャップ 0 の周（`-GapSeconds 0`）を 1 回回し、U-1 が合格すること
- 既存のテストの期待値を変えた箇所があれば、1 つずつ理由を書く
