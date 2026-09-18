# 0.4.5-A §3 設計: 再生位置フィードバックの一貫化

作成: 2026-09-19（同期担当、agent-a）。**この文書は設計のみで、製品・テストのコードは変更していない。**
実装は 0.4.4 が検証機で通ってから（指示書 冒頭）。実機も使っていない（除去担当が M1-b で使用中）。

出典:

- 指示書: `docs/prompts/2026-09-19-V045-A-position-feedback-coherence.md` §3 以降
- §2 の実測: `docs/analysis/2026-09-19-V045A-section2-memo.md`
- 再格付け: `docs/analysis/2026-09-18-LTC-sync-0.4.5-regrade.md`
- コード: `native/gst-shim/src/tcs_gstreamer.cpp`、`src/TimecodeSyncPlayer/`（行番号は agent-a の作成時点）

## 0. 前提（確定した現状モデル）と用語

- 通常再生: クエリ値 = 配信 PTS + **0.4〜7ms**（25fps の 1 フレーム 40ms の 1/6 以下）。
- シーク中: `seek.issue` の約 4ms 後に目標へ跳び、**着地まで目標で凍結**する。アプリには誤差 0 に見え、
  着地で溜まったぶんが現れる（メモ §1-1）。
- **この設計が入力に触るのは着地未確認の区間だけ**。定常は同じ値を使う（メモ §4）。

プロンプト §3-3 の「D37-b2 の抑制」は、コード上に D37-b2 としてある機構とは別物を指している。
取り違えると着地窓を消してしまうため、呼び方をここで固定する:

| この文書の呼び方 | 実装 | 扱い |
| --- | --- | --- |
| D37-b 2-1「着地未確認の間は判定しない」 | `PlaybackPositionTrust`、`SyncDecision.PositionUntrusted`（`SyncDecisionEngine.cs:389-390`）、`WhilePositionUntrusted`（同 69-75） | **置き換え対象**。検証後に外す（§3） |
| D37-b 2-2「定常の実在不足は速度補正」 | rate catch-up（`SyncDecisionEngine.cs:140-151`） | **残す** |
| D37-b2「ギャップ明け・切替の着地はシーク」 | `NotifyLanding` / `_seekLandingAt`（`TimecodeSyncService.cs:93-102`）、`RateCatchUpAllowed`（`SyncDecisionEngine.cs:365-367`） | **残す**（入力の質ではなく手段の選択） |
| `IsNativeSeeking`（配信到着数ベース） | `GstSeekingTracker.IsSeeking`（`GstSeekingTracker.cs:64-88`） | 着地ゲートとして併用（位置の基準切替は世代で行う。§2-2） |

**「着地」の定義**: シーク・ステップ・ロードで shim の `generation` が +1 され（`seek_prepare_locked:2499`）、
その世代のフレームが届くまで配信 PTS は旧世代のまま（`latest_gen = generation` は配信時のみ: `:1452`）。
この設計では **`delivered_generation < current_generation` の間を「着地未確認」** とする。

## 1. shim: 基準・世代・配信 PTS を同じ瞬間に返す

### 1-1. API の形（追加。既存関数は変えない）

`tcs_player_get_time_pos`（`tcs_gstreamer.cpp:3411-3452`）は**挙動不変**。新しい関数を足す。

```c
/* seconds の基準。 */
#define TCS_POSITION_BASIS_NONE      0
#define TCS_POSITION_BASIS_PIPELINE  1  /* gst_element_query_position */
#define TCS_POSITION_BASIS_DELIVERED 2  /* 最新の配信映像フレームの stream-time PTS */

typedef struct TcsPositionSample {
  double   seconds;               /* tcs_player_get_time_pos と同じ規則の値 */
  int32_t  basis;                 /* TCS_POSITION_BASIS_* */
  uint64_t generation;            /* seconds が属する世代 */
  double   delivered_seconds;     /* 最新配信フレームの PTS（無ければ 0） */
  uint64_t delivered_generation;  /* その世代（無ければ 0） */
  uint64_t current_generation;    /* 同時点の player->generation */
} TcsPositionSample;

TCS_GST_API int tcs_player_get_time_pos_ex(TcsPlayer* player, TcsPositionSample* out);
```

- 実装は共通ヘルパ 1 本（`frame_lock` 下で現行の分岐を行い、値を充填）。`tcs_player_get_time_pos` は
  ヘルパの `seconds` だけを使うラッパにし、**戻り値を現状と一致**させる。
