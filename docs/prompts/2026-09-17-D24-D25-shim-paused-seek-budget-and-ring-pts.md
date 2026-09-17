# D24: 一時停止シークのポンプ予算 500ms を超える復号距離で位置が動かない / D25: シーク直後のリースが PTS は目標・画素はシーク前（shim）

作成: 2026-09-17 16:10、親。担当: 同期担当（`w5:p3`。コンテキストが多ければ `/new` してから）。作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`。
基点: **main の最新（D20-b・D21-b の統合後）** を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
背景: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` 末尾の D24・D25。**shim の変更。C ABI は変えない。不変条件 I1〜I13・H-3 を守る。**

## 1. D24（再現済み、除去担当の切り分け）

- 事実: 一時停止中の accurate シークは、直前のキーフレームから目標まで復号する。`kPumpBudgetMs = 500` を超えると `paused-seek: pump deadline gen=N -> PAUSED (faults=N)` で一時停止に戻り、**フレームが来ないので位置が 0 のまま**。resume すると 3〜5ms で届く（復号は進んでいた）
- 再現条件: GOP 600（キーフレーム 10 秒間隔）で 1080p60 / 1920x886 とも再現。GOP 250 は 220〜305ms で到着（予算内）。高さ 886 は無関係。素材と shim ログ: agent-b の作業ツリー `artifacts/d24-media/*.mp4`、`*.shim.log`。テストツール `tcs-shim-test --paused-seek` は目標位置を argv[3] で受ける（`e3a7725`、main 統合済み）
- 現場の意味: yt-dlp 由来や配信向けの長 GOP 素材で、プロジェクト読み込み直後の先頭フレーム表示や Freeze の最終フレーム取得が失敗する（検証機の M2）

### 直し方（親の案。計測で決める）

- ポンプの期限を固定 500ms ではなく **「最初のフレーム到着まで、上限 N 秒」** にする（N の既定は 4000ms、環境変数 `TCS_PUMP_BUDGET_MS` で上書き可）。accurate シークではデコーダが目標前のフレームを出さないので、PLAYING を延ばしても目標前の絵は出ない（ことをログで確認する）。音声は既存の priming/mute のまま
- 期限超過時は現状どおり PAUSED に戻すが、`set_error` ではなく警告ログに復号距離の目安（目標 − 直前キーフレーム、分かれば）を出す
- 副作用の確認: 一時停止中のシークを連打したとき（V5 相当）に pump が重ならないこと（既存の gen/faults の仕組みで打ち切られること）

### 検証

- `tcs-shim-test --paused-seek` で `artifacts/d24-media` の 7 本（g30 / g250 / g600 × 886 / 1080、hi）を目標 19.968 で: g600 が到着し、他が悪化しない（到着 ms を表に）
- 実素材 10 本の shim テスト、`check-shim-lock-rule.py` PASS、E2E 一部（GStreamerBackend / SystemScenario）、V5 を 1 回（実機は一報のうえ合図）

## 2. D25（未解明、調査 → 修正）

- 事実: ギャップ進入の accurate シーク（目標 19.967）の 6.3ms 後の `compose.acquire` は `ptsNs=19966666666`（目標）で Ready だが、その絵はシーク前の B 本文。165ms 後の再取得（seq=20）も同じ PTS・同じ絵。証跡 `TestResults/d20b-trace-f4/events.jsonl`、`TestResults/ltc-scenarios/d20b-f4-single/`（agent-a の作業ツリー）
- 調べること: (1) shim がスロットの Info（PTS・sequence）を書くタイミングと、テクスチャの `CopySubresourceRegion` → fence signal の順序。消費側（`GStreamerSource` / `OutputEngine`）が fence を待ってから読んでいるか、fence 値の対応（スロットごと / epoch）がずれ得るか。(2) フラッシュシーク後、古いスロットの内容が残ったまま新しい Info で公開される経路（世代切替、`ring: destroyed/created`、D8 の epoch）。(3) `tcs-shim-test` で「シーク直後の最初のリースの画素が目標フレームか」を色素材（`artifacts/media` の A/B/C は秒ごとに色が違う）で機械判定するテストを追加して再現させる
- 直し方は原因次第。**不変条件の文書（`docs/OUTPUT-GPU-INVARIANTS.md`）に反していればそれを直し、反していなければ不変条件に足りない条項を追加**して親に報告

## 3. 報告

D24 と D25 を別コミットで。到着 ms の表、shim ログの該当行、単体/shim テスト、E2E、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。バージョンは上げない。**
