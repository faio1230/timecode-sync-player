# D26: ジャンプ（シーク・トラック切替）で一瞬黒が挟まる

作成: 2026-09-17 17:00、親。担当: 除去担当（`w5:p6`、作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（`3db2e36` 以降）** を `agent-b` へ通常マージし、shim を自分のツリーでビルドしてから。
発見: 利用者がシナリオ E2E の画面を見て「ジャンプする時に一瞬黒が挟まる」。**製品コードの変更（GPU 合成、`OutputEngine` / `ComposeLayer`）。shim の C ABI は変えない。不変条件 `docs/OUTPUT-GPU-INVARIANTS.md` を守る（リングの面を世代をまたいで参照しないこと）。**

## 1. 原因（親のコード読み）

- `OutputEngine.SyncGStreamerGeneration()`: shim の世代（load / seek で進む）が変わると `layer.ClearHeld()` して古い世代のフレームを返さない。新しい世代の最初のフレームが届くまで（シーク 100〜300ms、ロードはそれ以上）`AcquireGStreamer` は `NotReady`
- `ComposeLayerPolicy.Decide`: gap が None のとき `hasAcquired=false` かつ `hasHeld=false` → **`DrawBlack`**。これが「ジャンプ時の一瞬の黒」
- `ClearHeld` の理由（コメント）: Held がリングの Surface（リーステクスチャ）を参照しており、世代切替後はそのスロットが再利用されるため手放す必要がある

## 2. 直し方（親の案。設計差異は報告に書く）

- **Held を「リングの面への参照」ではなく「合成側が所有する複製」にする**。候補は 2 つ:
  - (A) 毎 tick、合成後のキャンバス（レンダーターゲット）を所有テクスチャへ複製（ping-pong 2 枚）し、`NotReady` かつ gap None のときはその「直前のキャンバス」を描く。ソースの面は参照しないので世代切替で捨てる必要がない。コピーは 1080p60 で毎 tick 8MB、4K60 で 33MB（GPU 内コピー。V6 で影響を測る）
  - (B) 世代切替を観測した時点で、直前に描いたソース画像を所有テクスチャへ複製して Held にする。コピーは切替時だけだが、切替を観測した時点でリングのスロットが既に flush されている可能性があり（shim のシークは合成側が気づく前に走る）、古い絵が取れないことがある → (A) を推奨
- 置き場: `ComposeLayer` の Held を「所有テクスチャ」に統一する（Freeze 用の frozen テクスチャと同じ所有形態）。`PreviewHandoff` の読み戻し用キャンバスと二重に持たないよう整理してよい
- **黒を出すのは gap = Black のときだけ**（`ComposeLayerPolicy` の既存規則どおり）。トラック切替（ロード）中も直前の絵を保持する（Continue の境界通過では新トラックの最初のフレームまで前トラックの絵。利用者の意図「黒を挟まない」に合わせる）。起動直後でまだ 1 枚も描いていないときだけ黒
- D5 再現用の `TCS_TEST_FORCE_GAP_BLACK_ON_SWITCH` の経路は残す（テスト用）

## 3. 検証

- 単体: `ComposeLayerPolicy` / `ComposeLayer` のテスト（世代切替後の NotReady で Held を描く、Black gap は黒、起動直後は黒）
- E2E: `LtcScenarioE2ETests` に**ジャンプ中の黒検出**を足す: C-1（同一トラック内ジャンプ）と C-2（別トラックへジャンプ）で、ジャンプ発行から着地確認までの間に 50ms 間隔で画面を採り、黒率 ≥ 0.99 の枚数を数えて **0 枚**を合格条件に（`LtcScenarioFrameProbe`）。修正前に 1 回回して黒が何枚出るかを記録（修正前失敗の記録）
- 回帰: G-1〜G-6（Black gap は黒のまま）、F-1〜F-5、V5 シーク連打（着地と無公開区間が悪化しない）、V6 の短縮版（10 分、`gpuPublishedFrames` の 2 秒増分と working set。毎 tick コピーの負荷を見る）。実機は一報のうえ親の合図。同期担当と重ねない
- V3 は不要（同期に触れない）

## 4. 報告

コミット（テスト先行 → 実装）、方式 (A)/(B) の選択理由、修正前後の黒枚数、V5/V6 短縮版の数字、証跡パス。**合否は書かない。素材名・絶対パスを書かない。バージョンは上げない。**
