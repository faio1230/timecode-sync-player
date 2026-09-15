# D7: 同期の残差補正（V3 を通すための設計）

対象: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の V3（不合格）。
不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13）。**判定基準は緩めない。**

---

## 1. いま分かっていること（親がコードで確認した事実）

### 事実 1: 同期エンジンに「追従」が無い

`SyncDecisionEngine.Decide` の出口は **`None` か `Seek` の 2 つだけ**（`SyncDecisionEngine.cs:89` の
`SyncActionType`）。消費側（`SingleModeSyncCoordinator.cs:41`、`ContinueSyncSeekPlanner.cs:23`、
`MainWindow.xaml.cs:1785`）は `Action != Seek` で全て早期 return する。
**連続的な補正の経路が存在しない。**

### 事実 2: デッドゾーンが広い

```csharp
// SyncDecisionEngine.cs:23
toleranceSeconds = Math.Max(fps.VideoFrameSeconds, fps.TimecodeFrameSeconds) * _options.ToleranceFrames;
// ToleranceFrames = 6.0
```

| 素材 fps | LTC fps | デッドゾーン |
| ---: | ---: | ---: |
| 60 | 30 | **±200ms** |
| 29.97 | 30 | **±200ms** |
| 24 | 30 | **±250ms** |

この中では `None` が返り、**何も起きない**。V3 で観測した定常オフセット
（freeze-sweep −37〜−53ms、seek-a/b/back −102〜−113ms）は**全てこの中**にある。
「その後、誰も補正しない」は不具合ではなく、**設計にその経路が無い**という意味だった。

### 事実 3: 唯一の道具である Seek は、この誤差を直せない

seek-c（60fps クリップ）の着地遅延は **254.9ms**（24/29.97fps では 91〜121ms、mpv は同条件 143.0ms）。
再シークしても**同じ 254.9ms をもう一度払う**ので、−274ms は原理的に収束しない。
`TimecodeSyncSeekState.HasReachedSeekTarget` の下限は `TargetSeconds - tolerance`
（60fps で target−200ms）なので、−274ms の着地は「未到達」のまま `Pending` が続き、
2 秒で `TimedOut` → 再シーク → また −274ms、という繰り返しになる。

### 事実 4: これはエンジン非依存のアプリ側の欠落

mpv も同じ計測で定常 −81.0ms、seek-c −147.5ms。**両バックエンドに同じ穴がある。**
直せば mpv・GStreamer の両方が良くなる。

### 事実 5: レート微調整は今のままでは使えない

`tcs_player_set_speed`（`native/gst-shim/src/tcs_gstreamer.cpp:2512`）は

```c
gst_element_seek (pipeline, new_rate, GST_FORMAT_TIME,
    GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE | GST_SEEK_FLAG_SKIP, ...);
```

**フラッシュシーク**である。微調整のたびに絵が飛ぶので、連続追従には使えない。
アプリ側で `speed` を触っているのは手動の速度切替キー（`MainWindow.xaml.cs:1306`）だけ。

---

## 2. 方針: 2 段構え。**D7-a だけで V3 を再測する**

V3 の不足は 1 行（seek-c の −274ms）に収束している。
**まず D7-a（アプリ側のみ）で測り、基準を満たせば D7-b はやらない。**

### D7-a 先行シーク補償 — 今回の依頼

**sync seek のターゲットに、実測の着地遅延を足す。**

シーク発行から新しい位置の最初のフレームが出るまでに `L` かかるなら、その間に LTC は `L` 進む。
だから `ltcSeconds` ではなく `ltcSeconds + L` を狙えば、フレームが出た時点で LTC と一致する。

- **`L` は実測で学習する。** 既存のトレースに材料がある:
  `seek.decide`（SYNC）の QPC → 新しい位置の最初の `source.acquire` が `Ready` になった QPC。
- 学習はトラック単位。**トラックを切り替えたらリセット**（60fps は 255ms、24fps は 91ms と
  素材で大きく違うため、混ぜてはいけない）。
- 保持は指数移動平均（係数は 0.25 程度から）。**初期値 0**（学習前は現行と同じ挙動）。
- **上限 400ms・下限 0** でクランプ。上限に張り付いたら警告ログを出す。
- 補償後のターゲットは `Math.Clamp(ltc + L, 0, duration)`。
- **`TimecodeSyncSeekState.BeginSeek` へ渡すのも補償後のターゲット**にすること
  （そうしないと `HasReachedSeekTarget` が元のターゲットで判定して、また未到達のままになる）。

**やらないこと:**
- LTC の更新イベントごとにシークを増やさない。**シークの回数は変えない。**行き先だけ変える。
- デッドゾーンの幅（`ToleranceFrames = 6.0`）は**今回は触らない**。

### D7-b 連続追従（レート微調整）— 今回はやらない。記録だけ

D7-a で V3 が通らなかった場合の次の手。着手前に親へ相談すること。

- `SyncActionType` に `Trim` を足し、デッドゾーン内の残差 `e` に対して
  `rate = 1 + k·e`（clamp ±0.5% 程度）を出す。
- デッドバンド（`|e| < 1 フレーム` で `rate = 1.0` へ戻す）とヒステリシスで発振を止める。
- **shim 改修が要る**: `GST_SEEK_FLAG_INSTANT_RATE_CHANGE`（GStreamer 1.18+）による
  非フラッシュのレート変更経路を `tcs_player_set_speed` に足す（現行のフラッシュ経路は
  手動の速度切替用に残す）。mpv は `speed` プロパティでそのまま滑らかに変わる。
- 音声のピッチ影響の確認が要る（±0.5% は可聴域外のはずだが**未検証**）。

---

## 3. 依頼（D7-a）

1. **単体テストを先に書く。** 最低限:
   - 学習前（`L=0`）は現行と同一のターゲットを出す
   - `L` 学習後はターゲットが `ltc + L` になる
   - `duration` を超えない・0 を下回らない
   - 上限 400ms でクランプされる
   - **トラック切替でリセットされる**
   - `BeginSeek` に渡るターゲットが補償後の値である
2. 実装する。**製品コードの変更は最小限に。**
3. 非E2E を全て通す。
4. 実機で V3 を再測する（`scripts/run-v3-accuracy.ps1`、GPU 合成・`backend=1`/`outputBackend=1`）。
   **mpv 側も同じビルドで測る**（この修正は両バックエンドに効くため、比較の基準も動く）。
5. 報告する: コミット SHA、変更ファイル、非E2E 件数、実機の時刻と指標、設計差異、未検証。

## 4. 合格条件（V3 の基準そのまま。緩めない）

- **定常誤差**: mpv×Gpu と同等以下
- **回復時間**: シーク p95 **250ms 以下**、ギャップ再入 p95 **500ms 以下**

加えて D7-a 固有の確認:

- **シークの発振が無いこと。** `seek.decide` が 2 秒間隔で繰り返し出ていないか、
  各フェーズのトレースで確認する（事実 3 の繰り返しが消えているのが目的）。
- `L` の学習値をログに出し、**60fps で 200〜300ms、24/29.97fps で 80〜150ms 付近**に
  収束しているか確認する。大きく外れたら計測点の取り方が違う。

## 5. 守ること

1. **合否判定は書かないこと**（判定は親が出す）
2. **実機は 1 本ずつ。自分の PID のみ終了**
3. `main` へ書き込まない。統合は親が ff で行う
4. デッドゾーンの幅と `HasReachedSeekTarget` の上下限の非対称
   （`upper = target + tolerance*2`）は**今回は変えない**。変えたくなったら先に相談すること
