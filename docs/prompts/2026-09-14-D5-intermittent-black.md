# D5: GStreamer × GPU 合成で、クリップ切替後に映像が戻らなくなることがある（10 本中 2 本）

main から新ブランチ `codex/d5-black-after-switch-20260914` を切る。
**自分のワークツリー（`C:\Users\<user>\Documents\timecode-sync-player-wt-output-engine-20260911-1247`）で作業すること。**

不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I13 を含む）。実機の前に一声かけること。

---

## これは v0.4 の出荷判断を左右する

**v0.4 が既定にしようとしている経路（GStreamer × GPU 合成）で、
クリップ境界を越えたあと映像が黒のまま戻らなくなることが、5 本に 1 本の割合で起きる。**
ライブショー用途で「5 回に 1 回、曲の変わり目で映像が消えたまま」は出荷できない。

**D4 の修正とは無関係**（D4 の前後どちらでも起きている）。

## 測った事実

VB-CABLE の精度ハーネス（`SyncAccuracyE2ETests`）を GStreamer × `outputBackend:1` で
これまでに 10 本回した結果。`analysis/accuracy-samples.csv` の `status` の内訳:

| run | 時期 | measured | unexpected-black |
| --- | --- | ---: | ---: |
| `a1v/a1-gst-gpu` | D4 前 | 1965 | 14 |
| `t3/base` | D4 前 | 1965 | 13 |
| `t3/gap-black` | D4 前 | 1962 | 23 |
| `t3/seek-accurate` | D4 前 | 1967 | 13 |
| `t3/seek-keyunit` | D4 前 | 1961 | 15 |
| `spread/gst` | D4 前 | 1962 | 17 |
| **`v3b/v3b-gst`** | D4 前 | **247** | **1744** |
| **`d4v/d4-gst`** | D4 後 | **498** | **1494** |
| `d4v/d4-gst-2` | D4 後 | 1957 | 22 |
| `d4v/d4-gst-3` | D4 後 | 1954 | 23 |

**正常時は黒が 13〜23 件**（クリップ境界の一瞬だけ）。**失敗時は 1494〜1744 件**。

**mpv × GPU 合成では 0 件**（`v3b-mpv` と `d4-mpv` はどちらも `unexpected-black=0`）。
**GStreamer 側の問題である。**

### 失敗の形

`d4v/d4-gst` の場合:

- 黒が始まるのは **`black-sweep` のフェーズ内経過 11938ms**。
  これはタイムライン 12 秒＝**clip1 → clip2 の最初のクリップ境界**にあたる
- **そこから最後まで黒のまま**（`freeze-sweep` 749 件、`seek-*` 各 123〜124 件がすべて黒）
- **エラーログは出ていない。** 切替自体は成功しているように見える:

```
05:56:10.664 [INF] Continue mode: switching to track accuracy-2-30000-1001 at media position 0.000s
05:56:10.676 [INF] Gst loadfile path=...clip-2.mp4 start=0 paused=true rc=0
05:56:10.680 [INF] FetchMetadata: 1920x1080 29.970fps V:d3d11h264dec A:
```

- 共有リングは run の最初に 1 回開くだけで、切替ごとには開かない。
  これは**正常な run でも同じ**なので、リングの開き直しは差ではない

## 依頼: 再現条件を絞り、原因を特定する

### 1. まず決定的に再現させる

**5 本に 1 本では原因追及ができない。** 確率を上げるか、決定的に再現させる方法を探すこと。

- 精度ハーネスは 1 本 2 分かかる。**もっと短い再現系を作れないか**
  （`GStreamerBackendE2ETests.SurvivesRepeatedTrackSwitches` に近い形で、
  GPU 合成・クリップ切替を反復し、公開フレームが止まらないことを見る）
- D2 で使った `TCS_TEST_HOLD_FRAME_LOCK_MS` のような**決定的なテストフック**が有効なら、それも検討する
- **確率的なハングは決定的に再現させてから直す**（D2 の教訓、`docs/OUTPUT-GPU-INVARIANTS.md` の I13 補足）

### 2. どこで止まっているかを切り分ける

止まりうる段階と、それぞれの見分け方:

| 段階 | 確認するもの |
| --- | --- |
| デコードが止まった | shim の `tcs_player_get_delivery_stats()` の `arrivals` / `DecoderOut` が進むか |
| 配信が止まった | 出力トレースの `gst.delivery` が来るか |
| 取得が止まった | `source.acquire` が `Ready` を返すか（`NotReady` は「新フレーム無し」の意味） |
| 合成が止まった | `compose.publish` は出ているか（出ているなら黒を合成している） |
| 世代／リースの不整合 | 切替時の `generation` と、取得側が要求している世代が一致しているか |

**`compose.publish` が正常な間隔で出ているのに黒なら、合成には届いているが中身が黒**、
**`gst.delivery` が止まっているならデコード〜配信側**、と切り分けられる。

### 3. 出してほしいもの

- 再現方法（確率と、決定的にできたならその手順）
- 上の表のどの段階で止まっているか、その根拠の数字
- **原因の仮説は数字で裏付けること。推測だけで直さない**

## 守ること

1. **I13 を破らない。** 提出前に `python scripts/check-shim-lock-rule.py` が PASS すること
2. 非E2E が全て通ること（現在 1754 件）
3. **実機は親が回す。** ただし短い再現系の試行は自分で回してよい（1 本ずつ、自分の PID のみ終了）
4. **合否判定は書かないこと**

## 参考

- 失敗した 2 本の生データは `TestResults/v3b/v3b-gst` と `TestResults/d4v/d4-gst` に残してある
- 正常な run との比較対象は `TestResults/d4v/d4-gst-2`（同じバイナリ・同じ設定）
