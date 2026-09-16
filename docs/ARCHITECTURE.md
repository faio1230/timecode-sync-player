# アーキテクチャ

TimecodeSyncPlayer の内部構造について、データフロー・スレッドモデル・主要コンポーネント・
GStreamer 連携やLTC同期の実装上の要点をまとめる。

---

## 1. データフロー

### CPU 出力経路（OutputBackend=Cpu。**段 3 で除去予定**。記述は除去まで現状を残す）

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
                              MainWindowのI/O境界 → IMpvApi.CommandString("seek")
                                             ↓
                                   GstMpvApiAdapter → tcs_gstreamer.dll
                                             ↓
                              RenderSession / レンダーコンテキスト
                              （GstMpvRenderApiAdapter、専用レンダースレッド）
                                             ↓
                              最大1枚の待機画像（コピー・世代・順序番号）
                                             ↓
                              UIスレッド（直列公開処理）
                                      ↙              ↘
                              WriteableBitmap     ISpoutOutput.SendFrame()
                                      ↓                       ↓
                              Image コントロール         SpoutDX / shim 経由
```

LTC音声はNAudioのWASAPIループバック/入力デバイスから取得し、`LtcAudioMonitor` がPCMサンプルを
`LtcDecoder` に渡してタイムコード（時:分:秒:フレーム）を復元する。音声受信時に採った時刻と
フレームをDispatcher経由で `LtcSyncController` へ渡す。controllerはフレーム診断・信号断の
抑止条件を評価し、Single/Continue/Gapに応じた既存Coordinatorを実行する。
`SyncDecisionEngine` は現在の再生位置との差分からシークすべきかどうかを判定する。
シークが必要と判定された場合、UIスレッド上で再生API（`IMpvApi` 越し。段 4 で型付き API に
置換予定）へシークを発行し、`GstMpvApiAdapter` が shim の `tcs_player_seek` を呼ぶ。
CPU 経路では `GstMpvRenderApiAdapter` が shim のリース画像を専用レンダースレッドで
bgr0 バッファへコピーし、描画結果をUIスレッド上で `WriteableBitmap` へ転送する。
同じUIスレッド上の直列公開処理からSpout出力にも渡す。

### GPU 出力経路（OutputBackend=Gpu、既定）

`OutputBackend=Gpu`では、GStreamer shimが自前デバイスでデコードしたフレームを共有リングで
GPU workerへ渡し、D3D11上で固定キャンバスへ合成してから全画面・Spout・プレビューへ配る。
UIはタイムライン状態（世代・Gap・テストカード・キャンバス・配置・位置）を不変レコードの
mailboxで渡し、出力側はクリップやシークの判断を持たない。

```text
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
  GStreamerではshimが別デバイスでデコードし、3枚の共有リングへコピーして`seq`を共有フェンスで
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

`OutputBackend=Cpu`の経路は設定で選択できるが、**段 3（CPU 合成の除去）で削除予定**。除去まで上の記述を現状として残す。

## 2. スレッドモデル

`OutputBackend=Gpu`時のスレッド間の受け渡しは次のとおり。各workerは互いを同期待ちせず、
不変レコードのmailbox・有限queue・共有フェンスだけで受け渡す。

```text
UIスレッド ─ TimelineOutputState（mailbox）／コマンド（有限queue）─▶ GPU worker
    ▲                                                                  │
    │ プレビュー配列・GPU状態通知（Dispatcher.BeginInvoke、待たない）     │
    └──────────────────────────────────────────────────────────────────┘
GPU worker ─ 合成pool（NT共有＋共有フェンス）───────────────────▶ Spout worker（別デバイス）
GStreamerストリーミングスレッド ─ 共有リング＋共有フェンス（別デバイス）─▶ GPU worker
GStreamer shim ─ フレーム通知（コールバック）─────────▶ RenderSession ─▶ UIスレッド
```

