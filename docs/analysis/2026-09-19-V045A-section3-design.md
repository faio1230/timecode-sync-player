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

### 1-3. 世代チェック本体 — 0.4.5-A には入れない（親の判断）

- アプリは §2-2 で `delivered_generation` を見て採用可否を決める。これが実質のチェック。
- shim 側で `:3434` のフォールバックに世代チェックを足すのは**次以降**にする。この経路は
  6 trace・9,644 評価で発火が観測されておらず（メモ §1-2）、**測っていない経路の挙動を変えない**。
  まず §1-2 の trace で実測し、発火が観測されたら直す。
- 入れるときの形（参考）: `_ex` に限定し、古い世代では `basis = NONE / seconds = 0`
  （`delivered_seconds` は残す）。旧 `get_time_pos` の戻り値は変えない（UI・ギャップ凍結を巻き込まない）。

**2026-09-19 追記（フェーズ 1 の実測で前提が変わった。親判断）**: フェーズ 1 で足した
`gst.positionFallback` trace により、この経路は実際には **`seek.issue` の 0.3〜0.6ms 後に発火**
していることを確認した（V3(LTC25) 3 件 / L-1 長 GOP 3 件 / V3(LTC29.97) 2 件。返る値は
旧世代の配信 PTS そのもの）。「発火が観測されていない」という後回しの根拠は消えたため、
**世代チェックの優先度を上げ、0.4.5-C の後に着手する**（親の決定）。

**2026-09-19 実装追記（フェーズ 2 の前提。同期担当）**: 世代チェックを実装した。上の参考形
（`basis = NONE / seconds = 0`）は**採用しない**。

- **方針: 旧世代の PTS しか無いときは `TCS_ERR_NOT_LOADED`（失敗）を返す。**
  - 旧世代の値は現在のシーク／ロードの位置ではないため、返せば偽の位置になる。
  - 旧 `tcs_player_get_time_pos` には basis が無く、`0.0` は有効な素材位置なので
    「不明 = 0」は呼び出し側で実位置と区別できない。`_ex` だけ NONE を返すと 2 つの API で
    成否が食い違う（`GstPlaybackApi.TryGetPositionSample` → 失敗時に旧 `TryGetTimePos` を
    再試行する経路がある）。
  - 失敗は呼び出し側で既に「位置なし」として扱われている: 同期コーディネーターは
    `Blocked("time-pos")` で 1 フレーム保留、UI は null、ギャップ凍結は `hasPosition=false`、
    `TryGetPositionSample` は false。フェーズ 2 が `PlaybackPositionTrust` の抑制を外しても、
    古い値が評価に入らない。
  - `paused_frame_pos` 分岐は元から `latest_gen == generation` を要求している。修正は
    クエリ失敗時のフォールバックだけ。
- **純関数の継ぎ目**: `include/tcs_position_policy.h` の
  `tcs_position_fallback_allowed(latest_gen, generation)`（同一世代のときだけ 1）。
  `shim_test.cpp` の `run_position_policy_tests` で、着地済み → 許可、seek 直後 → 拒否、
  最初の新世代フレーム → 再許可、を固定する。
- **数え方**: trace は残す。受理したフォールバックは従来どおり bit 4 の
  `gst.positionFallback`。**拒否したフォールバックは bit 5（32）を足して
  `gst.positionFallbackRejected`** として出す（`running_ns = 0`、`pts_ns` は旧 PTS）。
  `events.jsonl` の stage 行数で「弾いた回数」を直接数えられる。アプリ側は
  `GstDeliveryTraceMapper` の写像テストで stage を固定する。
- **ABI は変えない**: `TcsStats` / `TcsDeliveryStats` / `TcsDeliveryEvent` のサイズ・
  シグネチャはそのまま（flags の未使用ビットを使う）。旧 DLL との組み合わせでも壊れない。

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
    `DeliveredSeconds + clamp(now − サンプル時刻, 0, 上限) × レート`。
    **上限 = 2 × (1 / videoFps)**（素材のフレーム 2 枚ぶん。60fps なら 33ms、25fps なら 80ms。
    fps は更新時に `state.VideoFps` を渡す。不明はシーク判定と同じ解決済み fps（既定 30）で 67ms）。
    上限では配信 PTS で頭打ちにする。
  - `delivered_generation == 0`（まだ 1 枚も配信されていない）→ クエリ値 + 旧ガード（サンプル無しと同じ）。
