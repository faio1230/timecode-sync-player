# GStreamer×Gpu の実運用検証計画（既定切替の前提）

状態: 2026-09-12、親が作成。目的は `PlayerBackend=Gstreamer`＋`OutputBackend=Gpu` を既定にしてよいかを判断すること。既定値は本計画の全項目が合格するまで変えない（現在 `Mpv`＋`Cpu`）。実行は `scripts/GpuOutputProbeHarness/Invoke-AppGpuTrial.ps1`（1 本ずつ、console セッション）と既存 E2E。

## 合格条件（共通）

実フレーム 60/秒（起動 2 秒を除く）、表示 59.9Hz 以上、合成 p99 1ms 以下、Spout 60Hz、error 0、exit 0、プロセス残存なし。ソース側の欠落（seq 飛びと NotReady の対）0。数値は `app_run_summary.py` と `analyze_probe.py` で出す。

## 項目

| # | 項目 | 方法 | 合格 |
| --- | --- | --- | --- |
| V1 | 実素材のコーデック表 | H.264 High 4:2:0（1080p/4K）、HEVC 8bit/10bit、ProRes 422/4444（`avdec_prores`＝CPU デコード）、MXF（OP1a）、MPEG-TS、フレームレート 23.976/25/29.97/59.94/60 を各 60 秒 | 各素材で共通条件。CPU デコードの素材は実フレームの上限を記録し、表に「GPU デコード可／不可」を残す |
| V2 | 音声付き素材 | 音声トラックあり素材でミュート／音量／再生速度 | 音声出力あり、映像指標は共通条件 |
| V3 | LTC 同期シーク精度 | ケーブルループ E2E（`SyncAccuracyE2ETests`）を GStreamer で有効化して実行。シーク→表示までの遅延と位置誤差 | mpv 経路と同等以下（現行の基準値を先に測って併記） |
| V4 | ギャップ 3 種 | Freeze／Black／Hold をプロジェクトで作り、Gpu 経路で確認（Freeze の元画像が合成前であること、Black が不透明黒、Hold が最後の確定画像） | 既存 E2E（`RealProjectGapE2ETests`）を GStreamer×Gpu 設定で通す |
| V5 | トラック切替・シーク連打 | `GStreamerBackend_SurvivesRepeatedTrackSwitches` と手動 20 回切替、世代排除の記録 | generationRejected が切替直後のみ、黒フレームなし |
| V6 | 長時間 | 120 秒素材をループまたは 1 時間プロジェクトで 60 分 | 欠落 0、到着→取得の遅れが鋸歯（H-3 の 21ms 閾値で 1 枚破棄→4ms 台へ）、メモリ増加なし |
| V7 | mpv×Gpu×Spout | 1080p/4K、受信機あり、受信機断 | 共通条件（実フレームは mpv の上限で可） |
| V8 | 表示先の違い | DISPLAY1（4K 主画面）を表示先にした全画面、120Hz 表示先があれば追加 | 表示周期に合成が追従、落ち 0 |
| V9 | 起動・終了・復旧 | 起動直後の再生、通常終了／強制終了、消失シミュレーション 2 回＋手動再試行を GStreamer と mpv で | 既存 E2E＋段階 5 の runner 手順 |
| V10 | 旧プロジェクトの読込 | Canvas なしの既存 .tsp を開く | ダイアログで選択→保存で記録。キャンセルで 1920×1080 仮採用 |

## 既定切替の条件

V1〜V10 が合格し、GStreamer で読めない素材が現場の一覧に無いこと。切替後も `Mpv`／`Cpu` は設定で戻せる状態を 1 リリース残す。