- **UIスレッド（WPFメインスレッド）**: シークコマンドの発行、`WriteableBitmap`への描画更新、
  ユーザー操作（プレイリスト編集・再生制御）の処理を担う。GPU経路ではさらに、タイムライン状態の
  mailbox公開、全画面HWND・キャンバス・カード・世代のコマンド発行、GPU状態通知とプレビュー画像の
  反映を担う。
- **専用レンダースレッド（`RenderSession`）**: レンダーコンテキストの作成・更新・解放を直列に実行する。
  CPU 経路では `GstMpvRenderApiAdapter.RenderContextRender` のリース画像コピーもこのスレッドで実行し、
  UIスレッドから分離する（段 3 後はフレーム通知の駆動と寿命管理のみ）。
- **オーディオスレッド（NAudio WASAPIコールバック）**: `LtcAudioMonitor`がこのスレッド上で
  PCMサンプルを受け取り、LTCデコードを行う。UIスレッドとは別スレッドで動作するため、
  デコード結果をUIスレッドに引き渡す際はスレッドセーフな手段（Dispatcher経由など）を使う。
- **GStreamer shim のコールバック**: shimのフレーム通知は shim が作ったデバイスのスレッドから
  呼び出される。`GstBackendState` がデリゲートの寿命を保持して `RenderSession` へ転送し、
  UIはそれを `Dispatcher.BeginInvoke` で処理する。コールバック内でUIの位置取得や操作完了を
  待たない。画像のコピーとUIへの公開予約は別スレッドで行い、連続する更新が明示的な再描画・
  Freeze取得・終了処理を待たせ続けないようにする。
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
| `RenderSession.cs` | レンダーコンテキスト・専用スレッド・callback・パラメータ・ピクセルバッファ・世代・公開ゲートを所有し、CPU 経路の描画開始から停止までを管理する（GPU 経路ではフレーム通知の駆動と寿命管理。CPU 側のスナップショット経路は段 3 で除去予定） |
| `FrameRenderer.cs` | `WriteableBitmap`の保持と、レンダー API から受け取ったフレームバッファの描画を担当（CPU 経路。段 3 で除去予定） |
| `RenderFrameWorker.cs` | 描画サイズの判定、レンダー処理、UI反映用フレーム情報の受け渡しを直列化する（CPU 経路。段 3 で除去予定） |
| `RenderedFrameSnapshot.cs` | プールから借りた画像コピーを保持し、待機画像を最大1枚に制限する。UIが使用中の画像を上書きしない（CPU 経路。段 3 で除去予定） |
| `GapFreezeCaptureOperation.cs` | 画像コピー成功と現在の取得試行を確認してからFreeze確定状態へ進める |
| `RenderThreadExecutor.cs` | レンダーAPIを単一の専用スレッド上で実行する |
| `RenderFramePipelineGate.cs` | 通常・Black・Freezeのフレーム公開と共有バッファ操作を直列化する（CPU 経路。段 3 で除去予定） |
| `LtcDecoder.cs` | libltcに依存しない純C#実装のLTCデコーダ。PCMサンプル列からタイムコードを復元する |
| `LtcAudioMonitor.cs` | NAudio WASAPIで音声デバイスを監視し、PCMサンプルを`LtcDecoder`に供給する |
| `SyncDecisionEngine.cs` | LTC秒と現在の再生位置からシークすべきかどうかを判定するロジック |
| `TimecodeSyncService.cs` | `SyncDecisionEngine`の判定結果とシーク抑制（デバウンス）・ファイルロード状態を統合管理する |
| `GapFreezeHandler.cs` | トラック間・終端後のギャップ状態を管理するステートマシン（Freeze/Black/通常再生の遷移） |
| `PlaylistState.cs` | プレイリストの内部状態（トラック一覧・現在位置など）を保持する |
| `GstSpoutOutput.cs` | shim 所有の spoutDX sender を使う`ISpoutOutput`。通常フレームは GPU テクスチャ、Freeze/Black は CPU 画像を送る |
| `ViewModels/MainViewModel.cs` | Playlist・Sync・Playerの各ViewModelを集約するルートViewModel |
| `ViewModels/PlaylistViewModel.cs` | プレイリスト操作コマンドとプレイリストの表示状態を管理 |
| `ViewModels/SyncViewModel.cs` | LTC開始/停止、同期トグル、同期状態の管理 |
| `ViewModels/PlayerViewModel.cs` | 再生状態（再生/一時停止など）と再生系コマンドの管理 |
| `Contracts/IVideoSource.cs` | 出力側の映像ソース契約。世代排除・最新優先・有限lease・非ブロッキングを定める |
| `Output/OutputEngine.cs` | GPU出力層。GPU workerとSpout workerを所有し、pool・全画面swapchain・合成・プレビュー・デバイス消失復旧・終了を管理する |
| `Output/ComposeLayer.cs` | 固定キャンバスへの合成。Held（最後に確定した画像）・Freeze・Black・GapFreeze・テストカードと、配置の選択規則（`ComposeLayerPolicy`）を持つ |
| `Gst/GstBackendState.cs` | shim（`tcs_gstreamer.dll`）のプレイヤーハンドル・pause ミラー・フレーム通知デリゲートの寿命を所有する |
| `Gst/GstNativeApi.cs` / `Gst/IGstNativeApi.cs` | shim の C ABI（`tcs_player_*`）の薄いラッパ。テストでは fake を注入する |
| `Gst/GstMpvApiAdapter.cs` / `Gst/GstCommandTranslator.cs` | 再生操作の境界。`IMpvApi` の文字列コマンド／プロパティを shim 呼び出しへ翻訳する（`loadfile` / `seek` / `stop` / `pause` / `volume` / `mute` / `speed` と取得系のみ。他は no-op。段 4 で型付き API に置換予定） |
| `Gst/GstMpvRenderApiAdapter.cs` | `IMpvRenderApi` の GStreamer 実装。SW レンダー互換の `RenderParam` を解釈し、CPU 経路ではリース画像を bgr0 へコピーする（コピーは段 3 で除去予定） |
| `Output/GStreamerSource.cs` | tcs_gstreamer.dllのリースAPIを`IVideoSource`へ適合する。共有リング3枚＋共有フェンスを合成デバイス上で一度だけ開く |
| `Output/SpoutSender.cs` | Spout送信workerの送信機。別デバイスで保持テクスチャへコピーし、アクセスmutexを要求8msで取得して送信する |
| `Output/TimelineOutputState.cs` | UIがmailboxでGPU workerへ渡すタイムライン状態（世代・Gap・テストカード・キャンバス・配置・位置） |
| `Output/VblankDisplayGate.cs`, `Output/SwapchainTarget.cs` | DXGI統計からのvblank予測・表示判断と、子HWNDへのswapchain Present |
| `Output/ComposeLeadController.cs`, `Output/ComposeAlignGate.cs` | 合成leadの学習と、合成tickの位相をvblankへ整列する補正 |
| `Output/GpuRecoveryState.cs`, `Output/GpuRecoveryPlan.cs` | デバイス消失の状態機械（自動復旧は1回）と復旧手順の順序 |
| `ExitCoordinator.cs`, `ExitTransitions.cs` | 終了確認ダイアログの状態機械（Running→Confirming→ShuttingDown→Exited、Confirming/ShuttingDownからForcing） |
| `MainWindowResourceDisposer.cs` | 終了手順を定められた順序で段階実行し、例外を集約する |