- リセット: `BeginFileLoad`（素材が変わる: `TimecodeSyncService.cs:172`）と `Stop`。
  手動シークは素材が変わらないので保持する（凍結した絵の続きとして外挿する）。
  `ClearSeekState` はクエリ値に戻るだけなのでリセット不要。
- 公開: `EvaluationSeconds` / `EvaluationBasis` / `IsLandingConfirmed`（= 直近サンプルが着地済み。
  §2-3 のシーク抑止に使う）。

外挿の意味は「絵が止まっていれば頭打ち」。配信が来なければ上限（フレーム 2 枚ぶん）で伸びが止まり、
LTC は進み続けるので**シーク中に育つ誤差がそのまま見える**（再格付けの訂正どおり）。

**上限を実時間 0.5 秒にしてはいけない（親の訂正、2026-09-19）。** LTC 側の
`MaxSampleClockAgeSeconds = 0.5`（`LtcSyncController.cs:58`）との類推は成り立たない。

- **LTC 側**: 音源は実時間で走り続ける。フレームを取りこぼしても信号は進んでいるので、経過時間 × 1.0 の
  外挿が正しい。
- **映像側**: 配信が止まったら**画面上の映像も進まない**。経過時間 × レートを足すのは「実在しない前進」で、
  育った誤差を外挿そのものが埋めてしまう。4K60 のシーク所要は中央値 555ms・最長 2,038ms なので、
  0.5 秒上限だと**誤差のほぼ全部が隠れ、今回の目的（誤差を見えるようにする）が壊れる**。

外挿の正当な目的は**配信の離散性を埋めることだけ**（60fps なら 16.7ms 間隔）。フレーム 2 枚ぶん来なければ
その絵は実際に止まっている、と線を引く。1 枚は離散性の下限、もう 1 枚は配信サンプルを取ってから次の評価
までの遅れ（最大で LTC 1 フレーム周期 ≈ 40ms）に対する余裕。60fps ではこの古さがフレーム 2.4 枚
（= 40ms）に達しうるが、上限 33ms との差は高々 7ms で、切り捨て側（誤差を少し大きく見せる側）に
倒れるため許容する。2 秒のシーク中は評価位置が「シーク前 PTS + 33〜80ms」に留まり、**誤差 2 秒が
そのまま見える**。

### 2-3. 既存のどこを置き換えるか（フェーズ 2 実装仕様）

**有効化スイッチ**: `TCS_SYNC_POSITION_FEEDBACK=on`（既定 off = フェーズ 1）。off では
この節の変更は全て無効で、フェーズ 1 の挙動のまま。実機で同等以上を確認したら既定を on にする。

#### 2-3-0. 前提と依存関係（2026-09-19 更新）

- **位置フォールバックの世代チェックが必須。実装済み（agent-a `2db35f0`）。**
  フェーズ 1 の trace で、`gst.positionFallback` は実際に `seek.issue` の 0.3〜0.6ms 後に
  発火し、**旧世代の PTS をそのまま返していた**（V3(LTC25) 3 件 / L-1 長 GOP 3 件 /
  V3(LTC29.97) 2 件）。フェーズ 2 は `PlaybackPositionTrust` の抑制を外すため、この経路が
  古い値を返すと**そのまま評価に入る**。世代チェックが無い shim ではフェーズ 2 を有効にしない。
  - 起動時に `_ex` が `EntryPointNotFoundException`（旧 DLL）のときは従来どおり 1 回警告し、
    **フェーズ 2 も自動で無効化**する（サンプル無し経路へ）。`GstPlaybackApi._timePosExUnavailable`
    と同じフラグに乗せる。
  - 世代チェック後の契約: 旧世代しか無ければ `_ex` は失敗（`TCS_ERR_NOT_LOADED`）し、
    サンプル無し経路（旧ガード）に落ちる。
