# D3: sync seek の保留が GStreamer で長く続く

worktree は現行のまま。main から新ブランチ `codex/d3-seek-pending-20260913` を切る。
不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。実機の前に一声かけること。

## 親の訂正（前提の整理）

親は当初これを「エンジンの空転」と呼び、D2 の合格条件に「decide/issue 比が mpv に近づくこと」を入れたが、
**2 点とも不正確だった**。

1. **D2 の経路を通らない条件だった**。再同期ハーネスは一時停止しないので D2 の pump は arm されない。撤回済み。
2. **「空転」ではない**。`TimecodeSyncSeekState.ShouldSuppressSeek` は**シーク保留中は発行を抑止する設計**で、
   `seek.decide` は判断時点で記録され多くが抑止される。**decide/issue 比が高いこと自体は設計どおり**。

**本当の問題は「保留が長く続くこと」**。保留は `HasReachedSeekTarget(playbackSeconds, toleranceSeconds)` で解除されるので、
**シーク後に再生位置が目標へ到達していない、または到達が報告されていない**ということになる。

## 事実

| 再同期ハーネス（31 秒） | decide/issue | クラスタ最大 | decide 時の \|ずれ\| 中央値 |
| --- | ---: | ---: | ---: |
| GStreamer（D1 後・D2 後） | 190/41 = 4.63 | 41 | **1.728 秒** |
| mpv | 36/39 = 0.92 | 10 | 1.288 秒 |

実装側の観測: **`decide` クラスタは `issue` 間（sync seek の pending 中）に発生する。
D1 の `seeking` は最初の配信（約 180ms）で `no` に戻るが、sync seek の pending はその後も続く。**

## 依頼: どちらが起きているかを計測で決める

**仮説 A: 位置の報告が遅れている。** シークで実際には目標へ到達しているのに、`time-pos` の報告が
遅れている（たとえば shim が「最後に配信したフレームの pts」を返しており、配信の遅れ分だけ古い）。
→ この場合、直すのは**報告**であって再生ではない（D1 と同じ種類の問題）。

**仮説 B: 実際に到達していない。** パイプラインが目標位置へ行っていない、または行くのが遅い。
→ この場合、直すのは**シーク経路**。

### 測り方（設計は任せるが、次を同じ QPC で並べること）

1 回の sync seek について、発行後の時間軸で:

- シークの目標位置
- `HasReachedSeekTarget` に渡っている `playbackSeconds`（＝アダプターが返す `time-pos`）
- 実際に配信されたフレームの pts（`gst.delivery`）
- `ShouldSuppressSeek` の戻り値と `LastStatus`（Pending / Settled / TimedOut / None）

**目標到達の瞬間を、報告位置と実フレーム pts の両方で見ること。** 両者がずれていれば仮説 A、
両方とも目標に届いていなければ仮説 B。mpv でも同じ表を取って対比する。

## 直すのはその後

**原因が確定するまで製品コードを変えないこと。** 計測イベントの追加は可。
どちらの仮説かが決まったら、修正方針を提案して親の承認を得ること。

## 参考: `time-pos` の実装

`GstMpvApiAdapter.GetProperty` の `time-pos` が何を返しているか（パイプラインの position か、
最後の配信フレームの pts か）を最初に確認するとよい。ここが仮説 A の核心。

報告は 計測方法／上記の表（GStreamer と mpv）／どちらの仮説か／修正方針の提案／未検証。
