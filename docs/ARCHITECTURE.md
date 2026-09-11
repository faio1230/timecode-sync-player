# アーキテクチャ

TimecodeSyncPlayer の内部構造について、データフロー・スレッドモデル・主要コンポーネント・
mpv連携やLTC同期の実装上の要点をまとめる。

---

## 1. データフロー

### CPU 出力経路（OutputBackend=Cpu、既定）

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
                              最大1枚の待機画像（コピー・世代・順序番号）
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

### GPU 出力経路（OutputBackend=Gpu）

`OutputBackend=Gpu`では、mpvのSW描画スレッドが作ったスナップショットをGPU workerへ渡し、
D3D11上で固定キャンバスへ合成してから全画面・Spout・プレビューへ配る。UIはタイムライン状態
（世代・Gap・テストカード・キャンバス・配置・位置）を不変レコードのmailboxで渡し、
出力側はクリップやシークの判断を持たない。

```text
mpv描画スレッド ─ RenderedFrameSnapshot ─▶ MpvSnapshotSource（4 slot、GPUアップロード、3枚リング）
                                                          │
GStreamer shim（自前デバイス）─ NT共有3枚＋共有フェンス（値=seq）─▶ GStreamerSource
                                                          │
UIスレッド ─ TimelineOutputState mailbox ───────────────▶ GPU worker（OutputEngine.GPU）
UIスレッド ─ コマンド（全画面HWND、キャンバス、カード、世代）─▶   │
                                                                  ▼
                                        ComposeLayer（配置・Held/Freeze/Black/GapFreeze・カード）
                                                                  │
                                                合成pool 3枚（lease、共有フェンス）
                                        ┌─────────────┬───────────────┐
                                        ▼             ▼               ▼
                                全画面swapchain   Spout worker    プレビュー
                                vblank−marginに   （別デバイス）   960×540へ縮小
                                Present            位相4msで送信   GPU読み戻し
```

- **ソース**: `IVideoSource`契約（世代排除・最新優先・準備不可はNotReady・有限lease・非ブロッキング）。
  mpvはCPU画素をGPUへアップロードし、完了クエリでGPUコピー完了を確認してからリングへ公開する。
  GStreamerはshimが別デバイスでデコードし、3枚の共有リングへコピーして`seq`を共有フェンスで
  Signalする。合成側は描画前に`Context4::Wait(fence, seq)`をGPUキューへ積むだけでCPUは待たない。
- **合成**: 合成画像は常に3枚のpool。読者0の面に書き、GPU完了後に最新として公開する。
  読者はleaseを取り、GPU使用完了まで返さない（読者→書き手の安全）。
- **表示**: DXGIフレーム統計から次のvblankを予測し、`vblank − margin(3ms)`を目標にPresentする。
  届くvblankは飛ばさず、期限の来た合成より表示を優先する。合成tickは`vblank − margin − lead`へ
  slew 0.5ms/tickで整列し、leadは合成時間の実測p99＋1msを基本に上げ急・下げ緩で学習する
  （起動直後3秒、全画面接続/切断・Spout切替・世代変更・ソース接続の直後1秒は学習を除外）。
- **Spout**: 専用workerが同一アダプターLUIDの別`GpuDevice`を持つ。合成poolのNT共有サーフェスを
  `OpenSharedResource1`で開き、共有フェンスをGPUキューで待ってから保持テクスチャへコピーし、
  アクセスmutexを要求8ms（期限＝次回予定）で取得して`SendTexture`する。取得失敗は保持画像の再送で、
  資源は無効化しない。
- **プレビュー**: 960×540へ縮小したstagingをGPU workerが読み戻し、UIへ配列を渡す
  （全画面中10Hz、それ以外30Hz）。
- **終了**: 全画面用の子HWNDの破棄より先にswapchainを切断する。

`OutputBackend=Cpu`の経路は設定で選択でき、従来どおり残す。

## 2. スレッドモデル

`OutputBackend=Gpu`時のスレッド間の受け渡しは次のとおり。各workerは互いを同期待ちせず、
不変レコードのmailbox・有限queue・共有フェンスだけで受け渡す。

