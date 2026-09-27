# アーキテクチャ図の読み方

対象は **v0.4.7 / `a4cb181` / 2026-09-20** の現行コードです。
主な責務とデータの流れを要約しており、すべてのメソッド呼出しや例外分岐を表すものではありません。

- **編集する:** [architecture.drawio](architecture.drawio) を draw.io / diagrams.net で開きます。4ページあり、箱・矢印・ラベルを個別に編集できます。
- **読む:** [index.html](index.html) をブラウザーで開きます。ページ切替・拡大縮小ができ、外部通信は行いません。
- **画像として使う:** 下記の SVG を利用できます。

| ページ | 答える問い | 画像 |
| --- | --- | --- |
| 01 全体構成 | どの部品が何を担当するか | [01-overview.svg](01-overview.svg) |
| 02 スレッドと映像の流れ | どのスレッドで動き、何を受け渡すか | [02-threads.svg](02-threads.svg) |
| 03 LTC 同期 | 時刻入力からシークや速度変更までどう判断するか | [03-sync.svg](03-sync.svg) |
| 04 起動・復旧・終了 | 誰が資源を作り、つなぎ直し、解放するか | [04-lifecycle.svg](04-lifecycle.svg) |

青の矢印は制御・状態、紫は映像データ、水色の破線は通知です。箱の下部には対応するクラス名を記載しています。

丸印は出力経路の分岐点です。長い折り返し線による交差を避けるため、一部の通知・保留経路には接続先の英字を付けています。
**同じ英字が経路の続き**を表します（02: A＝native 更新通知、B＝プレビュー、C＝UI 更新予約。03: D＝保留要求）。

## 最初に押さえる分担

**UI 側が「どの素材の何秒を再生するか」を決め、GStreamer が素材を復号し、GPU 側が届いた画像を合成・出力します。**

`MainWindow` は UI イベントの処理だけでなく、サービスの接続、状態の受け渡し、各 Coordinator に渡す副作用のデリゲート、起動・復旧・終了の接続も担当しています。`ViewModel` だけを読んでもアプリ全体の制御は追えません。

`RenderSession` はフレーム更新通知を処理する専用スレッドとその寿命を管理します。名前に Render が付きますが、現行構成で映像の合成を担当するのは `OutputEngine` と `ComposeLayer` です。

LTC の音声入力と、動画素材に含まれる音声の出力も別経路です。LTC は NAudio と純 C# デコーダが処理し、動画の音声は GStreamer shim 側が処理します。

## 1フレームを追って読む

1. **音声から時刻を取り出す。** `LtcAudioMonitor` が WASAPI から PCM を受け取り、`LtcAudioSampleProcessor` と `LtcDecoder` がタイムコードとフレーム終端時刻を生成します。`MainWindow` が Dispatcher 経由で UI スレッドへ渡します。
2. **その時刻を採用するか判断する。** `LtcFrameProcessor` と `LtcSyncController` が fps、Jump、Duplicate、信号断などを確認します。通常受信ではサンプル時計による経過時間と同期オフセットを反映します。保持値の再適用には別の扱いがあります。
3. **追従先を決める。** Single は現在のトラックの素材範囲に追従します。Continue は `PlaylistState` のタイムライン検索からトラックと素材内位置を決め、素材のない区間は Gap の処理へ進みます。
4. **操作を決めて実行する。** `TimecodeSyncService` はロード安定、位置の信頼性、着地待ちを管理し、`SyncDecisionEngine` は粗いシークを判定します。継続再生中の補正には `SyncCorrectionController` を使い、Smooth では速度変更、Jump ではシークを判断します。実行は `GstPlaybackApi` に集約されています。
5. **映像を届ける。** shim が復号した画像を共有リングに置き、`GStreamerSource` がその画像を借ります。GPU worker は `TimelineOutputState` の配置・Gap 状態と画像を使って合成します。
6. **各出力へ配る。** 合成済みの pool を全画面出力、Spout、プレビューが使います。プレビューだけは縮小後に CPU へ読み戻し、UI に渡します。

これは毎回シークする仕組みではありません。通常は再生を継続し、ずれ・シーク所要・信号状態などの条件を見て操作します。

## 図からコードへ

パスはリポジトリの現行ファイルへリンクしています。行番号ではなくクラス・メソッド名を手掛かりにすると、更新後も辿りやすくなります。