## 4. GStreamer shim 連携とレンダー境界の要点

- **再生は shim（`tcs_gstreamer.dll`）が唯一のバックエンド**: mpv の再生経路は段 2 で削除済み。
  shim は自前の D3D11 デバイスでデコードし、NT 共有の 3 枚リングと共有フェンスで合成層へ渡す。
  合成デバイス（`OutputEngine` のもの）とは別で、context は共有しない。
- **再生操作は現状文字列の翻訳で行われている**: `GstMpvApiAdapter` が `loadfile` / `seek` / `stop` /
  `frame-step` と `pause` / `volume` / `mute` / `speed` / `time-pos` / `duration` / `container-fps` /
  `path` / `width` / `height` / `video-codec` / `seeking` を解釈し、それ以外は黙って no-op（または
  空・-1）を返す。既知の穴（相対シークのカルチャ依存書式、shim のシーク失敗でも 0 を返す）と
  置き換え案は [V04-STAGE4-TYPED-API-SURVEY-2026-09-16.md](V04-STAGE4-TYPED-API-SURVEY-2026-09-16.md)。
- **`RenderParam` は明示的パディングが必要**:
  ```csharp
  struct RenderParam { int Type; int _padding; IntPtr Data; }  // 16バイト
  ```
  `_padding`フィールドを省略するとx64 ABI上でアライメントがずれ、クラッシュの原因になる。
  SW レンダー param の定数は 17〜20（`SW_SIZE=17`, `SW_FORMAT=18`, `SW_STRIDE=19`, `SW_POINTER=20`）で、
  `GstMpvRenderApiAdapter` が同じ番号を公開して `RenderFrameParameterBuilder` の配列を解釈する
  （CPU 経路のコピーは段 3 で除去予定）。