```text
UIスレッド ─ TimelineOutputState（mailbox）／コマンド（有限queue）─▶ GPU worker
    ▲                                                                  │
    │ プレビュー配列・GPU状態通知（Dispatcher.BeginInvoke、待たない）     │
    └──────────────────────────────────────────────────────────────────┘
mpv描画スレッド ─ スナップショット（mailbox）─────────────────────▶ GPU worker
GPU worker ─ 合成pool（NT共有＋共有フェンス）───────────────────▶ Spout worker（別デバイス）
GStreamerストリーミングスレッド ─ 共有リング＋共有フェンス（別デバイス）─▶ GPU worker
```

- **UIスレッド（WPFメインスレッド）**: シークコマンドの発行、`WriteableBitmap`への描画更新、
  ユーザー操作（プレイリスト編集・再生制御）の処理を担う。GPU経路ではさらに、タイムライン状態の
  mailbox公開、全画面HWND・キャンバス・カード・世代のコマンド発行、GPU状態通知とプレビュー画像の
  反映を担う。
- **専用レンダースレッド**: mpvレンダーコンテキストの作成・更新・描画・解放を直列に実行する。
  ターゲット時刻まで待機するmpv描画処理をUIスレッドから分離し、再生中もユーザー操作と
  UI Automation要求へ応答できるようにする。
- **オーディオスレッド（NAudio WASAPIコールバック）**: `LtcAudioMonitor`がこのスレッド上で
  PCMサンプルを受け取り、LTCデコードを行う。UIスレッドとは別スレッドで動作するため、
  デコード結果をUIスレッドに引き渡す際はスレッドセーフな手段（Dispatcher経由など）を使う。
- **mpv内部スレッド**: mpvのレンダー更新コールバックはmpv自身のスレッドから呼び出される。
  コールバックは専用レンダースレッドへ直接処理を予約する。UIの位置取得や操作完了を待たず、
  FRAME要求ごとに描画する。画像をコピーしてからUIへ公開を予約する。1つの予約で1回だけ描画し、
  連続する更新が明示的な再描画・Freeze取得・終了処理を待たせ続けないようにする。
- **GPU worker（`OutputEngine.GPU`、`OutputBackend=Gpu`時のみ）**: 合成pool・合成・全画面
  swapchain・Present・vblank統計・プレビュー読み戻し・ソースleaseを所有する。D3D11のimmediate
  contextはこのスレッドのもので、他スレッドが触る資源は共有フェンスで同期する。UIからは
  コマンドqueueと不変レコードのmailboxで受け取り、UIを同期待ちしない。
- **Spout worker（`OutputEngine.Spout`）**: 同じアダプターLUIDの別`GpuDevice`・別immediate
  contextを持つ。合成poolの画像を開いた共有サーフェスから保持テクスチャへGPUコピーし、Spoutの
  アクセスmutexを短時間待って送信する。合成workerとは別スレッドで、互いを待たない。
- **GStreamerのストリーミングスレッド**: shimが作った自前デバイスのcontextを使う。合成デバイス
  とは別で、受け渡しは共有リングと共有フェンスのみ。合成側の`TimelineOutputState`やGPU workerの
  contextには触れない。

## 3. 主要コンポーネント

