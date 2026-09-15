# T7: 同期補正（Smooth / Jump）が Continue モードの 2 本目以降のクリップで効かない不具合を直す

**製品のバグ修正。** T5 で入れた補正が、タイムライン上でオフセットを持つクリップでは
**タイムライン秒と素材秒を引き算している**。V3 はこの状態で測っていたので、測定のやり直しまでを含める。

---

## 1. 何が起きているか（親がログとコードで確認済み）

### 症状（T6 の 3 run すべてで同じ）

`t6-43bba1b-ltc25-gst-1` のアプリログ:

```
01:33:15.530 Continue mode: switching to track accuracy-2-30000-1001 at media position 0.000s
01:33:15.540 Smooth correction rate=1.10000 residualMs=11999.0
01:33:19.747 Smooth correction rate=1.00000 residualMs=11891.8    ← smooth-ineffective
（以後、run 終了まで Smooth のログは 0 件）
```

- clip 2 のタイムライン上の開始は 12 秒。**残差 11999ms はそのオフセット分**
- 2 秒たっても縮まないので `smooth-ineffective` で Smooth が自分を無効にした
- 無効化は解けないまま、**freeze-sweep と全シークフェーズを Smooth なしで測っていた**
- run2 / run3 も 01:35:11 と 01:37:07 に同じ行が出て、同じく以後 0 件

### 原因 1: 残差の計算に使う値の単位が違う

`LtcSyncController.ReceiveProcessedFrame` → `ApplyCorrection(effectiveSeconds)`（T3 統合後）。

```csharp
SyncCorrectionDecision decision = _correction.Evaluate(
    ltcSeconds - playback, ltcSeconds, _effects.GetCorrectionMode(), _smoothAvailable, DateTime.UtcNow);
```

- `ltcSeconds` は**タイムライン秒**（LTC を実時間に直し、T3 のオフセットを足した値）
- `playback` は**いま読み込んでいるクリップの素材秒**
- **Single モード**は LTC 秒をそのまま素材位置として判定しているので一致する（`SingleModeSyncCoordinator.cs:39`）
- **Continue モード**は `ContinueOnTrackPlanner` がタイムラインから素材位置 `MediaPositionSeconds` を出し、
  粗い同期判定はそちらで行っている（`ContinueOnTrackCoordinator.cs:100`）。**補正だけがその変換を通っていない**
- `Jump` のシーク先も `ltcSeconds`（タイムライン秒）になっている。clip 2 では素材の長さを超えた位置へシークする

### 原因 2: 補正の状態がトラック切替で捨てられない

`SyncCorrectionController.Reset()` のコメントは「トラック切替・モード切替・手動操作で状態を捨てる」だが、
呼んでいるのは `SyncEnabledChanged` と `SyncModeChanged` の 2 か所だけ。
**一度 `smooth-ineffective` になると、そのセッションが終わるまで Smooth は戻らない。**

### 親の指示書の責任

T5 の指示書は残差を `e = effectiveLtcSeconds - playbackSeconds` と書いた。
**Continue モードで素材位置へ変換することを書いていなかった。** 実装はその文面どおりである。

## 2. 直すこと

1. **補正の残差は、粗い同期判定と同じ 2 つの値から作る。**
   Continue モードでは「そのフレームで `ContinueOnTrackPlanner` が出した素材位置」と「同じ時点の再生位置」。
   Single モードは現行のままでよい。
   **同じ LTC フレームについて、補正の残差と `sync.evaluate` の `delta` が一致すること**を不変条件にする
2. **`Jump` のシーク先も素材位置にする**
3. **補正を評価しない状況**を明示する。少なくとも次のとき:
   - そのフレームでトラック切替（`SwitchTrack`）を発行した
   - ギャップ中（Black / Freeze）
   - ファイルロードの安定待ち（`TryMarkFileLoaded` が false）
   - ネイティブのシーク中、保留中のシークがある
   - 読み込み済みのトラックと、LTC が指すトラックが違う
4. **`Reset()` を呼ぶ場所を、クラスのコメントどおりにする。** 少なくとも次のとき:
   トラック切替（ロード成功）、ギャップへの出入り、操作者の手動シーク・再生・一時停止、プロジェクトやプレイリストの差し替え。
   既存の 2 か所は残す
5. 値の渡し方（コーディネーターの戻り値で渡すか、共有する状態に置くか）は任せる。
   ただし**素材位置を出す計算を 2 か所に書かない**こと

## 3. テスト（非E2E）

- Continue モード、clip 2 がタイムライン 12 秒開始: LTC 12.500s、再生位置 0.450s のとき
  **残差は +50ms**（12050ms ではない）、レートは `1 + 0.05`、Smooth は無効化されない
- 同じ条件で `Jump`: シーク先は **0.500s**
- clip 1 で `smooth-ineffective` にした後、clip 2 へ切り替えると **Smooth が再び動く**
- 上の 3 番の各状況で補正を評価しない
- 補正の残差と `sync.evaluate` の `delta` が同じフレームで一致する（T3 のオフセットが 0 以外のときも）
- Single モードの既存テストが変わらず通る

## 4. 実機での確認（テストが通ってから）

条件は T6 と同じ（gst / `outputBackend=1` / 先行補償 off / LTC 25 / 出力トレース有効 / Debug）。

1. **`Smooth` を 3 本、`Jump` を 1 本**
2. 出すもの（フェーズ別と全体。数字だけで、合否は書かない）:
   - `delta` の中央値・p5・p95
   - `signedErrorMs` の中央値・p5・p95、プールした p95-p5
   - **絵の遅れ（`signedErrorMs + delta`）**の中央値（`analyze-t6-gap.py` の修正版で）
   - **クリップ別・フェーズ別の `Smooth correction` の行数**、`smooth-ineffective` の回数、補正シークの回数
   - ロード完了（`Gst loadfile ... elapsedMs=`）が切替のたびに出ていること
3. 比較として T6 の 3 本（`t6-43bba1b-ltc25-gst-1..3`）の同じ数字を並べる

## 5. 守ること

1. 原因 1 と 2 以外の挙動を変えない（制御則、デッドバンド、±10%、連続 3 回の上限、2 秒の無効化判定はそのまま）
2. 合否判定は書かない。親が出す
3. 実機は直列に 1 本ずつ。自分が起動した PID だけ終了する
4. 再入ガード（ロード中の再入を止める仕組み）は入れない。別に決める
