# vblank 位相基準の表示（`--display-pacing vblank`）の実証仕様

状態: 2026-09-10、実装・実機比較を完了。実機で3つの欠陥（表示期限・重複防止の基準・合成優先の順序）を修正した。結果は [実証結果](GPU-VBLANK-PACING-RESULTS-2026-09-10.md) を参照。

## 目的

[走査時刻の計測](GPU-SCANOUT-STATS-RESULTS-2026-09-10.md) で、通知直後に Present する `vsync` は tick より約1フレーム遅く、tick の表示遅延は起動位相依存（0〜16.7ms）だと分かった。表示側が自分の vblank 時計を持ちつつ遅延を小さく一定にするため、フレーム統計から次の vblank を予測し、その `margin` 前に最新画像を選んで Present する方式を比較する。合成60Hz・Spout60Hz・フェンス同期・pool 契約は変えない。

## 動作

- 許可条件: 全画面出力、present-wait 0、fixed plan。`--present-margin-ms`（double、既定 3.0、0.5〜8.0）を追加。根拠: 描画＋Present の実測 p99 約0.4ms と、高分解能タイマーなしで観測した起床遅れ最大約3.5〜4.8ms。margin は比較条件として固定し、振らない。
- 位相の取得: `present.scanout` の観測（`SyncQPCTime`、`SyncRefreshCount`）から、refresh 周期を直近の連続差の中央値（初期値は表示先の `refreshHz`）で推定し、次の vblank を `lastSyncQpc + k × period`（`now + margin` を超える最小の k）で予測する。統計を得る前（起動直後）は tick と同じく合成直後に Present して統計を起動する（`display.vblank.bootstrap` を記録）。
- ループ（GPU worker の idle hook）: 目標時刻 `target = predictedVblank − margin`。`min(target, 次の合成期限)` まで、停止 handle と高分解能 waitable timer（`CreateWaitableTimerExW` に `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`、失敗時は通常タイマーにフォールバックして manifest に記録）を `WaitForMultipleObjects` で待つ。合成期限が先なら合成を優先し、合成後に再判断する。
- target に達したら: 最新 lease を取り、ID が最後に表示した ID より大きければ描画→GPU完了確認→source返却→Present。同じ ID なら `display.vblank.noNewerImage` を記録して見送る。Present 直前に latency waitable を timeout 0 で確認し、未シグナルなら `display.vblank.notReady` を記録して見送る（Present のブロックを避ける。予測が正しければ発生しない）。予測 vblank 1回につき表示は最大1回（当初は `predictedRefreshCount` で管理する案だったが、統計の番号と1ずれるため vblank 時刻で管理する）。遅れて到達した場合（`now > predictedVblank`）は次の vblank へ再予測し、過去分を貯めない。
- 待機中に lease・keyed mutex・フェンス待ちを持たない。停止優先、停止後に新規描画・Present をしない。

## 記録

- `display.vblank.wait.start/end`: start は value=要求待ち時間（µs 単位の整数）、deadlineQpc=target；end は detail=`target`／`compose`／`cancelled`／`error`、deadlineQpc=target、value=起床遅れ（end.qpc − target、µs、compose 復帰では 0）。
- `display.vblank.predict`: 表示試行ごとに1件、value=predictedRefreshCount、deadlineQpc=predictedVblankQpc、detail=推定周期 µs。
- 既存の `display.select.*`、`display.draw.*`、`present.start/return`、`present.scanout` はそのまま。manifest に `presentMarginMs`、`highResolutionTimer`（bool）。

## 解析

`analyze_probe.py` に `vblank` セクション: 待ち件数と復帰理由、起床遅れ（平均／p99／最大 ms）、予測誤差（同じ Present の `SyncQPCTime` − `predictedVblankQpc`、平均／p99／最大 ms）、noNewerImage／notReady／bootstrap 件数、予測 refresh あたり表示1回の検証、表示 ID の単調増加。既存 `scanout` セクション（生成→走査、Present開始→走査、連続 refresh 差）で tick と比較する。古いログは `available=false`。

## 管理テスト

予測（k の選択、遅延到達時の再予測、周期推定の中央値）、1 vblank 1表示、合成期限優先、停止優先、bootstrap から通常への遷移を、フェイク時計・フェイク統計で検証する。

## 実機と合格条件

同一バイナリ、fence・位相4ms・retry off・monitor index 1（60Hz）。1080p windowed は統計が粗いので確認は全画面 1080p 12秒で行う（monitor index 1、`-Windowed` なし）。その後 4K tick→vblank→vblank→tick 各32秒。

- 全 run 有効・error 0、`notReady` 0、連続 refresh 差すべて1、合成 60Hz 維持。
- vblank: Present開始→走査の平均が margin＋1ms 以内、生成→走査の平均が両 run で 1ms 以内に一致（起動位相非依存）かつ tick の 2 run のいずれより小さいか同等、起床遅れ p99 が 0.5ms 未満（高分解能タイマー）、CPU が tick と同等。
- 満たせば後続基準を `fence`＋`vblank` とし、120Hz 表示先（DISPLAY1 1920×1080@120、monitor index 0）で tick／vblank を比較する。

## 範囲外

mpv・CPU アップロード・本体変更、Spout 側の変更、margin の掃引。