| コンポーネント | 役割 |
|---|---|
| `MainWindow.xaml.cs` | メインウィンドウのコードビハインド。UIイベントとI/O境界を制御クラスへ接続する |
| `LtcSyncController.cs` | LTCフレーム受信・信号断・表示状態とSingle/Continue/Gapへの分岐を統合し、本番と統合テストで共用する |
| `RenderSession.cs` | render context・専用スレッド・callback・パラメータ・ピクセルバッファ・世代・公開ゲートを所有し、描画の開始から停止までを管理する |
| `FrameRenderer.cs` | `WriteableBitmap`の保持とmpvから受け取ったフレームバッファの描画を担当 |
| `RenderFrameWorker.cs` | 描画サイズの判定、mpv描画、UI反映用フレーム情報の受け渡しを直列化する |
| `RenderedFrameSnapshot.cs` | プールから借りた画像コピーを保持し、待機画像を最大1枚に制限する。UIが使用中の画像を上書きしない |
| `GapFreezeCaptureOperation.cs` | 画像コピー成功と現在の取得試行を確認してからFreeze確定状態へ進める |
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
| `Contracts/IVideoSource.cs` | 出力側の映像ソース契約。世代排除・最新優先・有限lease・非ブロッキングを定める |
| `Output/OutputEngine.cs` | GPU出力層。GPU workerとSpout workerを所有し、pool・全画面swapchain・合成・プレビュー・デバイス消失復旧・終了を管理する |
| `Output/ComposeLayer.cs` | 固定キャンバスへの合成。Held（最後に確定した画像）・Freeze・Black・GapFreeze・テストカードと、配置の選択規則（`ComposeLayerPolicy`）を持つ |
| `Output/MpvSnapshotSource.cs` | mpvのスナップショット（BGRA配列）をGPU workerでアップロードし、3枚リングで合成層へ供給する`IVideoSource` |
| `Output/GStreamerSource.cs` | tcs_gstreamer.dllのリースAPIを`IVideoSource`へ適合する。共有リング3枚＋共有フェンスを合成デバイス上で一度だけ開く |
| `Output/SpoutSender.cs` | Spout送信workerの送信機。別デバイスで保持テクスチャへコピーし、アクセスmutexを要求8msで取得して送信する |
| `Output/TimelineOutputState.cs` | UIがmailboxでGPU workerへ渡すタイムライン状態（世代・Gap・テストカード・キャンバス・配置・位置） |
| `Output/VblankDisplayGate.cs`, `Output/SwapchainTarget.cs` | DXGI統計からのvblank予測・表示判断と、子HWNDへのswapchain Present |
| `Output/ComposeLeadController.cs`, `Output/ComposeAlignGate.cs` | 合成leadの学習と、合成tickの位相をvblankへ整列する補正 |
| `Output/GpuRecoveryState.cs`, `Output/GpuRecoveryPlan.cs` | デバイス消失の状態機械（自動復旧は1回）と復旧手順の順序 |
| `ExitCoordinator.cs`, `ExitTransitions.cs` | 終了確認ダイアログの状態機械（Running→Confirming→ShuttingDown→Exited、Confirming/ShuttingDownからForcing） |
| `MainWindowResourceDisposer.cs` | 終了手順を定められた順序で段階実行し、例外を集約する |

## 4. mpv SW render の要点

- **`vo=libmpv`が必須**: これを設定しないとmpvが自前のウィンドウを開いてしまう。
  `vo=null`は破棄用VOのため、再生自体は進んでもフレームが`WriteableBitmap`側に届かない。
- **現在のSW描画では`hwdec=no`を既定とする**: ハードウェアコピー方式のシーク遅延が同期追従を
  妨げた実測に基づく。CPU負荷と本番素材の性能確認が必要。比較は`SYNC-ACCURACY.md`を参照。
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
- **公開順序を逆転させない**: 明示的な再描画で新しい画像を反映した後は、待機していた古い画像を
  順序番号で破棄する。新しい画像がまだ描画されたに過ぎない場合は、UI使用中の画像を妨げない。
- **Freezeはコピー成功後に確定する**: nativeの`seeking=no`、`pause=yes`、パスと位置を確認して
  再描画し、await後にも確認する。画像を最終用バッファへコピーできた場合だけキャッシュを確定する。
  最終画像からさらにframe-stepは送らない。停止後の通知が来なくてもタイマーで再試行・公開し、
  取得中の操作・Gap再進入・失敗・タイムアウトを確定済みキャッシュに見せかけない。
  確定待ちは`Hold`で既に公開した画像を保ち、以前のクリップのFrozenバッファへ切り替えない。
  キャッシュの画素・サイズとhandlerの確定情報がそろった場合だけ最終画像を再公開する。
  動画長が不明な場合も確定情報を作らず、現在の画像を保持する。
