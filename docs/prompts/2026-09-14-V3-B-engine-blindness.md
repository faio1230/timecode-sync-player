# V3-B: 同期エンジンが 274ms のずれを「見ていない」理由を確定させる

main から新ブランチ `codex/v3-b-engine-20260914` を切る。
**自分のワークツリー（`C:\Users\codea\Documents\timecode-sync-player-wt-output-engine-20260911-1247`）で作業すること。**
main のワークツリーは親も使う共有の場所なので使わない。

不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I13 を含む）。
**これは計測だけの依頼。製品の挙動は変えない。判定も書かない。**

---

## ここまでで分かっていること

依頼 A で、次の連鎖が確定した（`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の
「ばらつきの正体」「着地が遅れる段階の特定」）。

1. 60fps クリップへシークすると、最初のフレームが出るまで **254.9ms**（mpv は 143.0ms）
2. その遅れが**そのまま定常オフセット -274ms** になる
3. **5 秒間 -270ms 前後で一定。誰も補正しない**

## 新しい事実（親が確認）

`spread/gst` の `seek-c` フェーズ（5 秒間）の制御イベントは**これだけ**だった。

```
  232ms load.issue     value=3080000  start
  240ms load.return
  283ms player.seeking raw=no
 2311ms player.seeking raw=no
 4322ms player.seeking raw=no
```

**`seek.decide` が 1 件も無い。**

`seek.decide` は `SyncDecisionEngine.Decide` の中で、
`Math.Abs(delta) <= toleranceSeconds` を**抜けた後**・抑止判定より**前**に記録される。
つまり**エンジンは「ずれている」と一度も判断していない**。

許容は `ToleranceFrames = 6.0`、`max(videoFrameSeconds, timecodeFrameSeconds) * 6` なので、
25fps LTC では **240〜250ms**。表示は -274ms ずれているので、**本来なら超えているはず**である。

## 依頼: どちらが起きているかを計測で決める

### 仮説 (a): エンジンの `delta` が許容内

`delta = ltcSeconds - state.PlaybackSeconds` の `PlaybackSeconds` は
**プレイヤーの自己申告位置**であって、**実際に画面に出ているフレームの位置ではない**。
デコード → リング → 合成 → 公開 の遅延の分だけ申告が先行していれば、
**エンジンには同期して見えるが、見ている人には 274ms 遅れて見える。**

この場合、直すべきは「残差を補正する」ではなく
**「エンジンに表示位置を見せる」**か「申告位置と表示位置の差を既知の補正として入れる」になる。

### 仮説 (b): `IsSeeking` が真のままで、`Decide` が冒頭で None を返している

```csharp
if (!state.SyncEnabled || !state.HasCurrentTrack || state.IsSeeking)
    return SyncDecision.NoneWith(fps, toleranceSeconds);
```

この行で返っていれば `seek.decide` は当然出ない。D1 で `seeking` は実装されたが、
**`player.seeking` の記録は 5 秒間に 3 件しかなく、常時の状態は分からない。**

### やること

`SyncDecisionEngine.Decide` に、**出力トレース有効時のみ**動く計測イベントを 1 つ足す。
`seek.decide` と同じ要領で、**呼ばれるたびに**（＝ LTC フレームごとに）次を記録する。

| 項目 | 内容 |
| --- | --- |
| `stage` | `sync.evaluate` など新しい名前 |
| `value` | `ltcSeconds` をマイクロ秒で |
| `detail` | `playback=` `delta=` `tolerance=` `syncEnabled=` `hasTrack=` `isSeeking=` `reason=` |

`reason` は None を返した理由を 1 語で:
`disabled` / `no-track` / `seeking` / `not-finite` / `bad-duration` / `within-tolerance` / `seek`。

**既定経路のコストをゼロにすること**（`OutputTrace.Current.IsEnabled` が false のとき
文字列を作らない。`seek.decide` と同じ形にすればよい）。これは不変条件 I4 の趣旨。

### 出してほしい表

`spread` と同じ条件（既定設定・`outputBackend:1`・VB-CABLE）で **gst と mpv を 1 本ずつ**。
実機は親が回してもよい（その場合は測り方だけ指示すること）。

フェーズごとに:

| フェーズ | `reason` の内訳（件数） | エンジンの \|delta\| 平均 | 同時刻の**表示誤差** 平均 | 差 |
| --- | --- | ---: | ---: | ---: |

**「エンジンの delta」と「表示誤差」の差**が知りたい中心である。
表示誤差は `analysis/accuracy-samples.csv` の `signedErrorMs` を QPC で突き合わせて取る
（依頼 A で作った `scripts/analyze-v3-spread-stages.py` の突き合わせがそのまま使えるはず）。

## 守ること

1. **製品の挙動を変えない。** 足すのは計測イベントだけ。しきい値も判定も触らない
2. 非E2E が全て通ること
3. `python scripts/check-shim-lock-rule.py` が PASS すること（shim を触らなければ自明だが確認する）
4. **合否判定と「こうすべき」は書かないこと。** どちらの仮説かと、その根拠の数字だけ

## 提出時に書いてほしいこと

- 上の表
- `reason` の内訳から言えること（(a) か (b) か、あるいは両方か）
- 計測を入れた箇所と、既定経路がゼロコストであることの確認方法
