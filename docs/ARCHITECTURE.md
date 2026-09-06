# アーキテクチャ

TimecodeSyncPlayer の内部構造について、データフロー・スレッドモデル・主要コンポーネント・
mpv連携やLTC同期の実装上の要点をまとめる。

---

## 1. データフロー

```
[マイク/ライン入力] → NAudio WASAPI → LtcAudioMonitor
                                             ↓
                                       LtcDecoder（純C#）
                                             ↓ Dispatcher
                              LtcSyncController（UIスレッド）
                              フレーム診断・信号断・モード分岐
                                             ↓
                              既存Coordinator / TimecodeSyncService
                                             ↓
                                   SyncDecisionEngine.Decide()
                                             ↓ （Seek / None）
                              MainWindowのI/O境界 → mpv_command("seek")
                                             ↓
                              RenderSession / mpv SW render context
                              （専用レンダースレッド）
                                             ↓
                              UIスレッド（直列公開処理）
                                      ↙              ↘
                              WriteableBitmap     SpoutOutput.SendFrame()
                                      ↓                       ↓
                              Image コントロール          SpoutDX.dll
```

LTC音声はNAudioのWASAPIループバック/入力デバイスから取得し、`LtcAudioMonitor` がPCMサンプルを
`LtcDecoder` に渡してタイムコード（時:分:秒:フレーム）を復元する。音声受信時に採った時刻と
フレームをDispatcher経由で `LtcSyncController` へ渡す。controllerはフレーム診断・信号断の
抑止条件を評価し、Single/Continue/Gapに応じた既存Coordinatorを実行する。
`SyncDecisionEngine` は現在の再生位置との差分からシークすべきかどうかを判定する。
シークが必要と判定された場合、UIスレッド上でmpvへ`seek`コマンドを発行する。mpvの
ソフトウェアレンダリングAPIは専用レンダースレッドで実行し、描画結果をUIスレッド上で
`WriteableBitmap`へ転送する。同じUIスレッド上の直列公開処理からSpout2出力にも渡す。

## 2. スレッドモデル

- **UIスレッド（WPFメインスレッド）**: シークコマンドの発行、`WriteableBitmap`への描画更新、
  ユーザー操作（プレイリスト編集・再生制御）の処理を担う。
- **専用レンダースレッド**: mpvレンダーコンテキストの作成・更新・描画・解放を直列に実行する。
  ターゲット時刻まで待機するmpv描画処理をUIスレッドから分離し、再生中もユーザー操作と
  UI Automation要求へ応答できるようにする。
- **オーディオスレッド（NAudio WASAPIコールバック）**: `LtcAudioMonitor`がこのスレッド上で
  PCMサンプルを受け取り、LTCデコードを行う。UIスレッドとは別スレッドで動作するため、
  デコード結果をUIスレッドに引き渡す際はスレッドセーフな手段（Dispatcher経由など）を使う。
- **mpv内部スレッド**: mpvのレンダー更新コールバックはmpv自身のスレッドから呼び出される。
  コールバックはUI Dispatcherへ更新通知だけを送り、実際のmpv描画は専用レンダースレッドへ
  委譲する。完了したフレームの`WriteableBitmap`反映だけをUIスレッドで行う。

## 3. 主要コンポーネント