- **D37-d が入った**（着地窓は到達まで + 前進ガード + 0.5× + 上限）。役割境界は §2-3-5。
- **実測したシーク中の誤差の育ち方**（フェーズ 1 trace）: 393 → 547 → 751ms、着地で 446ms。
  フェーズ 2 の効果はこの区間（着地未確認の窓）で測る（§4）。

#### 2-3-1. 判定位置と trace の値（契約を守る）

| 値 | 変数 | フェーズ 2 の扱い |
| --- | --- | --- |
| クエリ値 | `state.PlaybackSeconds` | そのまま（`playback=` / `delta=` / `ShouldSuppressSeek` / `TryMarkFileLoaded` / 境界ホールド / UI） |
| 評価位置 | `state.EvalPositionSeconds`（`WithShadow` が設定） | エンジンの**判断だけ**に使う |
| 判断用 Δ | `target − 評価位置` | `SeekDecisionGate`・tolerance・速度補正/シーク分岐 |
| 表示用 Δ | `target − クエリ値` | `delta=`（既存の意味を変えない。§3-1） |

- `SyncDecisionEngine.Decide` の算術（tolerance・rate catch-up・gate・seek 判定）を
  `state.EvalPositionSeconds ?? state.PlaybackSeconds` に切り替える。**ログは変えない**:
  `RecordEvaluate` は従来どおり `state.PlaybackSeconds` から `playback=` / `delta=` を作り、
  `evalPosition=` / `evalDelta=` を併記する。
- `SyncDecision` に `QueryDeltaSeconds` を追加し、`LogDecisionIfNeeded` の `delta=` は
  これを使う（判断用 `DeltaSeconds` と区別する）。off では両者同値なので出力は不変。
- `seek.decide` の `playback=` も同じ規則（クエリ値）のまま。

#### 2-3-2. `TimecodeSyncService.EvaluateDecision`（実装仕様）

phase2 = スイッチ on かつ `_ex` が使える、とする。

1. `WithShadow` で評価位置を作る（フェーズ 1 と同じ）。
2. 旧ガード `!_positionTrust.IsTrusted` は**サンプルが無いときだけ**適用する。
   - phase2 && サンプルあり → ガードを通さず評価を続ける。
   - phase2 && サンプルなし → 従来どおり `WhilePositionUntrusted`。
   - phase2 off → 従来どおり。
3. D37-d の前進ガードと着地窓は現行のまま（評価位置ベースになる。着地後は評価位置 =
   クエリ値なので、前進ガードの pre/post 比較の意味も現行と一致する）。
4. `Decide` が `Seek` を返し、かつ**着地未確認**（`reading.LandingConfirmed == false`）なら
   `GateDeferred = true` の None に置き換えて**新しいシークを出さない**（ログ理由 `unlanded`。
   同じエピソードで 1 回だけ出す）。到達判定（`WithinTolerance` → 窓を閉じる）は置き換えの
   前に済ませる。
5. trace は §2-3-1 のとおり（既存フィールド + eval フィールド）。

`LandingConfirmed` の定義: `delivered_generation >= current_generation`。
`delivered_generation == 0`（1 枚も配信されていない）も false。

#### 2-3-3. コーディネーター（Continue / Single）

- `ReadSyncTimePos` が既に 1 回の `_ex` から値とサンプルの両方を作る。先にサンプルを読む。
- サンプルがあれば `IsNativeSeeking` でも早期 return しない（フェーズ 1 の shadow 記録は
  残し、判断に使わないのは off のときだけ）。`GetTimePos` のクエリ値は
  `TryMarkFileLoaded`・`ShouldSuppressSeek`・`BuildPlaybackState` にそのまま使う。
- `SingleModeSyncCoordinator.Apply`（30-87）も同じ: `IsNativeSeeking` で即 Deferred にせず、
  サンプルありなら評価を続ける。
- `ShouldSuppressSeek` / `TimecodeSyncSeekState` / 境界ホールドは**変えない**（§2-4）。

#### 2-3-4. 表（置き換え箇所の一覧）