- **描画資源の所有者は`RenderSession`**: 内部でバッファ・描画helper・`FrameRenderer`を生成する。
  Windowは`BitmapChanged`をプレビューとFullscreenへ接続し、Gap判断とUI更新を受け持つ。
  native専用バッファとUI公開用バッファを分離し、画像のコピーだけを受け渡す。
  UIバッファの通常/Black/Freeze公開はsession内の同じゲートを通す。

## 5. LTC同期の要点

- **nativeシーク中の位置を完了判定に使わない**: `time-pos`が要求先の値を返していても、
  `seeking=yes`なら同じクリップのロード安定判定・シーク完了判定を保留する。
  別クリップへの変更やGap退出は、新しい要求で置き換えられる。

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
- **GPU出力の管理テスト**: 合成poolの読み書き規則、vblank判断、lead学習、キャンバス世代の
  破棄規則、GStreamerリングのリース規則、復旧手順、終了状態遷移はGPU非依存の純粋規則として
  xUnitで固定する。実動画のスループット・Spout受信・デバイス消失は実機で確認する
  （[verification-checklist.md](verification-checklist.md)）。

## 7. 終了時の所有権と失敗処理

Windowが終了順序を管理し、`RenderSession`が描画資源を所有する。Spout・LTC入力はWindowの
終了処理で解放し、sessionはSpoutを借用して公開する。バッファや描画helperをDIへ別登録しない。
`OutputBackend=Gpu`ではさらに`OutputEngine`がD3D11デバイス・合成pool・全画面swapchain・
Spout worker・ソースleaseを所有する。

終了は`ExitCoordinator`（状態機械は`ExitTransitions`）が制御する。×／Alt+F4では確認
ダイアログを表示し、その間も再生・LTC・出力は継続する。キャンセルでRunningへ戻り、通常終了は
`MainWindowResourceDisposer`の5段階（新規受付停止 → mpv／GStreamer停止 → 出力停止 →
全画面終了 → 資源解放）を定められた順序で1つずつ実行する。50ms以上ブロックし得る段階はUIスレッド外で
実行し、ダイアログの進捗表示を更新する。強制終了は確認なしで`Environment.Exit(2)`を呼び、
2秒の番人スレッドでプロセスをKillする。

通常は描画停止 → Fullscreen終了 → timer停止 → render context解放 → mpv終了 → LTC終了 →
Spout終了 → timeline終了 → sessionのバッファ・スレッド解放の順に処理する。Gpu経路では
同じ順序（新規受付停止 → `RenderSession.Stop` → `OutputEngine.Stop`（Spout worker join・
全lease返却）→ 全画面閉 → mpv／GStreamer終了 → `OutputEngine.Dispose` → CPU Spout →
バッファ）で、leaseはGStreamer shimのdestroyより先に返す。
停止ではnative workerの完了だけを待つ。UIへ戻る非同期パイプラインをUIスレッド上で待たず、
後着の公開処理は停止フラグで無効化する。

`MainWindowResourceDisposer`は各段階の例外を収集し、独立した後処理を続けてから
`AggregateException`として呼び出し側へ通知する。render contextの解放に失敗した場合は、
そのcontextが参照し得るmpv・callback・バッファ・描画スレッドを保持し、LTCやtimelineなどの
独立した後処理を試行する。Window終了では失敗したnative解放を自動再試行しない。
安全に解放できない資源はプロセス終了まで残るため、終了エラーのログを確認すること。
LTC通知はDispatcherへ渡す前と実行時の両方で終了状態を確認し、終了後のtimer tickも無視する。
native worker自体が戻らない場合は終了待ちが続く。タイムアウトで使用中の資源を解放する動作は行わない。

GPUデバイス消失（`GpuRecoveryState`）は`GpuDeviceLostException`だけを入力とする。プロセス
寿命で自動復旧を1回だけ試し、失敗または再発時は手動再試行（`BtnGpuRetry`）を待つ。復旧手順は
`GpuRecoveryPlan`の順序（Spout worker停止 → lease返却・合成資源破棄 → デバイス再作成 →
合成資源再構築 → 全画面swapchain再作成 → Spout再初期化 → ソース再接続）で実行する。
GPU完了待ちの期限超過はfaultとして新規処理を止めるが、使用中資源の解放理由にはしない。