| コンポーネント | 役割 |
|---|---|
| `MainWindow.xaml.cs` | メインウィンドウのコードビハインド。UIイベントとI/O境界を制御クラスへ接続する |
| `LtcSyncController.cs` | LTCフレーム受信・信号断・表示状態とSingle/Continue/Gapへの分岐を統合し、本番と統合テストで共用する |
| `RenderSession.cs` | render context・専用スレッド・callback・パラメータ・ピクセルバッファ・世代・公開ゲートを所有し、描画の開始から停止までを管理する |
| `FrameRenderer.cs` | `WriteableBitmap`の保持とmpvから受け取ったフレームバッファの描画を担当 |
| `RenderFrameWorker.cs` | 描画サイズの判定、mpv描画、UI反映用フレーム情報の受け渡しを直列化する |
| `RenderThreadExecutor.cs` | mpvレンダーAPIを単一の専用スレッド上で実行する |
| `RenderFramePipelineGate.cs` | 通常・Black・Freezeのフレーム公開と共有バッファ操作を直列化する |
| `LtcDecoder.cs` | libltcに依存しない純C#実装のLTCデコーダ。PCMサンプル列からタイムコードを復元する |
| `LtcAudioMonitor.cs` | NAudio WASAPIで音声デバイスを監視し、PCMサンプルを`LtcDecoder`に供給する |
| `SyncDecisionEngine.cs` | LTC秒と現在の再生位置からシークすべきかどうかを判定するロジック |
| `TimecodeSyncService.cs` | `SyncDecisionEngine`の判定結果とシーク抑制（デバウンス）・ファイルロード状態を統合管理する |
| `GapFreezeHandler.cs` | トラック間・終端後のギャップ状態を管理するステートマシン（Freeze/Black/通常再生の遷移） |
| `PlaylistState.cs` | プレイリストの内部状態（トラック一覧・現在位置など）を保持する |
| `SpoutOutput.cs` | SpoutDXを介したフレーム送信のライフサイクル管理 |
| `ViewModels/MainViewModel.cs` | Playlist・Sync・Playerの各ViewModelを集約するルートViewModel |
| `ViewModels/PlaylistViewModel.cs` | プレイリスト操作コマンドとプレイリストの表示状態を管理 |
| `ViewModels/SyncViewModel.cs` | LTC開始/停止、同期トグル、同期状態の管理 |
| `ViewModels/PlayerViewModel.cs` | 再生状態（再生/一時停止など）と再生系コマンドの管理 |

## 4. mpv SW render の要点

- **`vo=libmpv`が必須**: これを設定しないとmpvが自前のウィンドウを開いてしまう。
  `vo=null`は破棄用VOのため、再生自体は進んでもフレームが`WriteableBitmap`側に届かない。
- **SW render param定数は17〜20**: `MPV_RENDER_PARAM_SW_SIZE=17`, `SW_FORMAT=18`,
  `SW_STRIDE=19`, `SW_POINTER=20`。古いドキュメントでは6〜9と記載されている場合があるが、
  これは誤りなので注意。
- **`MpvRenderParam`は明示的パディングが必要**:
  ```csharp
  struct MpvRenderParam { int Type; int _padding; IntPtr Data; }  // 16バイト
  ```
  `_padding`フィールドを省略するとx64 ABI上でアライメントがずれ、クラッシュの原因になる。
- **レンダー更新コールバックのデリゲートはフィールドで保持する**:
  `mpv_render_context_set_update_callback`に渡したデリゲートをローカル変数のみで保持すると、
  GCに回収されてクラッシュする。`RenderSession`がコンテキストの解放成功までフィールドで保持する。
- **mpvレンダーAPIは単一の専用スレッドで直列実行する**: `mpv_render_context_render`は
  ターゲット時刻まで待機することがあるためUIスレッドでは呼ばない。レンダーコンテキストの
  作成・更新・描画・解放も同じ専用スレッドに揃え、同時呼び出しを避ける。
- **非同期描画の後着を無効化する**: トラック変更時はレンダー世代を進め、await完了後の旧世代
  フレームを表示・Spout・Freezeキャッシュへ公開しない。Black/Freezeの遅延描画もゲート取得時に
  現在のGap判断を再確認し、Gap退出後の古い副作用を破棄する。レンダーcallbackの例外はUIの
  未処理例外にせずログ境界で処理し、schedulerの完了処理は必ず実行する。
- **描画資源の所有者は`RenderSession`**: 内部でバッファ・描画helper・`FrameRenderer`を生成する。
  Windowは`BitmapChanged`をプレビューとFullscreenへ接続し、Gap判断とUI更新を受け持つ。
  共有バッファの通常/Black/Freeze公開はsession内の同じゲートを通す。