| 場所 | いま | 変更 | フェーズ |
| --- | --- | --- | --- |
| `TimecodeSyncService.EvaluateDecision` | `!_positionTrust.IsTrusted` で `WhilePositionUntrusted` | サンプルありは評価を続ける。サンプル無しだけ旧ガード。`unlanded` の安全弁を追加 | 2 |
| `TimecodeSyncService.ShouldSuppressSeek` / `TimecodeSyncSeekState` | クエリ値で settle・時間切れ・学習 | **変えない** | - |
| `SyncDecisionEngine.Decide` の算術 | `state.PlaybackSeconds` | `EvalPositionSeconds ?? PlaybackSeconds`。ログは不変 | 2 |
| `SingleModeSyncCoordinator.Apply` | `IsNativeSeeking` で即 Deferred | サンプルありなら評価を続ける。クエリ値の用途は不変 | 2 |
| `ContinueOnTrackCoordinator.HandleFrame` | 同 | 同 | 2 |
| `PlaybackPositionTrust` | 通常経路の判定停止 | サンプル無しのフォールバック専用に縮小（フェーズ 3 で削除） | 2→3 |
| `LtcSyncController.ApplyCorrection` | `HasPendingSeek`・`IsPlaybackPositionUsable` で抑止 | 着地まで適用しない（現行どおり）。shadow は継続（下記） | 1→2 |
| UI・タイムライン・ギャップ凍結の `TryGetTimePos` | - | **触らない**（定常の見た目を変えない） | - |

#### 2-3-5. D37-d の着地窓との役割境界（重要）

| 機構 | 見るもの | 働くタイミング | フェーズ 2 での関係 |
| --- | --- | --- | --- |
| 着地未確認シーク抑止（新、`unlanded`） | 配信世代 < 現在世代 | シーク中・ロード中（着地するまで） | **上流**。unlanded の間は Seek を出さない |
| D37-d 着地窓 | 着地後に誤差が許容へ入るまで | **着地後**（世代が一致してから） | **下流**。着地後のシーク選択を決める |
| D37-b `PlaybackPositionTrust` | クエリ値の保留・settle | サンプル無しのときだけ | サンプルあり経路では判定に使わない |

- 順序は「シーク → unlanded（全 Seek 抑止）→ 着地確定 → D37-d の窓が次のシークを選ぶ」。
  両者が同時に Seek を判断する状態は無い（unlanded の間は窓の判断に到達しない）。
- D37-d の上限（5 秒 / 連続 3 シーク）は着地済みの窓だけを数える。unlanded の時間は窓の
  年齢には入るが、`ReportSeekSent` が呼ばれないのでシーク回数は増えない。
- 前進ガードの `pre` / `post` は**評価位置**で比較する（着地後は評価位置 = クエリ値なので
  意味は現行と同じ。unlanded 中は `ReportSeekSent` が起きないため比較も発生しない）。

着地未確認中の速度補正（親の判断）: **実際には出さない。** フラッシュ中は効かないうえ副作用が
読めないため。ただし「出したとしたら」の値を `sync.evaluate` に追記する（`shadowRate=` /
`shadowRateReason=`）。後から「シーク中にも出した方が良かったか」を測定で判断できるようにする。

- 残差は評価位置から作り、`SyncCorrectionController` と同じ式（`RateFor` とデッドバンド・
  戻りバンド・着地窓）で計算する。**`Evaluate` は `_rateActive` / `_smoothDisabled` 等の状態を
  変えるため shadow では呼ばない**（純関数の shadow ヘルパを追加する）。Jump モードはレートを
  出さないので記録対象外（`shadowRateReason=not-smooth` の 1 語だけ残す）。
- 適用はフェーズ 1・2 ともに行わない。解禁の是非はこのデータを見て 0.4.6 以降に判断する。
- 現在位置: `ApplyCorrection` は `HasPendingSeek` / `!IsPlaybackPositionUsable`（726-730）で
  残差の計算前に return する。shadow はその分岐で、`TimecodeSyncService` が公開する直近の評価位置から
  残差を作る（Continue は素材位置 − 評価位置、Single は LTC − 評価位置）。

