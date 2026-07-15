# アーキテクチャ

TimecodeSyncPlayer の内部構造について、データフロー・スレッドモデル・主要コンポーネント・
mpv連携やLTC同期の実装上の要点をまとめる。

---

## 1. データフロー

```
[マイク/ライン入力] → NAudio WASAPI → LtcAudioMonitor
                                             ↓
                                       LtcDecoder（純C#）
                                             ↓
                                   SyncDecisionEngine.Decide()
                                             ↓ （Seek / None）
                              MainWindow（UIスレッド）→ mpv_command("seek")
                                             ↓
                              mpv SW render context
                              （MPV_RENDER_API_TYPE_SW）
                                             ↓
                              WriteableBitmap → Image コントロール
                                             ↓
                              SpoutOutput.SendFrame() → SpoutDX.dll
```

LTC音声はNAudioのWASAPIループバック/入力デバイスから取得し、`LtcAudioMonitor` がPCMサンプルを
`LtcDecoder` に渡してタイムコード（時:分:秒:フレーム）を復元する。復元されたLTC秒は
`SyncDecisionEngine` に渡され、現在の再生位置との差分からシークすべきかどうかを判定する。
シークが必要と判定された場合、UIスレッド上でmpvへ`seek`コマンドを発行し、mpvのソフトウェア
レンダリングAPIで描画されたフレームを`WriteableBitmap`経由でUIに表示、同時にSpout2出力にも
渡す。

## 2. スレッドモデル

- **UIスレッド（WPFメインスレッド）**: シークコマンドの発行、`WriteableBitmap`への描画更新、
  ユーザー操作（プレイリスト編集・再生制御）の処理を担う。
- **オーディオスレッド（NAudio WASAPIコールバック）**: `LtcAudioMonitor`がこのスレッド上で
  PCMサンプルを受け取り、LTCデコードを行う。UIスレッドとは別スレッドで動作するため、
  デコード結果をUIスレッドに引き渡す際はスレッドセーフな手段（Dispatcher経由など）を使う。
- **mpv内部スレッド**: mpvのレンダー更新コールバックはmpv自身のスレッドから呼び出される。
  このコールバックからUIスレッドの描画処理を直接呼ぶことはできないため、
  `DispatcherPriority.Background`でUIスレッドに処理を委譲する。

## 3. 主要コンポーネント

| コンポーネント | 役割 |
|---|---|
| `MainWindow.xaml.cs` | メインウィンドウのコードビハインド。mpv初期化、レンダーコンテキスト管理、UIイベントの配線を行う |
| `FrameRenderer.cs` | `WriteableBitmap`の保持とmpvから受け取ったフレームバッファの描画を担当 |
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
  GCに回収されてクラッシュする。必ずインスタンスフィールドとして保持すること。

## 5. LTC同期の要点

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
- **E2Eテスト（FlaUI）**: FlaUIを用いて実際にアプリケーションを起動し、UI操作を通じて
  エンドツーエンドの挙動を検証する。実行には`scripts/get-mpv.ps1`で導入する
  `libmpv-2.dll`（または互換用`mpv-2.dll`）などのネイティブDLLと実機環境が必要。