- 分岐と basis（意味の追加のみ。値は現行どおり）:
  - paused 経路（`paused && !pump_active && latest_pts_ns > 0 && latest_gen == generation`）:
    seconds = latest PTS、basis = DELIVERED、generation = latest_gen。
  - クエリ成功: seconds = pos、basis = PIPELINE、generation = player->generation。
  - クエリ失敗（`:3434-3437`）: seconds = latest PTS、basis = DELIVERED、generation = latest_gen。TCS_OK。
  - クエリ失敗 + 配信なし: TCS_ERR_NOT_LOADED（現行どおり）。
- `delivered_seconds` / `delivered_generation` は**全経路で充填**する。アプリは再生中でも
  「今画面に出ている絵」の基準を要る（§2-2）。
- 構造体 1 回で返す理由: 3 つの値を 1 回の `frame_lock` で同時に固定できる。別 API を 2 回呼ぶと
  着地をまたいだ不整合が出る。既存関数のシグネチャ・ABI は変えない。

### 1-2. フォールバック経路の trace（必須）

現状 `:3434-3437` は trace を記録せずに return する（メモ §1-2 の留保）。共通ヘルパの
フォールバック分岐で `delivery_append_locked` を 1 行足す。

- flags に **bit 4（16）= position fallback** を追加する（`include/tcs_gstreamer.h:103-107` の
  コメントを更新）。
- 内容: `running_ns` = 返した値（= latest PTS）、`pts_ns` = latest PTS、`seq` = latest_seq、qpc = 現在。
  bit 3（8）の position スナップショットに bit 4 を重ねる形。
- 旧 API・`_ex` のどちらから呼ばれても同じヘルパを通るため、両方で記録される。
- アプリ側 `GstDeliveryTraceMapper`（`GstDeliveryTraceMapper.cs:17-22`）は bit 4 を見て
  **stage `gst.positionFallback`** を出す（bit 3 のみは従来どおり `gst.position`）。
- 数え方（実装後）: `events.jsonl` の `stage=="gst.positionFallback"` を行数カウントする。
  `sync.evaluate` / `player.seeking` と突き合わせれば、どのシーク窓で何回起きたかを直接数えられる
  （§2 memo の消去法は不要になる）。

### 1-3. 世代チェック本体（優先度低。後続でよい）

- アプリは §2-2 で `delivered_generation` を見て採用可否を決める。これが実質のチェック。
- shim 側で `:3434` のフォールバックを「古い世代なら返さない」に変えるのは**別変更**にする。
  旧 `get_time_pos` の戻り値を変えると他呼び出し側（UI・ギャップ凍結）の挙動が変わるため。
  やるなら `_ex` に限定し、古い世代では `basis = NONE / seconds = 0`（`delivered_seconds` は残す）に
  落とす。小さいコミット + shim テストで。

### 1-4. 実装時に触るドキュメント・テスト

- `native/gst-shim/README.md` の位置クエリ節（119-128）に `_ex` と bit 4 を追記。
- `native/gst-shim/test/shim_test.cpp`: (a) 各分岐で basis/gen が付く、(b) クエリ失敗時に bit 4 の
  trace が出る、(c) 旧 API の戻り値が不変。

## 2. アプリ: 着地未確認の間は「配信 PTS + 外挿」で評価する

### 2-1. 読み取り型と経路

- `Contracts/IPlaybackApi.cs` に `bool TryGetPositionSample(out PlaybackPositionSample sample)` を追加
  （`TryGetTimePos` は残す）。
- `PlaybackPositionSample`（Contracts）: `Seconds / Basis / Generation / DeliveredSeconds /
  DeliveredGeneration / CurrentGeneration`。`Basis` は `None/Pipeline/Delivered`。
- `Gst/GstNative.cs` に struct + P/Invoke、`IGstNativeApi` / `GstNativeApi` に `TryGetPositionSample`、
  `GstPlaybackApi` に実装（`_ex` 呼び出し）。
- **DLL 不一致のフォールバック**: `_ex` が `EntryPointNotFoundException` のときはセッション内 1 回だけ
  警告し、以後 `TryGetPositionSample` を false にして旧 `TryGetTimePos` + 旧ガードへ落とす
  （旧 DLL でも起動できる）。
- テストの fake（`FakeGstNative.cs:110`、`FakePlaybackApi.cs:69`）は既定で `Pipeline` のサンプルを返す。

### 2-2. 新規 `PlaybackPositionFeedback`（純ロジック。QPC は注入）

`TimecodeSyncService` が 1 つ所有する（`TimecodeSyncSeekState` と同じ持ち方）。1 評価につき 1 サンプル入力。

- 保持: 直近の delivered（pts / generation / QPC）と実測レート（EMA）。
- 実測レート: delivered が進んだサンプル間の `Δpts / Δqpc` だけで更新する。`Δt ≥ 20ms`、`Δpts > 0`、
  比が 0.25〜4.0 のときだけ採用。EMA はシーク学習と同じ keep 0.7（`TimecodeSyncSeekState.cs:19`）。
  未計測は 1.0。pts が逆行したらリセット。