### 2-4. なぜ「保留・settle・学習」はクエリ値のままか（変更なし）

`TimecodeSyncSeekState.ShouldSuppressSeek` は `playback >= target − tol` で settle し、保留管理と
シーク所要の学習（`LearnedSeekDurationSeconds`）を兼ねる（144-151）。**この入力はクエリ値のまま
変えない。**

- 現行の settle は `IsNativeSeeking`（配信到着数）でゲートされた「クエリ値が目標に跳んだか」で決まり、
  **D37-b 以降で校正した「着地までの実測時間」がこの系列に乗っている**。外挿値に替えると学習値の意味が
  変わり、D37-b2 の速度補正／シーク分岐（実測所要との比較）に波及する。
- 上限をフレーム 2 枚に下げたことで「外挿が先に tolerance 圏へ届く」誤 settle の危険はほぼ消える
  （tolerance は 6 フレーム > 上限 2 フレーム。そもそも `|delta| > tolerance` のシークしか発行されない）
  が、入力の意味を変えない方針自体は維持する。

着地の真偽は §2-2 の世代で別に持ち、評価とシーク抑止（§2-3）にだけ使う。

## 3. 段階導入: 「判定しない」抑制をいつ外すか

**先に両方を並べて測り、同等以上を示してから外す**（指示書 §3-3、メモ §4-2）。

- **フェーズ 1（shadow）**: `PlaybackPositionFeedback` と `_ex` を入れる。判定・シーク・補正の適用は
  **現行のまま**（D37-b ガードも生かす）。着地未確認の評価位置と「出したとしたら」の補正レートは
  `sync.evaluate` に**追記フィールド**として記録する: `evalPosition=` / `evalDelta=` / `evalBasis=` /
  `shadowRate=` / `shadowRateReason=` / `deliveredGen=` / `currentGen=`。
  `playback=` / `delta=` の既存の意味は変えない（§3-1）。これで 1 本の run に「現行の判断」と
  「新入力での判断」が並ぶ。ネイティブシーク中（`IsNativeSeeking`）も、早期 return の前にこの記録
  だけは行う。記録は出力トレース有効時のみ（無効時は先頭で即 return。既存と同じ）。
- **フェーズ 2（delivered）**: 環境変数 `TCS_SYNC_POSITION_FEEDBACK` で切替（既定 `off` = フェーズ 1、
  `on` = フェーズ 2。実機で同等以上を確認したら既定を `on` にする。前例:
  `TCS_LTC_SAMPLE_CLOCK` / `TCS_SEEK_LATENCY_COMPENSATION` / `TCS_PUMP_BUDGET_MS`。
  検証機がビルドし直さずに A/B できる）。実装仕様は §2-3。
  1. 判定に評価位置を使う（`sync.evaluate` は `playback=` / `delta=` のまま + `evalPosition=` /
     `evalDelta=` を併記。`SyncDecision.QueryDeltaSeconds` を追加してログの意味を守る）、
  2. `WhilePositionUntrusted` を通常経路から外す（サンプル無しのときだけ旧ガード）、
  3. 着地未確認のシーク抑止（`unlanded`）を入れる。
  残すもの: D37-b2 の着地窓、D37-b 2-2 の速度補正方針、`ShouldSuppressSeek` の入力、
  速度補正を出さない方針（値だけ shadow で残す）。
  **前提: 位置フォールバックの世代チェック（§2-3-0、実装済み `2db35f0`）。旧 DLL や世代チェック
  無しの shim ではフェーズ 2 を有効にしない。**
- **フェーズ 3（後片付け）**: shadow の記録経路と `PlaybackPositionTrust` の通常経路を削除する
  （旧 DLL フォールバックに必要な最小分は残す）。**切替スイッチは 0.4.5 では消さない**
  （先行補償の前例に合わせ、0.4.6 で判断する）。

### 3-1. trace フィールドの契約（親の判断。最も取り違えやすい点）

**既存フィールドの意味を変えない。** `playback=` は今までどおり「クエリ値」、`delta=` は
「LTC（clamp 後）− `playback=`」のまま。評価に使う位置は**新しい名前**で足す:

