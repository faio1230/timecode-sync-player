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
| `LtcSyncController.ApplyCorrection`（717-730） | `HasPendingSeek`・`IsPlaybackPositionUsable` で抑止 | 着地まで**適用しない**（現行どおり）。着地未確認の抑止分岐で「出したとしたら」の Smooth レートだけ trace に記録（下記） | 1→2 |
| UI・タイムライン・ギャップ凍結の `TryGetTimePos` | - | **触らない**（定常の見た目を変えない） | - |

フェーズ 2 で追加する安全弁: **着地未確認の間に新しいシークを出さない**。
`HasPendingSeek` が時間切れした後でも、世代の合うフレームが届く前に `Seek` を選んだら抑止する
（ログ理由 `unlanded`）。現行はここを D37-b の判定停止で塞いでいる。**連鎖の再発防止の要**なので、
フェーズ 2 の必須項目にする。

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

### 2-4. なぜ「保留・settle・学習」はクエリ値のままか

`TimecodeSyncSeekState.ShouldSuppressSeek` は `playback >= target − tol` で settle する（144-151）。
ここに外挿値を入れると、シーク距離が 0.5 秒以下のとき**着地前に目標を跨いで誤 settle** する。
settle は保留管理とシーク所要の学習（`LearnedSeekDurationSeconds`）を兼ねているため、**入力を変えない**。
着地の真偽は §2-2 の世代で別に持ち、評価とシーク抑止にだけ使う。

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
  検証機がビルドし直さずに A/B できる）。通常経路で
  1. 判定に評価位置を使う（`sync.evaluate` は `playback=` のまま + `evalPosition=` を併記）、
  2. `WhilePositionUntrusted` を通常経路から外す（サンプル無しのときだけ旧ガード）、
  3. §2-3 の着地未確認シーク抑止を入れる。
  残すもの: D37-b2 の着地窓、D37-b 2-2 の速度補正方針、`ShouldSuppressSeek` の入力、
  速度補正を出さない方針（値だけ shadow で残す）。
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

- 着地未確認の窓で `evalDelta` が育ち、着地直後の `delta`（クエリ値基準）に連続すること
  （窓ごとの時系列。既存の `TestResults/x1-v045a/` のスクリプトを再利用）。
- 窓内でフィードバックを使った場合にシークが増えないこと（安全弁の効き。ログ `unlanded` の件数）。
- 定常で `evalBasis=pipeline` が 100% であること（§4-2）。
- 着地未確認中の `shadowRate`（出さなかったレート）が、着地後の実補正と比べてどうだったか
  （0.4.6 以降で解禁を判断する材料）。
- フォールバック回数の実数（§1-2 の bit 4）。

## 4. 定常の非回帰をどう確かめるか

1. **構造**: 定常は `delivered_generation == current_generation` なので評価位置はクエリ値そのもの。
   フェーズ 1 は判断に入らないため挙動は変わらない。フェーズ 2 でも入力が同一のため、差が出るのは
   着地未確認の区間だけ。
2. **trace の不変条件**: `events.jsonl` で「`evalBasis=delivered` の `sync.evaluate` が、
   `seek.issue` / `gst.generation` / `loadfile` の窓の外に 1 件も無い」ことを数える。
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
  shadow が判断を変えない、`shadowRate` の計算が補正の状態（`SyncCorrectionController` の
  `_rateActive` / `_smoothDisabled`）を変えない。
- 既存の非E2E がすべて通ること。

## 6. 親の判断（2026-09-19 に確定）

1. **着地未確認中の速度補正**: 出さない。`shadowRate=` / `shadowRateReason=` で「出したとしたら」だけ
   残し、解禁の是非は測定で判断する（0.4.6 以降）。
2. **`sync.evaluate` の `playback=`**: 意味を変えない。評価位置は `evalPosition=` / `evalDelta=` /
   `evalBasis=` を追加する（§3-1。過去トレースとの比較を壊さないため）。
3. **切替スイッチ**: 環境変数 `TCS_SYNC_POSITION_FEEDBACK`。shadow の間は既定 `off`、実機で同等以上を
   確認したら既定 `on`。検証機がビルドし直さずに A/B できる。**スイッチ自体は 0.4.5 では消さず、
   0.4.6 で判断する**（先行補償の前例）。
4. **shim 側フォールバックの世代チェック**: 0.4.5-A に含めない（§1-3）。`gst.positionFallback` の
   trace は必須で入れる（§1-2）。未発火の経路の挙動は変えず、まず測る。

## 7. 触らないもの（指示書 §4 と本設計の追記）

- 許容値（6 フレーム）、Jump しきい値（80ms）、Smooth 制御則、LTC 側（アンカー・時刻付け）、
  表示フレームの選択。
- D37-a のゲート、D37-b2 の着地窓、D37-b 2-2 の速度補正方針。
- `ShouldSuppressSeek` / `TimecodeSyncSeekState` の入力。
- 定常で使う値そのもの（評価位置は着地済みならクエリ値）。
- 既存の trace フィールド（`playback=` / `delta=`）の意味（§3-1）。
