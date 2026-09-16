# T2: LTC フレームの時刻を音声サンプル位置から出す（受信バッファの 0〜50ms の揺れを消す）

作成: 2026-09-16 14:20、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main `12cc8ff`** をまず `agent-a` へ通常マージしてから始める（`6e6214e` 以降は文書だけなので衝突しない）。

V3 は合格した（2026-09-16 13:58）。この作業は合格を取り消すものではなく、**残っている揺れを消す改善**。
2 段階に分け、**段 1 は測るだけ（同期の挙動を変えない）**。段 2 は親が段 1 の数字を見てから合図する。

---

## 1. 事実（親がコードで確認済み）

### 1-1. いまの LTC 時刻の出どころ

| 段 | 場所 | 何をしているか |
| --- | --- | --- |
| 音声受信 | `LtcAudioMonitor.OnAudioData`（NAudio `WasapiCapture.DataAvailable`、音声スレッド） | バッファをまとめて `LtcAudioSampleProcessor.Process` へ |
| デコード | `LtcDecoder.Write` → `Feed` → `OnTransition` → `EmitBit` → `TryParseFrame` | 同期ワード `0xBFFC` が **80 ビットの末尾**で揃った時にフレームを `_queue` へ積む。**サンプル位置は数えていない** |
| イベント | `LtcAudioSampleProcessor.Process` が `LtcFrameReceivedEventArgs(Timecode, Fps, RealTimeSeconds)` を作る | **時刻を持っていない** |
| 受信時刻 | `MainWindow.LtcMonitor_FrameReceived` | `Environment.TickCount64`（ms 分解能）を取り、`SyncAccuracyTrace.RecordLtc` が `Stopwatch.GetTimestamp()` で `ltc` イベントを記録。その後 `Dispatcher.BeginInvoke` で UI スレッドへ |
| 同期 | `LtcSyncController.ReceiveFrame` → `ReceiveProcessedFrame` | `receivedAtMilliseconds` は**信号ロス検出（`ObserveValidFrame`）にしか使っていない**。同期値 `effectiveSeconds` は `SyncOffsetPolicy.Apply(processed.ResolvedSeconds, offset)` で、**経過時間を足していない** |

### 1-2. 揺れの構造（`docs/SYNC-ACCURACY.md`「測定の外にある LTC 経路の遅延」）

- 音声コールバックは約 50ms ごと（`LTC audio stats callbacks=40` / 2 秒）。LTC 25fps の 40ms フレームが 50ms の塊で届くので、
  **受信時刻で見た LTC には 0〜50ms の揺れ**が乗る（受信間隔の実測: 50ms が 1762 件・0ms が 472 件、n=2235）