- **更新コールバックのデリゲートはフィールドで保持する**: shim へ渡したコールバックをローカル変数
  のみで保持すると GC に回収されてクラッシュする。`GstBackendState` は `_thunk` を、
  `RenderSession` は `_updateCallback` をコンテキストの解放成功まで保持する。
- **レンダーAPIは単一の専用スレッドで直列実行する**: レンダーコンテキストの作成・更新・解放
  （CPU 経路では描画コピーも）を同じ専用スレッドに揃え、同時呼び出しを避ける。
- **非同期描画の後着を無効化する**（CPU 経路の規則。段 3 まで）: トラック変更時はレンダー世代を
  進め、await完了後の旧世代フレームを表示・Spout・Freezeキャッシュへ公開しない。Black/Freezeの
  遅延描画もゲート取得時に現在のGap判断を再確認し、Gap退出後の古い副作用を破棄する。
  レンダーcallbackの例外はUIの未処理例外にせずログ境界で処理し、schedulerの完了処理は必ず実行する。
- **公開順序を逆転させない**（CPU 経路。段 3 まで）: 明示的な再描画で新しい画像を反映した後は、
  待機していた古い画像を順序番号で破棄する。新しい画像がまだ描画されたにすぎない場合は、
  UI使用中の画像を妨げない。
- **Freezeはコピー成功後に確定する**: nativeの`seeking=no`、`pause=yes`、パスと位置を確認して
  再描画し、await後にも確認する。画像を最終用バッファへコピーできた場合だけキャッシュを確定する。
  最終画像からさらにframe-stepは送らない。停止後の通知が来なくてもタイマーで再試行・公開し、
  取得中の操作・Gap再進入・失敗・タイムアウトを確定済みキャッシュに見せかけない。
  確定待ちは`Hold`で既に公開した画像を保ち、以前のクリップのFrozenバッファへ切り替えない。
  キャッシュの画素・サイズとhandlerの確定情報がそろった場合だけ最終画像を再公開する。
  動画長が不明な場合も確定情報を作らず、現在の画像を保持する。
  **GPU 構成では CPU 側フリーズバッファを埋めないため `GapRenderDecision` が `Hold` に落ちる。**
  判定を合成層の frozen に寄せる変更は段 3 で行う予定。
- **描画資源の所有者は`RenderSession`**: 内部でバッファ・描画helper・`FrameRenderer`を生成する。
  Windowは`BitmapChanged`をプレビューとFullscreenへ接続し、Gap判断とUI更新を受け持つ。
  native専用バッファとUI公開用バッファを分離し、画像のコピーだけを受け渡す。
  UIバッファの通常/Black/Freeze公開はsession内の同じゲートを通す。