| フィールド | 意味 | フェーズ 1 | フェーズ 2 |
| --- | --- | --- | --- |
| `playback=` | クエリ値（`tcs_player_get_time_pos` / `_ex` の `seconds`） | 従来どおり | **従来どおり（変えない）** |
| `delta=` | `target − playback=` | 従来どおり | **従来どおり（変えない）** |
| `evalPosition=` | フィードバック位置（着地済みはクエリ値、着地未確認は配信 PTS + 外挿） | 追記 | 判定に使用 |
| `evalDelta=` | `target − evalPosition=` | 追記 | 判定に使用 |
| `evalBasis=` | `pipeline` / `delivered` | 追記 | 追記 |
| `shadowRate=` / `shadowRateReason=` | 着地未確認中に「出したとしたら」の Smooth レート | 追記 | 追記（適用はしない） |

理由: フィールド名は過去のトレースとの契約であり、**意味を黙って変えると過去 run との比較が全部壊れる**。
今日それに類する取り違えを 3 回踏んでいる（監査区間の基準ずれ、25fps で測った M1、ロング GOP でない素材）。
意味を変えるより名前を増やす方が常に安い（親の判断）。`seek.decide` の `playback=` も同じ規則にする。

**外す前提としてフェーズ 1 で示すもの**（数字と証跡だけ。合否は親が決める）:

- **着地未確認の窓で `evalDelta` が育ち、着地直後の `delta=`（クエリ値基準）に連続すること。**
  フェーズ 1 の実測ではシーク中に **393 → 547 → 751ms**、着地で **446ms**（推定される素の誤差の
  育ち方がそのまま見えている）。窓ごとの時系列で、着地の前後が不連続に跳ばないことを確認する
  （既存の `TestResults/x1-v045a/` のスクリプトを再利用）。
- 定常で `evalBasis=pipeline` が 100% であること（§4-2）。
- 着地未確認中の `shadowRate`（出さなかったレート）が、着地後の実補正と比べてどうだったか
  （0.4.6 以降で解禁を判断する材料）。
- フォールバックの実数: 受理（bit 4 の `gst.positionFallback`）と、**旧世代で弾いた回
  （bit 5 の `gst.positionFallbackRejected`）**。フェーズ 1 の 8 件はすべて後者に移る見込み。
- フェーズ 2 固有の安全弁（`unlanded` の抑止回数）はフェーズ 2 の run で数える（§4）。

## 4. フェーズ 2 の効果と非回帰をどう確かめるか

**フェーズ 2 の効果を p95−p5 で測ってはいけない（親の訂正、2026-09-19）。**
p95−p5 には LTC fps × 映像 fps の比で決まる**量子化の床**が含まれる（M5 の確定版:
1:1 なら厳密に 0、25×30 / 25×60 なら **26.67ms**、30×60 なら 16.67ms）。
現行 V3（LTC25 × 60fps）の 38〜40ms はこの床を含んでおり、**床より下には行けない**。
定常の p95−p5 は「悪化していないこと」の sanity check に留める。

1. **計測の主軸: 着地未確認の窓の可視化。** `events.jsonl` の `sync.evaluate` から、
   `player.seeking raw=yes`（および `gst.generation`）で囲まれた窓を切り出し、
   - `evalDelta` がシーク中に**単調に育つ**こと（実測例: 393 → 547 → 751ms）、
   - 着地の前後で `evalDelta` → `delta=`（クエリ値基準）が**不連続に跳ばない**こと
     （着地時の実測 446ms が両系列で連続する）、
   - 窓の中で新しいシークが出ていないこと（`seek.issue` が窓内に 1 件も無い。`unlanded` の
     抑止ログと対で数える）、
   - 窓の長さ（シーク所要）と窓内の最大 `evalDelta` の関係（シーク所要がそのまま見えること）。
   フェーズ 1 とフェーズ 2 で**同じ素材・同じテスト**の run を並べ、この 4 点を比較する。
2. **構造**: 定常は `delivered_generation == current_generation` なので評価位置はクエリ値そのもの。
   フェーズ 2 でも入力が同一のため、差が出るのは着地未確認の区間だけ。