- 評価位置:
  - `delivered_generation >= current_generation`（着地済み）→ **`Seconds`（クエリ値）**。定常は現行と同値。
  - `delivered_generation < current_generation`（着地未確認）→
    `DeliveredSeconds + min(now − サンプル時刻, 0.5) × レート`。0.5 秒の上限は LTC 側の前例に合わせる
    （`LtcSyncController.cs:58 MaxSampleClockAgeSeconds`）。
  - `delivered_generation == 0`（まだ 1 枚も配信されていない）→ クエリ値 + 旧ガード（サンプル無しと同じ）。
- リセット: `BeginFileLoad`（素材が変わる: `TimecodeSyncService.cs:172`）と `Stop`。
  手動シークは素材が変わらないので保持する（凍結した絵の続きとして外挿する）。
  `ClearSeekState` はクエリ値に戻るだけなのでリセット不要。
- 公開: `EvaluationSeconds` / `EvaluationBasis` / `IsLandingConfirmed`（= 直近サンプルが着地済み。
  §2-3 のシーク抑止に使う）。

外挿の意味は「絵が止まっていれば頭打ち」。配信が来なければ 0.5 秒で伸びが止まり、LTC は進み続けるので
**シーク中に育つ誤差がそのまま見える**（再格付けの訂正どおり）。

### 2-3. 既存のどこを置き換えるか（評価位置の適用箇所）

| 場所 | いま | 変更 | フェーズ |
| --- | --- | --- | --- |
| `TimecodeSyncService.EvaluateDecision`（69-88） | `!_positionTrust.IsTrusted` で `WhilePositionUntrusted`（判定しない） | サンプルがあれば評価位置（§2-2）で判定を続ける。旧ガードはサンプル無しのフォールバックに残す | 2 |
| `SingleModeSyncCoordinator.Apply`（30-87） | `IsNativeSeeking` で即 Deferred（位置を読まない） | 先にサンプルを読む。着地未確認でも評価は続ける。`ShouldSuppressSeek`・`TryMarkFileLoaded`・境界ホールドは**クエリ値のまま** | 1→2 |
| `ContinueOnTrackCoordinator.HandleFrame`（88-147） | 同（92-93） | 同 | 1→2 |
| `TimecodeSyncService.ShouldSuppressSeek` / `TimecodeSyncSeekState` | クエリ値で settle・時間切れ・学習 | **変えない**（§2-4） | - |
| `PlaybackPositionTrust` | 通常経路の判定停止 | フォールバック専用に縮小（フェーズ 3 で通常経路から削除） | 2→3 |
| `LtcSyncController.ApplyCorrection`（717-730） | `HasPendingSeek`・`IsPlaybackPositionUsable` で抑止 | **変えない**（保留中は補正しない。着地後はクエリ値≈配信 PTS なので現状のまま） | - |
| UI・タイムライン・ギャップ凍結の `TryGetTimePos` | - | **触らない**（定常の見た目を変えない） | - |

フェーズ 2 で追加する安全弁: **着地未確認の間に新しいシークを出さない**。
`HasPendingSeek` が時間切れした後でも、世代の合うフレームが届く前に `Seek` を選んだら抑止する
（ログ理由 `unlanded`）。現行はここを D37-b の判定停止で塞いでいる。**連鎖の再発防止の要**なので、
フェーズ 2 の必須項目にする。速度補正は保留中と同じく着地まで出さない（フラッシュ中に
`rate.instant` を重ねない。着地未確認中の速度補正解禁はフェーズ 1 の測定後に判断）。

### 2-4. なぜ「保留・settle・学習」はクエリ値のままか

`TimecodeSyncSeekState.ShouldSuppressSeek` は `playback >= target − tol` で settle する（144-151）。
ここに外挿値を入れると、シーク距離が 0.5 秒以下のとき**着地前に目標を跨いで誤 settle** する。
settle は保留管理とシーク所要の学習（`LearnedSeekDurationSeconds`）を兼ねているため、**入力を変えない**。
着地の真偽は §2-2 の世代で別に持ち、評価とシーク抑止にだけ使う。

## 3. 段階導入: 「判定しない」抑制をいつ外すか

**先に両方を並べて測り、同等以上を示してから外す**（指示書 §3-3、メモ §4-2）。

- **フェーズ 1（shadow）**: `PlaybackPositionFeedback` と `_ex` を入れる。判定・シーク・補正は
  **現行のまま**（D37-b ガードも生かす）。着地未確認の評価位置は `sync.evaluate` に追記フィールドとして
  記録する: `feedbackPlayback=` / `feedbackDelta=` / `feedbackBasis=` / `deliveredGen=` / `currentGen=`。
  `playback=` / `delta=` の既存の意味は変えない。これで 1 本の run に「現行の判断」と「新入力での判断」が
  並ぶ。ネイティブシーク中（`IsNativeSeeking`）も、早期 return の前にこの記録だけは行う。