- **デコード方式は `decodeMode` で選ぶ**: `software` のときだけ起動時に 1 回
  `tcs_player_set_decode_mode` を呼ぶ。旧 mpv の `hwdec=no` に相当する指定はこの設定に置き換わった
  （[SETUP.md](SETUP.md)）。

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

- **非E2Eテスト**: GStreamer shimやオーディオデバイスなどの実ネイティブDLL・実デバイスに依存せず、
  フェイク実装（Contracts配下のインターフェースに対するテストダブル）経由でロジックを検証する。
  CI環境やネイティブDLLがない環境でも実行できる。
- **同期の統合テスト**: `SyncScenarioHarness` は本番と同じ `LtcSyncController` を呼ぶ。
  受信済みフレームからの制御を共用し、再生・表示の境界を記録する。生LTCフレームの診断と
  同期抑止、停止通知の再入、デバイス列挙エラー表示の維持はcontrollerの振る舞いで検証する。
- **E2Eテスト（FlaUI）**: FlaUIを用いて実際にアプリケーションを起動し、UI操作を通じて
  エンドツーエンドの挙動を検証する。実行には `native/tcs_gstreamer.dll` と GStreamer ランタイム、
  必要に応じて `SpoutDX.dll` などのネイティブDLLと実機環境が必要（[SETUP.md](SETUP.md)）。
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
`MainWindowResourceDisposer`の5段階（新規受付停止 → 再生停止（ダイアログ表示は「mpv／GStreamer 停止」）→
出力停止 → 全画面終了 → 資源解放）を定められた順序で1つずつ実行する。50ms以上ブロックし得る段階はUIスレッド外で
実行し、ダイアログの進捗表示を更新する。強制終了は確認なしで`Environment.Exit(2)`を呼び、
2秒の番人スレッドでプロセスをKillする。

通常は描画停止 → Fullscreen終了 → timer停止 → render context解放 → shim player 破棄 → LTC終了 →
Spout終了 → timeline終了 → sessionのバッファ・スレッド解放の順に処理する。Gpu経路では
同じ順序（新規受付停止 → `RenderSession.Stop` → `OutputEngine.Stop`（Spout worker join・
全lease返却）→ 全画面閉 → GStreamer（shim）終了 → `OutputEngine.Dispose` → CPU Spout →
バッファ）で、leaseはGStreamer shimのdestroyより先に返す。
停止ではnative workerの完了だけを待つ。UIへ戻る非同期パイプラインをUIスレッド上で待たず、
後着の公開処理は停止フラグで無効化する。

`MainWindowResourceDisposer`は各段階の例外を収集し、独立した後処理を続けてから
`AggregateException`として呼び出し側へ通知する。render contextの解放に失敗した場合は、
そのcontextが参照し得るshimハンドル・callback・バッファ・描画スレッドを保持し、LTCやtimelineなどの
独立した後処理を試行する。Window終了では失敗したnative解放を自動再試行しない。
安全に解放できない資源はプロセス終了まで残るため、終了エラーのログを確認すること。
LTC通知はDispatcherへ渡す前と実行時の両方で終了状態を確認し、終了後のtimer tickも無視する。
native worker自体が戻らない場合は終了待ちが続く。タイムアウトで使用中の資源を解放する動作は行わない。

GPUデバイス消失（`GpuRecoveryState`）は`GpuDeviceLostException`だけを入力とする。プロセス
寿命で自動復旧を1回だけ試し、失敗または再発時は手動再試行（`BtnGpuRetry`）を待つ。復旧手順は
`GpuRecoveryPlan`の順序（Spout worker停止 → lease返却・合成資源破棄 → デバイス再作成 →
合成資源再構築 → 全画面swapchain再作成 → Spout再初期化 → ソース再接続）で実行する。
GPU完了待ちの期限超過はfaultとして新規処理を止めるが、使用中資源の解放理由にはしない。
