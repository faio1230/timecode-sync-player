# C1: 比較のための計測用スイッチを 2 つ入れる

main から新ブランチ `codex/c1-comparison-switches-20260913` を切る。
不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I13 を含む）。実機を使う前に一声かけること。

**これは計測用の切り替えを足すだけの作業。既定の挙動は 1 ミリも変えない。**
どちらを採用するかは親が測ってから決める。実装側で「こちらが良さそうなので既定にしました」は禁止。

---

## 背景

V3（LTC 同期精度）で GStreamer が mpv に劣る点が 2 つ残っている。原因の候補が
それぞれ 2 択あるので、**同じ素材・同じハーネスで両方を測れる状態**にしたい。

- D1（`seeking` 未実装）と D2（`frame_lock` 保持中の状態変更）は修正・統合済み（main `fdbf543`）。
- したがって今回測るのは「設計上の選択」であって、バグではない。

---

## (a) ギャップ中の扱い: プレイヤーを一時停止する vs 合成側で黒を出す

### 現状

`GapEnterCoordinator` はギャップに入るとき `_effects.PauseForGap()` を呼び、
**プレイヤー自体を一時停止**する。`GapBehavior.Black` のときは加えて黒を描く
（`ContinueModePlaybackPolicy.ShouldRenderBlackWhileGapActive`）。

### 疑い

一時停止して復帰する経路は、GStreamer では
「PAUSED → シーク → preroll pump → PLAYING」という長い往復になる。
D1/D2 前の実測では黒ギャップからの復帰に **11995ms**（mpv は 50ms）かかっていた。
**一時停止せず、再生は進めたまま合成側で黒を出すだけ**なら、この往復が不要になる可能性がある。

### 依頼

環境変数 `TCS_GAP_MODE` を足す。

| 値 | 挙動 |
| --- | --- |
| `pause`（**既定・現状のまま**） | 今と同じ。`PauseForGap()` を呼ぶ |
| `compose-black` | ギャップ中も**プレイヤーは止めない**。合成側で黒を出すだけ |

- 既定値は `pause`。環境変数が無いとき／未知の値のときは `pause` として扱い、
  未知の値なら警告を 1 回だけログに出す。
- `compose-black` でも、ギャップ中に「黒が出ている」こと自体は今と同じに見えるべき
  （利用者から見た表示は変えない）。変わるのは内部でプレイヤーを止めるかどうかだけ。
- 出力トレースに、どちらの経路を通ったか分かるイベントを 1 つ足すこと
  （例: `gap.enter` に `mode=pause|compose-black`）。

---

## (b) シーク方式: FLUSH|ACCURATE vs FLUSH|KEY_UNIT|SNAP_BEFORE

### 現状

`tcs_gstreamer.cpp` の `seek_prepare_locked` は**コンテナで方式を決め打ち**している。

```c
GstSeekFlags flags = p->mpegts
    ? (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT | GST_SEEK_FLAG_SNAP_BEFORE)
    : (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE);
```

MPEG-TS で KEY_UNIT|SNAP_BEFORE にしたのは S3 の対応（tsdemux の ACCURATE が
SPS/PPS を持たない IDR で見失う）で、理由は妥当。しかし
**MP4 側で KEY_UNIT|SNAP_BEFORE が速いのかどうかは測っていない**。
逆に TS 側で ACCURATE がどれだけ遅い／不正確なのかも、数字で残っていない。

### 依頼

環境変数 `TCS_SEEK_METHOD` を足す。

| 値 | 挙動 |
| --- | --- |
| `auto`（**既定・現状のまま**） | 今と同じ。TS は KEY_UNIT\|SNAP_BEFORE、他は ACCURATE |
| `accurate` | コンテナに関わらず `FLUSH\|ACCURATE` |
| `keyunit` | コンテナに関わらず `FLUSH\|KEY_UNIT\|SNAP_BEFORE` |

- 既定は `auto`。未知の値は `auto` 扱いで警告を 1 回。
- **TS の配信ゲートとセグメント書き換え（method 2）は方式と連動させること。**
  `keyunit` を強制したのに ACCURATE 前提のゲートが張られたままだと測定が壊れる。
  どちらの方式でも整合が取れるようにし、どう連動させたかをコミットメッセージに書く。
- 既存の `seek: send begin/end` ログに、実際に使った方式を出すこと。

---

## 守ること

1. **既定の挙動を変えない。** 環境変数を設定しなければ、今のバイナリと同じ動きをすること。
2. **I13 を破らない。** `gst_element_seek` / `send_event` / `set_state` は `frame_lock` の外で呼ぶ。
   提出前に `python scripts/check-shim-lock-rule.py` が PASS することを確認すること。
3. shim を触ったら `native/gst-shim/test/shim_test.cpp` に、
   `TCS_SEEK_METHOD` の 3 値それぞれで seek が成立することの確認を足す。
4. 非 E2E のテストが全て通ること。E2E は親が回すので、実機は勝手に長時間占有しないこと。

## 提出時に書いてほしいこと

- 2 つの環境変数の実装箇所（ファイルと関数）
- (b) でゲート／セグメント書き換えをどう方式に連動させたか
- 既定の挙動が変わっていないことをどう確認したか
- `check-shim-lock-rule.py` の結果

判定は親が実機で測ってから出す。**実装側で優劣の結論を書かないこと。**