## 5. LTC同期の要点

- **時間境界は差し替え可能な時計で検証する**: `TimecodeSyncService` と `GapFreezeHandler` は
  `TimeProvider` を受け取り、省略時は `TimeProvider.System` を使う。判定は従来どおりUTC時刻の
  差分で、シークのデバウンスは250ms未満、ロードのタイムアウトは5秒超、Freeze取得の
  タイムアウトは3秒超。テストでは時計を固定・前進させ、直前・同時・直後を実時間の待機なしで検証する。
- **許容誤差はfpsから自動計算する**: シークすべきかどうかを判定する許容誤差（トレランス）は、
  対象の映像/タイムコードのfpsに応じて自動的に算出される。フレームレートが異なる素材が
  混在してもフレーム単位の精度を維持できるようにするため。
- **シークは保留（2秒タイムアウト）を伴う**: シーク要求は即座に反映されるとは限らず、
  一定時間（2秒）を上限として保留状態を管理する。タイムアウトした場合は保留を解除し、
  次の判定サイクルに委ねる。
- **フレーム診断はNormal/Jump/Reverse/Duplicate/Invalidに分類される**: 連続するLTCフレームの
  関係を分類し、単発的なJump（一時的な外乱によるフレーム飛び）は誤判定を避けるために
  単発除外（1回だけの逸脱では同期判定に影響させない）する仕組みを持つ。

## 6. テスト戦略

テストは大きく2種類に分離されている。

- **非E2Eテスト**: mpvやオーディオデバイスなどの実ネイティブDLL・実デバイスに依存せず、
  フェイク実装（Contracts配下のインターフェースに対するテストダブル）経由でロジックを検証する。
  CI環境やネイティブDLLがない環境でも実行できる。
- **同期の統合テスト**: `SyncScenarioHarness` は本番と同じ `LtcSyncController` を呼ぶ。
  受信済みフレームからの制御を共用し、mpv・表示の境界を記録する。生LTCフレームの診断と
  同期抑止、停止通知の再入、デバイス列挙エラー表示の維持はcontrollerの振る舞いで検証する。
- **E2Eテスト（FlaUI）**: FlaUIを用いて実際にアプリケーションを起動し、UI操作を通じて
  エンドツーエンドの挙動を検証する。実行には`scripts/get-mpv.ps1`で導入する
  `libmpv-2.dll`（または互換用`mpv-2.dll`）などのネイティブDLLと実機環境が必要。

## 7. 終了時の所有権と失敗処理

Windowが終了順序を管理し、`RenderSession`が描画資源を所有する。Spout・LTC入力はWindowの
終了処理で解放し、sessionはSpoutを借用して公開する。バッファや描画helperをDIへ別登録しない。

通常は描画停止 → Fullscreen終了 → timer停止 → render context解放 → mpv終了 → LTC終了 →
Spout終了 → timeline終了 → sessionのバッファ・スレッド解放の順に処理する。
停止ではnative workerの完了だけを待つ。UIへ戻る非同期パイプラインをUIスレッド上で待たず、
後着の公開処理は停止フラグで無効化する。

`MainWindowResourceDisposer`は各段階の例外を収集し、独立した後処理を続けてから
`AggregateException`として呼び出し側へ通知する。render contextの解放に失敗した場合は、
そのcontextが参照し得るmpv・callback・バッファ・描画スレッドを保持し、LTCやtimelineなどの
独立した後処理を試行する。Window終了では失敗したnative解放を自動再試行しない。
安全に解放できない資源はプロセス終了まで残るため、終了エラーのログを確認すること。
LTC通知はDispatcherへ渡す前と実行時の両方で終了状態を確認し、終了後のtimer tickも無視する。
native worker自体が戻らない場合は終了待ちが続く。タイムアウトで使用中の資源を解放する動作は行わない。