- その揺れがそのまま残差（`delta`、`residualMs`）に入る。T7b 以降のログで Smooth の倍率が 1 フレームおきに 0.97 前後と 1.02 前後を往復し、
  Jump（T8）が空振りするのはこれが原因（`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の T7 節「残差の揺れの元」）
- **同じバッファの中で「同期ワードが揃ったサンプルの位置」は分かる。** バッファ末尾からの残りサンプル数を秒に直せば、
  フレームの終端時刻はサンプル精度（48kHz なら 0.02ms）で出る。コールバックの周期に依存しなくなる

### 1-3. 測定ハーネスの基準（重要。符号と定数の罠を避けるため先に書く）

`scripts/analyze-sync-accuracy.py` は **`ltc` イベントごとに 1 行**作り、その行の期待位置を「そのイベントの LTC 値」、
実際の位置を「そのイベントの `ticks` 時点で最新のフレームの PTS」とする。つまり**基準は「受信した瞬間の LTC 値をそのまま」**。

- いまのアプリはその基準と同じもの（受信した瞬間の LTC 値）を目標にしているので、揺れが基準とアプリの両方に同じ向きで乗り、
  **ハーネスの `signedErrorMs` にはこの揺れが見えにくい**（V3 の p95-p5 50〜67ms の中に一部だけ現れる）
- 段 2 でアプリが経過時間を足すと、**ハーネスの基準を変えない限り、平均が約 +25ms ずれ、p95-p5 は悪化して見える。**
  それは改善が失敗したのではなく基準がずれているだけ。だから段 2 では**解析の基準も「サンプル位置の時刻」に切り替えられる**ようにする（既定は従来のまま）
- 「80 ビットの完成待ち 40ms」（フレーム T の値は T の終端でしか出ない。終端では送出側の時計は T + 1 フレームを指している）は**定数**。
  **T2 では足さない**（定数は `SyncOffset` 設定の領分。足すかどうかは親と利用者が別途決める）。T2 が消すのは**変動分**だけ

---

## 2. 段 1: サンプル位置から時刻を出し、測る（同期の挙動は変えない）

### 2-1. デコーダにサンプル位置を持たせる

- `LtcDecoder` に「これまで `Feed` したサンプルの通し番号」（`long`）を持たせ、`TryParseFrame` で積むフレームに
  **同期ワードの最後のビットを確定させた遷移のサンプル番号**（= フレーム終端のサンプル位置）を添える
- `Read()` の戻りに終端サンプル番号を付ける（`LtcTimecode` は変えず、`record LtcDecodedFrame(LtcTimecode Timecode, long EndSampleIndex)` のような別の型で返す。
  既存の `Read()` の呼び出し元は `LtcAudioSampleProcessor` だけなので、そこを合わせる）
- 通し番号は `Start` ごとに 0 から。デコーダを作り直す時も 0 から

### 2-2. 時刻への換算（`LtcAudioSampleProcessor`）

- `Process` に **コールバック入口の QPC**（`Stopwatch.GetTimestamp()`、`OnAudioData` の先頭で 1 回取る）を渡す
- コールバック k のあと、デコーダに入れた総サンプル数を `N_k`、その時刻を `Q_k` とする。**単純な換算**は
  `frameEndQpc = Q_k − (N_k − EndSampleIndex) × Stopwatch.Frequency / sampleRate`
- ただし `Q_k` にはスレッドのスケジューリング遅れが乗る（遅れる方向にしか揺れない）。そこで**アンカーの最小値フィルタ**を使う:
  `offset_k = Q_k − N_k × F / sampleRate` を各コールバックで計算し、**直近 2 秒（コールバック約 40 回）の最小値**を `anchor` とする。
  `frameEndQpc = anchor + EndSampleIndex × F / sampleRate`。窓から古い値が抜けたら残りの最小値に更新する（単調ではない。ドリフトに追従するため）
- `LtcFrameReceivedEventArgs` に **`long FrameEndTimestamp`（QPC）、`long CallbackTimestamp`（QPC）、`long EndSampleIndex`** を足す。既存の 3 項目は変えない
- 音声デバイスのサンプルレートは `capture.WaveFormat.SampleRate` を使う（`PcmSampleConverter` がモノラル化した後のサンプル数と一致すること）

### 2-3. 記録（`SyncAccuracyTrace`）

- `RecordLtc` の `ltc` イベントに **`sampleTicks`（= `FrameEndTimestamp`）と `callbackTicks`** を足す。**`ticks` は今のまま**（受信ハンドラの QPC）。
  古い run には無い項目なので、解析は無ければ従来どおり動くこと
- アプリログには毎フレーム出さない。`LTC audio stats` の 2 秒ごとの行に、直近 2 秒の `(ticks − sampleTicks)` の中央値と最大、
  `anchor` の揺れ幅（窓内の `offset_k` の最大 − 最小）を ms で足す

### 2-4. 解析スクリプト（新規 `scripts/analyze-t2-ltc-clock.py`）

`events.jsonl` を読み、次を表で出す（単位 ms、n、中央値、p5、p95、最小、最大）:

1. `ticks − sampleTicks`（受信ハンドラがフレーム終端からどれだけ遅れて動いたか）。**予想: 0〜60ms、負は 0 件**
2. 連続する `sampleTicks` の間隔。**予想: 40.0ms（25fps）± 1ms 未満**。ここが 0 / 50ms の二山になっていたら換算が間違い
3. 連続する `ticks` の間隔（従来の量子化の姿。0 と 50ms の二山になるはず。比較のため）
4. `offset_k` の窓内ばらつき（最小値フィルタがどれだけ効いたか）

`scripts/tests/` に単体テストを 1 本足す（合成した events.jsonl で 1〜3 の値が出ること）。

### 2-5. 非E2E テスト

- `LtcDecoder`: `LtcTestSignalGenerator.Generate` で連続フレームを作って `Write` し、連続する `EndSampleIndex` の差が `sampleRate / fps`（48000/25 = 1920）になること。
  バッファを 100 サンプルずつに分けて `Write` しても同じ値になること（境界をまたぐフレーム）
- `LtcAudioSampleProcessor`: 2 回の `Process`（各 2400 サンプル）で、2 回目の中でフレームが終端したとき、`FrameEndTimestamp` が
  `anchor + EndSampleIndex × F / sampleRate` に一致すること。QPC は差し込めるようにする（`Func<long>`）
- 最小値フィルタ: 遅れて届いたコールバック（`Q_k` が大きい）が anchor を動かさないこと。窓から抜けたら更新されること

### 2-6. 実機（**親の合図を待つ。いま実機は D8 の測定で使用中**）

- `scripts/run-v3-accuracy.ps1 -Backends gst -Label t2a-<SHA> -Repeats 1`（Smooth、LTC 25、Debug、既定の設定）
- `python scripts/analyze-t2-ltc-clock.py <run>` の表と、`accuracy-samples.csv` のハッシュが**同じ SHA の T11 の run と同じ性質**（平均・p95-p5 が T11 の範囲内）であることを報告する。
  段 1 は同期の挙動を変えていないので、ここが大きくずれていたら何かを壊している

### 2-7. 段 1 の報告に入れるもの

コミット SHA、変更ファイル、非E2E の件数、2-4 の 4 つの表、実機 1 本の平均・p95-p5・収束、証跡のパス。**合否は書かない。**

---

## 3. 段 2: 同期の目標に経過時間を足す（**親の合図の後**。段 1 の数字で設計を確定してから）

概要だけ先に書く。段 1 の結果で細部は変わりうる。

- 切替は環境変数 `TCS_LTC_SAMPLE_CLOCK`（`on` / `off`）。**既定は `off`**（既定の切替は測定の後に親が決める）。起動時に 1 行ログ
- `LtcSyncController.ReceiveProcessedFrame` の入口で、`effectiveSeconds` を作る前に
  `age = (nowQpc − frame.FrameEndTimestamp) / F` を **`processed.ResolvedSeconds` に足す**（T3 の「同期に使う値だけを入口で 1 回」の場所）。
  表示用の `LastLtcSeconds` は生値のまま。`age` は 0〜0.5 秒にクランプし、範囲外なら足さずに 1 回だけ警告
- 粗い判定（`SyncDecisionEngine.Decide`）・Smooth の残差・Jump のシーク先・Continue の素材位置は、すべてこの `effectiveSeconds` から派生しているので自動的に揃う。
  **`_pendingSyncSeconds`（`Tick` で後から `RequestSync` する保留値）は時刻を一緒に持ち、使う時点で `age` を取り直す**
- 信号ロス検出（`receivedAtMilliseconds`）は変えない
- `scripts/analyze-sync-accuracy.py` に `--ltc-clock receipt|sample` を足す（既定 `receipt` = 従来）。`sample` は `ltc` イベントの時刻を `sampleTicks` にし、
  **イベントを時刻順に並べ直してから**行を作る（`sampleTicks` は `ticks` より最大 50ms 過去なので、順序が入れ替わる）。
  `receipt` で T11 の 3 run（`t11-6e6214e-ltc25-gst-1..3`）を解析し直して `accuracy-samples.csv` のハッシュが変わらないこと
- 測定: `on` で Smooth 3 本、`off` で 1 本（同じ SHA の対照）。解析は `receipt` と `sample` の両方で出す。
  見る数字: 平均、p95-p5、収束、`Smooth correction rate=` の切替回数、定常区間の `residualMs` の標準偏差

---

## 4. 守ること

1. **触らない領域**: `src/TimecodeSyncPlayer/Output/`、`native/gst-shim/`、`OutputEngine`、`GStreamerSource`（もう 1 人が D8 で作業中）。
   `MainWindow.xaml.cs` は `LtcMonitor_FrameReceived` の周辺だけ
2. `docs/OUTPUT-GPU-INVARIANTS.md` の I6（コールバックスレッドでブロックしない）。音声スレッドで重い処理・ロック待ちを足さない
3. 実機を使う前に親へ一報する。**D8 の測定（`w5:p6`）が実機を使っている間は待つ**
4. 合否判定は書かない。数字と証跡だけ
5. main への書き込みはしない。コミットは `agent-a`、メッセージは日本語
6. 段 2 は親の合図まで実装しない（設計の質問は先にしてよい）