3. **trace の不変条件**: `events.jsonl` で「`evalBasis=delivered` の `sync.evaluate` が、
   `seek.issue` / `gst.generation` / `loadfile` の窓の外に 1 件も無い」ことを数える。
   窓の特定は `player.seeking raw=yes` と `gst.delivery` の世代を使う。
4. **定常の sanity（悪化していないこと）**: V3 を Smooth・LTC25 で 1 本。見るのは定常の平均
   と p95−p5（床を含む値。黒・freeze は既存どおり集計外）と収束（seek-a/b/c/back）。
   比較対象は同じ条件の 0.4.4 の値（例: 定常 平均 −25.7ms / p95−p5 38.2ms、D37-b2 で −27.7 / 40.0ms）。
5. **広い回帰**: L-1 ×3（VP9 4K60。補正シーク回数・停止秒・最大誤差）、既存シナリオ 22、
   LTC ループ 14。フェーズ 1 とフェーズ 2 で同じセットを回して比較する。

## 5. 実装時のテスト（追加分）

- shim: `_ex` の basis/gen、フォールバックの bit 4 trace、旧 API 不変。
  **世代チェック（実装済み `2db35f0`）**: `tcs_position_policy.h` の純関数（着地→許可、
  seek 直後→拒否、新世代→再許可）と、弾いた回の bit 5 trace。
- C# 単体: 外挿（レート EMA、上限 2 フレーム、逆行リセット、未計測 1.0）、着地判定（世代）、
  古い世代の配信 PTS を着地に使わない、サンプル無しの旧ガード、着地未確認でシークを出さない、
  shadow が判断を変えない、`shadowRate` の計算が補正の状態（`SyncCorrectionController` の
  `_rateActive` / `_smoothDisabled`）を変えない。
- フェーズ 2 の追加単体（実装時）: スイッチ off がフェーズ 1 と 1 ビットも変わらないこと、
  `unlanded` の抑止が `Seek` を `GateDeferred` に置き換えても到達判定（`WithinTolerance`）を
  落とさないこと、`LogDecisionIfNeeded` の `delta=` が `QueryDeltaSeconds` を出すこと、
  D37-d の窓が unlanded 中にシーク回数を増やさないこと。
- 既存の非E2E がすべて通ること。

## 6. 親の判断（2026-09-19 に確定）

1. **着地未確認中の速度補正**: 出さない。`shadowRate=` / `shadowRateReason=` で「出したとしたら」だけ
   残し、解禁の是非は測定で判断する（0.4.6 以降）。
2. **`sync.evaluate` の `playback=`**: 意味を変えない。評価位置は `evalPosition=` / `evalDelta=` /
   `evalBasis=` を追加する（§3-1。過去トレースとの比較を壊さないため）。
3. **切替スイッチ**: 環境変数 `TCS_SYNC_POSITION_FEEDBACK`。shadow の間は既定 `off`、実機で同等以上を
   確認したら既定 `on`。検証機がビルドし直さずに A/B できる。**スイッチ自体は 0.4.5 では消さず、
   0.4.6 で判断する**（先行補償の前例）。
4. **shim 側フォールバックの世代チェック**: フェーズ 1 で発火が確認されたため（`seek.issue` の
   0.3〜0.6ms 後に 8 件）、**フェーズ 2 の前提として実装済み**（agent-a `2db35f0`、§1-3 の実装追記）。
   旧世代は `TCS_ERR_NOT_LOADED`、弾いた回は bit 5 の `gst.positionFallbackRejected`。

## 7. 触らないもの（指示書 §4 と本設計の追記）

- 許容値（6 フレーム）、Jump しきい値（80ms）、Smooth 制御則、LTC 側（アンカー・時刻付け）、
  表示フレームの選択。
- D37-a のゲート、D37-b2 の着地窓、D37-b 2-2 の速度補正方針。
- `ShouldSuppressSeek` / `TimecodeSyncSeekState` の入力。
- 定常で使う値そのもの（評価位置は着地済みならクエリ値）。
- 既存の trace フィールド（`playback=` / `delta=`）の意味（§3-1）。
