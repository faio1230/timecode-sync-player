# D32: ギャップのフリーズ画像が「遅れて届いたフレーム」「2 回目の進入」「トラック切替後」で更新されない（検証機の証跡から）

作成: 2026-09-17 21:40、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: main の最新（検証機のテスト基盤パッチ統合後）を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
前提: A1 の机上解析（`docs/analysis/2026-09-17-A1-gap-freeze-frame-not-updated.md`）。開発機の 3 条件では再現せず。以下は検証機（候補 2、実素材）のアプリログ（親が抽出。時刻は検証機ローカル）。
**製品コードの変更。同期の目標・補正には触らない。ギャップ進入の状態機械とフリーズ画像の確定・破棄だけ。**

## 1. 証跡

### 1.1 D22（先頭オフセット）の一時停止ロードでフレームが 3 秒以内に届かない（c の F-5、A = M2: H.264 60fps 長 GOP、GPU デコード）

```
18:27:35.532 Gst loadfile start=5 paused=true rc=0 elapsedMs=113.8
18:27:35.533 Continue mode: loading next track first frame for gap freeze track=M2 target=5.000 duration=25.000 fps=60.000 loadOk=true pauseOk=true
18:27:38.590 [WRN] Continue mode: gap freeze final-frame capture timed out, holding current frame
18:27:38.715 Gst loadfile start=5 paused=true rc=0 elapsedMs=93.5   ← 再試行（同じ結果のままテスト終了）
```

ロードは 114ms で返るが、開始位置つきの一時停止ロードのプリロール（キーフレームから 5 秒地点までの復号）が 3 秒で届かず、タイムアウト → Held（直前の絵）のまま。`pump deadline` は 0 件（pump は一時停止中の **seek** だけを対象にし、開始位置つきロードは対象外）。

### 1.2 フリーズ確定後に別の目標で再進入しても再確定しない（b の F-5、A = M1 ProRes 4K60 CPU）

```
18:17:18.094 Gap behavior changed to "Freeze"
18:17:19.667 Gst loadfile <C> start=24.983 paused=true rc=0 elapsedMs=…   ← LTC 開始前。参照採取の直後で位置が C の終端側にあり、「C の後のギャップ」として進入
18:17:19.690 Continue mode: loading previous track final frame for gap freeze track=<C> target=24.983 …
18:17:20.029 Continue mode: gap freeze activated, final frame captured        ← frozen = C の tail
18:17:21.170 Gst loadfile <A> start=5 paused=true rc=0 elapsedMs=1104.6
18:17:21.170 Continue mode: loading next track first frame for gap freeze track=<A> target=5.000 …   ← D22 の正しい進入
（以後 18:17:24.9 のテスト終了まで "final frame captured" なし。画面は C の tail のまま = テストの観測 M5/tail d=0.0、position=5.000）
```

a の F-4・b の F-3 でも Freeze 切替直後に同じ「C の最終フレームを読み込む」進入が先に走る（参照採取で位置が C の終端に残るため。切替時の位置がギャップなら進入するのは仕様として妥当）。問題は **その後の別目標の進入で frozen が置き換わらない**こと。

### 1.3 トラック切替後に直前の絵が残る（a の F-4、B = M4 VP9 1080p GPU）

```
18:10:40.876 Continue mode: entering gap freeze, waiting for final frame target=24.983 …   ← A の後のギャップ（SeekToFinalFrame 経路）
18:10:41.017 Continue mode: gap freeze activated, final frame captured                      ← frozen = A の tail
18:10:41.415 Timecode sync: applying the confirmed Jump frame once ltc=39.880
18:10:41.415 Continue mode: switching to track <B> at media position 14.932s compensation=0.0ms
18:10:41.855 Gst loadfile <B> start=14.93 paused=true rc=0 elapsedMs=440.0
（18:10:44.48 のテスト終了まで B の絵が出ない。テストの観測 f4-03: 期待 B の参照、実際 A の tail d=0.7）
```

B のロードは 440ms で返るのに 2.6 秒以上 A の tail（Held または frozen）が出続けた。開発機では同じ経路が通る。

## 2. 直すこと（設計の当たり。実装前に該当コードで裏を取り、違えば報告）

1. **遅れて届いたフレームでも確定する**: フリーズ確定のタイムアウト（3 秒）は「Held のまま待ち続けない」ための保険にとどめ、タイムアウト後も同じギャップに居る間は目標一致フレームの到着で確定・置き換える（`ForceFreezeComplete` で捕捉を打ち切らない、または打ち切っても `frameSeenSinceCapture` の監視を続ける）。開始位置つき一時停止ロードのプリロールが遅い素材（長 GOP）で効く
2. **進入目標が変わったら frozen を捨てて再確定**: `EnteringFreeze`/`FreezeComplete` 中に別の目標（別トラック・別位置）で進入が決まったら、`ClearGapFreezeFrame` 相当で frozen を破棄し、新しい目標の捕捉をやり直す（P2）。同じ目標の再進入は今のまま（F-1 の 110ms 周期の再進入を増やさない）
3. **トラック切替（ギャップ → トラック）で frozen を必ず破棄し、新トラックの最初のフレームまで Held**: 1.3 の「A の tail が残る」経路を追い、frozen が残るのか Held が残るのかを `GapRenderFramePolicy` と `ComposeLayer.SaveFreeze` の条件で切り分ける。B の最初のフレームが 2.6 秒以上届かない理由（開始位置つき一時停止ロードのプリロール = 1.1 と同根か）も併せて
4. 1.1 の根本: 開始位置つき一時停止ロードに pump（期限つきの PLAYING → 最初のフレーム → PAUSED）を適用できるか shim 側で検討（`pump_arm` の条件）。適用するなら `TCS_PUMP_BUDGET_MS` の既定 4000ms とフリーズの 3 秒の関係を整理（pump の期限 ≤ フリーズの待ち、または 1 の「遅れて確定」で吸収）

## 3. 検証

- 単体: `GapFreezeHandler` / `GapEnterCoordinator` に「タイムアウト後の遅延確定」「別目標での再進入は frozen 破棄」「トラック切替で frozen 破棄」を追加
- E2E（実機は一報のうえ親の合図。除去担当と重ねない）: `LtcScenarioE2ETests` の F-1〜F-5、G-1〜G-6、C-1/C-2 を生成素材（A/B/C）と 4K 3 本（`-Media`）で。合格していた本数が崩れないこと。`TCS_PUMP_BUDGET_MS=800` でも 1 回

## 4. 報告

コミット（項目ごと）、変更の要約、単体の増減、E2E の結果、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。版は上げない。**
