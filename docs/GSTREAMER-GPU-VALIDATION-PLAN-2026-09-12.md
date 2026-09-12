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

## V1 の結果（2026-09-12 13:07〜13:19 JST、`TestResults/v1`、生成素材 `artifacts/media/v1`）

素材は GStreamer で生成（`videotestsrc pattern=ball motion=wavy`＋`timeoverlay`、NVENC／`avenc_prores_ks`、GOP 1 秒、60 秒）。実素材ではないのでデコード負荷は軽め。run は 50 秒、解析窓 8〜48 秒、GStreamer×Gpu、Spout ON、DISPLAY2 全画面。集計 `scripts/GpuOutputProbeHarness/v1_matrix_summary.py`。

| 素材 | デコーダ | 実フレーム/秒（期待） | 表示 最小/秒 | Spout | 合成 p99 | 判定 |
| --- | --- | --- | --- | ---: | ---: | --- |
| H.264 1080p 23.976 | d3d11h264dec | 23〜24（24） | 59 | 60.0 | 0.92ms | 合格 |
| H.264 1080p 25 | d3d11h264dec | 25（25） | 59 | 60.0 | 0.98ms | 合格 |
| H.264 1080p 29.97 | d3d11h264dec | 29〜30（30） | 59 | 60.0 | 0.98ms | 合格 |
| H.264 1080p 59.94 | d3d11h264dec | 59〜60（60） | 59 | 60.0 | 0.90ms | 合格 |
| H.264 1080p60 MPEG-TS | d3d11h264dec | 59〜60（60） | 59 | 60.0 | 0.89ms | 合格 |
| H.264 1080p60 MXF（10 秒素材、8 秒 run） | d3d11h264dec | 起動後 60 | 48（起動含む） | 59.2 | 0.92ms | 読める（長尺で再確認） |
| HEVC 8bit 1080p60 | d3d11h265dec | 59〜61（60） | 59 | 60.0 | 0.91ms | 合格 |
| HEVC 10bit 1080p60 | d3d11h265dec | 60（60） | 59 | 60.0 | 0.89ms | 合格 |
| HEVC 4K60 | d3d11h265dec | 59〜60（60） | 59 | 60.0 | 0.96ms | 合格 |
| H.264 1080p60＋AAC 音声 | d3d11h264dec | 60（60） | 60 | 60.0 | 0.92ms | **S1 修正後に合格**（`fa77d0b`） |
| ProRes 422 1080p60（.mov、素材長 30 秒） | avdec_prores | 60（60、窓 8〜28 秒） | 59 | 60.0 | 0.82ms | **S2 修正後に合格**（`fa77d0b`） |

不合格 2 件の原因（shim の `tcs-shim-test` で再現）:
- 音声付き: qtdemux の `audio_0` パッドが接続されず `Internal data stream error`（not-linked）で読み込み全体が失敗する。実素材はほぼ音声付きなので致命的。
- ProRes: shim の明示プロファイルが `h264-gpu` 等の GPU デコーダのみで、CPU デコード（`avdec_prores`）への退避が無い。同様に DNxHD／MJPEG／その他 CPU コーデック全般が読めない。

→ 実装側へ差し戻した（S1: 音声パッドの接続、S2: CPU デコーダ退避プロファイル＋d3d11upload でリング経路を維持）。

## V1 の再検証（2026-09-12 18:35〜19:10 JST、`TestResults/v1-d347673`・`TestResults/ab-2997`）

S1／S2 の修正 `d347673` を検証し、main へ rebase して ff 統合（`fa77d0b`、native ツリーは検証した SHA と同一）。詳細は `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` の「追記: S1・S2 修正 `d347673`」。

- 11 素材すべてが読み込み可能になった（上表の判定を更新）。基点 DLL に差し替えると AAC・ProRes は `tcs-shim-test` で failures=25 のままで、修正の効果を独立に確認した。
- 非E2E 1712 件成功、全 E2E 55 成功・0 失敗・7 スキップ（基点と同数）。
- 29.97 と MPEG-TS の合成 p99 が 1.10ms／1.02ms と基準を僅かに超えたが、**exe 固定・DLL のみ入れ替えの A/B**（29.97 素材で交互 4 本）で基点 DLL も 1.00〜1.01ms を示したため、shim の回帰ではなく当日のマシン状態による。
- ProRes 422 の素材だけ長さが 30 秒（他は 60 秒、MXF は 10 秒）。V6（長時間）用に 60 秒素材を作り直す。

**V1 合格。次は V2（音声出力・ミュート・音量・速度）。**

### V1 で判明した追跡項目

- **S3: MPEG-TS のシーク後にフレームが期限内に来ない**（`tcs-shim-test` の 2 件が失敗）。基点 DLL でも同一なので既存事象だが、V3（LTC 同期シーク精度）と V5（切替連打）の前に原因を押さえる。
- **GStreamer の E2E はスキップのまま**（`test_720p25.mkv`・`test_720p25.avi`・`test_720p50.ts` と recv ツールが無い）。shim の E2E 被覆はゼロ。素材整備を V2 以降に含める。