| 読みたいこと | 起点と注目箇所 |
| --- | --- |
| DI と起動順 | [App.xaml.cs](../../src/TimecodeSyncPlayer/App.xaml.cs) の `ConfigureServices` / `OnStartup` |
| 各部品の実際の接続 | [MainWindow.xaml.cs](../../src/TimecodeSyncPlayer/MainWindow.xaml.cs) のコンストラクタ、`LtcMonitor_FrameReceived`、`CreateSingleModeSyncCoordinator`、`CreateContinueOnTrackCoordinator` |
| 音声入力・サンプル時計 | [LtcAudioMonitor.cs](../../src/TimecodeSyncPlayer/LtcAudioMonitor.cs)、[LtcAudioSampleProcessor.cs](../../src/TimecodeSyncPlayer/LtcAudioSampleProcessor.cs) |
| 同期全体の入口と分岐 | [LtcSyncController.cs](../../src/TimecodeSyncPlayer/LtcSyncController.cs) の `ReceiveFrame` / `ApplySync` / `ApplyCorrection` / `Tick` |
| シーク判断と着地管理 | [TimecodeSyncService.cs](../../src/TimecodeSyncPlayer/TimecodeSyncService.cs)、[SyncDecisionEngine.cs](../../src/TimecodeSyncPlayer/SyncDecisionEngine.cs)、[SeekDecisionGate.cs](../../src/TimecodeSyncPlayer/SeekDecisionGate.cs) |
| 継続再生中の補正 | [SyncCorrectionController.cs](../../src/TimecodeSyncPlayer/SyncCorrectionController.cs) |
| 素材とタイムライン | [PlaylistState.cs](../../src/TimecodeSyncPlayer/PlaylistState.cs)、[PlaylistTrack.cs](../../src/TimecodeSyncPlayer/PlaylistTrack.cs)、[ProjectSerializer.cs](../../src/TimecodeSyncPlayer/ProjectSerializer.cs) |
| C# と native の境界 | [GstPlaybackApi.cs](../../src/TimecodeSyncPlayer/Gst/GstPlaybackApi.cs)、[GstBackendState.cs](../../src/TimecodeSyncPlayer/Gst/GstBackendState.cs)、[tcs_gstreamer.cpp](../../native/gst-shim/src/tcs_gstreamer.cpp) |
| 更新通知とハンドルの寿命 | [RenderSession.cs](../../src/TimecodeSyncPlayer/RenderSession.cs)、[GstRenderUpdateSource.cs](../../src/TimecodeSyncPlayer/Gst/GstRenderUpdateSource.cs) |
| GPU の入力と合成 | [OutputEngine.cs](../../src/TimecodeSyncPlayer/Output/OutputEngine.cs) の `ComposeTick`、[GStreamerSource.cs](../../src/TimecodeSyncPlayer/Output/GStreamerSource.cs)、[ComposeLayer.cs](../../src/TimecodeSyncPlayer/Output/ComposeLayer.cs) |
| スレッド間で渡す表示状態 | [TimelineOutputState.cs](../../src/TimecodeSyncPlayer/Output/TimelineOutputState.cs) |
| GPU 復旧 | [GpuRecoveryPlan.cs](../../src/TimecodeSyncPlayer/Output/GpuRecoveryPlan.cs)、[GpuRecoveryState.cs](../../src/TimecodeSyncPlayer/Output/GpuRecoveryState.cs)、`MainWindow.OnGStreamerRebindRequested` |
| 通常終了 | [ExitCoordinator.cs](../../src/TimecodeSyncPlayer/ExitCoordinator.cs)、[MainWindowResourceDisposer.cs](../../src/TimecodeSyncPlayer/MainWindowResourceDisposer.cs) |
| v0.4.7 の警告と観測 | [DecodeHealthMonitor.cs](../../src/TimecodeSyncPlayer/DecodeHealthMonitor.cs)、[PlaybackActivityLedger.cs](../../src/TimecodeSyncPlayer/PlaybackActivityLedger.cs)、[CodecAdvice.cs](../../src/TimecodeSyncPlayer/CodecAdvice.cs)、[GopScanCache.cs](../../src/TimecodeSyncPlayer/GopScanCache.cs) |

## 用語と読み違えやすい点

| 用語 | このアプリでの意味 |
| --- | --- |
| shim | C# と GStreamer / D3D11 をつなぐ native DLL。プレイヤー操作や画像の貸出 API を提供する |
| mailbox | 最新の表示状態を1件だけ保持する受け渡し口。古い状態をすべて再生するキューではない |
| lease | 画像の貸出権。GPU が使い終わるまで返却しない |
| fence | GPU の処理完了を確認・待機するための目印。受信した画像が使用可能かを判断する |
| generation | ロード・シーク・資源切替などの前後を区別する番号。レンダー通知、shim、キャンバス等にそれぞれの世代がある |
| Held / Freeze | Held は直前に確定した画像の保持。Freeze は Gap 方針に従って保持する画像。`ComposeLayer` が実画像を扱う |

- **software decode でも GPU は必要です。** CPU での復号を選んでも合成・出力は D3D11.4 を使います。
- **通知は画像そのものではありません。** フレーム通知は `RenderSession` を通り、映像は共有リングから GPU worker へ渡ります。
- **GPU worker はトラック選択やシーク判断をしません。** UI 側から渡された表示状態と画像を使います。
- **警告機能は別の観測経路です。** デコード追従警告では、`GstPlaybackApi.Activity` の操作履歴と配信統計などを使い、シークや停止による低下と区別します。
- **実験経路は既定動作と区別します。** 位置フィードバックによる同期判断は `TCS_SYNC_POSITION_FEEDBACK`、GOP からのシーク所要ヒントは `TCS_SEEK_COST_HINT` で切り替えます。

## 更新時の扱い

編集用の正本は `architecture.drawio` です。構成を変更したら、対象バージョン・コミット表記と図を更新してください。
SVG は各ページを書き出した閲覧用スナップショットです。同じファイル名で SVG を書き出せば、`index.html` の表示も更新されます。

より詳細な制約は [ARCHITECTURE.md](../ARCHITECTURE.md) を参照してください。図は正常系の主経路を要約したもので、障害試験や実機確認の代わりにはなりません。