- **フェーズ 2（delivered）**: 内部スイッチで既定を切替（例 `TCS_SYNC_POSITION_FEEDBACK=delivered`。
  フェーズ 1 の既定は shadow）。通常経路で
  1. 評価位置を `playback` に使う（`sync.evaluate` は `playback=` が評価位置、`rawPlayback=` がクエリ値、
     `basis=` を追加）、
  2. `WhilePositionUntrusted` を通常経路から外す（サンプル無しのときだけ旧ガード）、
  3. §2-3 の着地未確認シーク抑止を入れる。
  残すもの: D37-b2 の着地窓、D37-b 2-2 の速度補正方針、`ShouldSuppressSeek` の入力。
- **フェーズ 3（後片付け）**: shadow の記録経路と `PlaybackPositionTrust` の通常経路を削除する
  （旧 DLL フォールバックに必要な最小分は残す）。

**外す前提としてフェーズ 1 で示すもの**（数字と証跡だけ。合否は親が決める）:

- 着地未確認の窓で `feedbackDelta` が育ち、着地直後の `delta` に連続すること（窓ごとの時系列。
  既存の `TestResults/x1-v045a/` のスクリプトを再利用）。
- 窓内でフィードバックを使った場合にシークが増えないこと（安全弁の効き。ログ `unlanded` の件数）。
- 定常で `feedbackBasis=pipeline` が 100% であること（§4-2）。
- フォールバック回数の実数（§1-2 の bit 4）。

## 4. 定常の非回帰をどう確かめるか

1. **構造**: 定常は `delivered_generation == current_generation` なので評価位置はクエリ値そのもの。
   フェーズ 1 は判断に入らないため挙動は変わらない。フェーズ 2 でも入力が同一のため、差が出るのは
   着地未確認の区間だけ。
2. **trace の不変条件**: `events.jsonl` で「`basis=delivered`（または `feedbackBasis=delivered`）の
   `sync.evaluate` が、`seek.issue` / `gst.generation` / `loadfile` の窓の外に 1 件も無い」ことを数える。
   窓の特定は `player.seeking raw=yes` と `gst.delivery` の世代を使う。
3. **実機（親の合図後）**: V3 を Smooth・LTC25 で 1 本（`scripts/run-v3-accuracy.ps1 -Backends gst`）。
   比較は同じ条件の 0.4.4 の値（例: v0.4.4 の定常 平均 −25.7ms / p95−p5 38.2ms、D37-b2 の
   −27.7 / 40.0ms）に対し、**定常の平均と p95−p5**（黒・freeze は既存どおり集計外）と収束
   （seek-a/b/c/back）。25fps の量子化で p95−p5 の下限は約 40ms であることに注意。
4. **広い回帰**: L-1 ×3（VP9 4K60。補正シーク回数・停止秒・最大誤差）、既存シナリオ 22、
   LTC ループ 14。フェーズ 1 とフェーズ 2 で同じセットを回して比較する。

## 5. 実装時のテスト（追加分）

- shim: `_ex` の basis/gen、フォールバックの bit 4 trace、旧 API 不変。
- C# 単体: 外挿（レート EMA、0.5 秒上限、逆行リセット、未計測 1.0）、着地判定（世代）、
  古い世代の配信 PTS を着地に使わない、サンプル無しの旧ガード、着地未確認でシークを出さない、
  shadow が判断を変えない。
- 既存の非E2E がすべて通ること。

## 6. 親の判断待ち（あれば）

- 着地未確認中の速度補正の扱い（推奨: 着地まで出さない。フラッシュ中の `rate.instant` を避ける）。
- フェーズ 2 で `sync.evaluate` の `playback=` の意味が評価位置に変わる点（`rawPlayback=` を併記して
  旧系列を残す案でよいか）。
- 切替スイッチの既定の運用（コード定数 vs 環境変数。フェーズ 3 で shadow を消す前提）。
- shim 側フォールバックの世代チェック（§1-3）を 0.4.5-A に含めるか、次にするか。

## 7. 触らないもの（指示書 §4 と本設計の追記）

- 許容値（6 フレーム）、Jump しきい値（80ms）、Smooth 制御則、LTC 側（アンカー・時刻付け）、
  表示フレームの選択。
- D37-a のゲート、D37-b2 の着地窓、D37-b 2-2 の速度補正方針。
- `ShouldSuppressSeek` / `TimecodeSyncSeekState` の入力。
- 定常で使う値そのもの（評価位置は着地済みならクエリ値）。
