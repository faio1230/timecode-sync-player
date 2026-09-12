# 段階 4・5 の詳細仕様（キャンバス設定・テストカード／終了ダイアログ・デバイス消失復旧）

状態: 2026-09-11、親が設計。段階 3 の再修正（S3-2）完了後に依頼する。合意済みの製品動作は [OUTPUT-PIPELINE-DESIGN.md](OUTPUT-PIPELINE-DESIGN.md)（固定キャンバス、テストカード、アプリの終了、GPU 異常時の復旧）、配置計算は [CANVAS-PLACEMENT-SPEC.md](CANVAS-PLACEMENT-SPEC.md)（実装済み `CanvasPlacement.cs`）、不変条件は [OUTPUT-GPU-INVARIANTS.md](OUTPUT-GPU-INVARIANTS.md)。本文書は「保存形式・状態遷移・UI の置き場所・受け入れテスト」を確定させるもので、機構の選択は実装に委ねない。

## 段階 4: キャンバス設定とテストカード

### 4.1 保存形式（`ProjectSerializer.cs`）

- `ProjectData` に `Canvas`（`CanvasData?`）を追加: `{ "width": 1920, "height": 1080, "defaultFit": "fit-height" }`。`null`＝未設定（既存プロジェクト）。
- `TrackData` に `Fit`（`string?`）を追加: `"fit-height"`／`"fit-width"`、`null`＝プロジェクト既定を継承。
- `Version` は 1 のまま（追加項目は省略可能）。旧ファイルは `Canvas=null`、`Fit=null` として読める。未知の `Fit`／`defaultFit` は読み込み時に警告ログを出し、`null`（継承）／`"fit-height"` へ倒す。書き戻すときは正規化後の値を保存する。
- 幅・高さの妥当範囲: 16〜16384、それ以外は読み込み時に警告して 1920×1080 に置換。
- `AppSettings` には入れない（プロジェクトの属性）。

### 4.2 実行時の状態

- `ProjectCanvasState`（新規、UI スレッド所有）: `CanvasSettings Current`、`bool IsUnsetInProject`（`Canvas=null` で読み込んだ）、`bool IsDirty`。
- 新規（プロジェクト未読込）: 1920×1080・`fit-height`、`IsUnsetInProject=false`。
- プロジェクト読込で `Canvas=null`: 直ちにサイズ選択ダイアログを出す（4.4）。選択結果は `Current` に入り `IsUnsetInProject=true` のまま。次回保存で `Canvas` に書かれる。ダイアログをキャンセルした場合は 1920×1080 を仮採用し、保存時に書く。
- `OutputEngine.SetCanvas(CanvasSettings)`（新規コマンド）: GPU worker が合成 pool（3 枚）と Freeze 用テクスチャを新寸法で作り直し、次の合成から反映する。作り直し中は最後の合成画像を表示し続け、黒を挟まない。全画面 swapchain は表示先寸法のままで、キャンバス全体を縦横比維持・中央・黒余白で描く（既存の表示描画の規則）。
- `TimelineOutputState` に現在クリップの `ClipPlacement` を含める（トラックの `Fit` を写す）。クリップ切替で配置が変わっても Held の再配置は行わず、次の新画像から新配置（Held は直前クリップの画像のまま直前の配置で描く）。

### 4.3 サイズ変更の可否（純粋関数、管理テスト対象）

`CanvasChangeGate.CanChange(isPlaying, isLtcFollowing, isRenderingFrozenOnly)`:

| 状態 | 変更 |
| --- | --- |
| 停止中・LTC 追従なし | 可 |
| 再生中（速度に関わらず） | 不可 |
| LTC 同期 ON で追従中（信号あり） | 不可 |
| LTC 同期 ON で信号ロス中 | 不可（追従が再開しうる） |
| Freeze／準備待ちで画が止まっているだけ | 不可（「停止」ではない） |

不可のとき UI はサイズ入力と適用ボタンを無効化し、理由をツールチップに出す。適用は「幅・高さを編集中に逐次反映しない」ため、明示の適用ボタンで `SetCanvas` を 1 回だけ送る。

### 4.4 UI

- 置き場所: メイン画面の設定領域（LTC デバイス／同期モードのコンボがある列）に「出力」グループを追加。
  - `CanvasWidthBox`、`CanvasHeightBox`（数値入力）、`CanvasFitCombo`（高さ合わせ／幅合わせ）、`BtnApplyCanvas`（AutomationId 同名）。
  - プリセット `CanvasPresetCombo`: 1920×1080、3840×2160、1080×1920（縦）、カスタム。
- 初回サイズ選択ダイアログ `CanvasSelectDialog`: プリセット＋カスタム、既定 1920×1080、OK／キャンセル。モーダルだが再生・出力は継続（ダイアログ表示中に出力を止めない）。
- クリップの配置: プレイリスト行の右クリックメニュー「配置」→「プロジェクト既定／高さ合わせ／幅合わせ」。AutomationId `TrackFitMenu`。
- テストカード: `BtnTestCard`（`BtnSpout` の隣、トグル表示「Card: ON/OFF」）。起動時 OFF。プロジェクト切替で維持。保存しない。環境変数 `TIMECODE_SYNC_PLAYER_TEST_CARD` は起動時の初期値としてのみ有効（既存）。
- テストカードの図柄は現状の `Pattern`（外周枠・中央十字・動く目印・24bit 更新番号）を採用し、キャンバス寸法で描く。図柄の追加（解像度文字、カラーバー）は範囲外。
- Cpu backend ではキャンバス設定・テストカードの UI を無効化（ツールチップ「GPU 出力でのみ有効」）。I12。

### 4.5 管理テスト（GPU なし）

1. シリアライザ: 旧 JSON（項目なし）→ `Canvas=null`／`Fit=null`；往復で項目保持；未知 ID の警告と正規化；範囲外寸法の置換。
2. `ProjectCanvasState`: 新規／未設定読込／キャンセル時の仮採用／保存で `IsUnsetInProject` が false。
3. `CanvasChangeGate` の表 5 行。
4. `TimelineOutputState` がトラックの `Fit` を `ClipPlacement` に写す（継承＝null）。
5. `OutputEngine` の `SetCanvas` を GPU 抜きの状態機械（`CanvasSwapPlan`: 旧 pool の lease が全て返るまで旧を保持、返った後に破棄）として検証。
6. テストカードのトグルが `TimelineOutputState`／再生状態を変えない（ViewModel テスト）。

### 4.6 実機（親が実施）と E2E

- E2E（FlaUI、Gpu backend）: `BtnTestCard` を再生中に ON/OFF → `TimeLabel` が進み続ける、`BtnPlay` の状態不変。4K キャンバスのプロジェクトを開く → ログに `canvas=3840x2160`。
- 実機: 1080p 素材を 3840×2160 キャンバス（`fit-height`）で全画面＋Spout → 左右黒（受信機の画で確認）、4K 素材を 1920×1080 キャンバス（`fit-width` を上書き）→ 上下切り落とし。素材切替でキャンバス寸法のログが変わらない。カード ON/OFF 中も表示 59.9Hz 以上・合成 p99 1ms 以下（トレース）。

## 段階 5: 終了ダイアログとデバイス消失復旧

### 5.1 終了の状態機械（`ExitCoordinator`、純粋部分は管理テスト対象）

状態: `Running` → `Confirming` → `ShuttingDown(step)` → `Exited`。`Confirming`／`ShuttingDown` から `Forcing` へ遷移可。

| 入力 | Running | Confirming | ShuttingDown | Forcing |
| --- | --- | --- | --- | --- |
| × ／ Alt+F4 ／ Closing | → Confirming（e.Cancel=true、再生・出力継続） | 無視 | 無視 | 無視 |
| キャンセル（既定ボタン、Enter、Esc） | — | → Running | 不可（ボタン無効） | — |
| 通常終了 | — | → ShuttingDown | 無視 | — |
| 強制終了 | — | → Forcing | → Forcing | — |
| 手順完了 | — | — | → Exited（exit code 0） | — |

- `Confirming`: ダイアログ `ExitDialog`（AutomationId `ExitDialog`、ボタン `BtnExitCancel`（IsDefault・IsCancel）、`BtnExitNormal`、`BtnExitForce`）。表示中も再生・LTC・出力は継続。
- `ShuttingDown`: ダイアログを進捗表示に切り替え（現在の手順名: 「新規受付停止」「mpv／GStreamer 停止」「出力停止（Spout 完了待ち）」「全画面終了」「資源解放」）。`BtnExitForce` は有効のまま、`BtnExitCancel`／`BtnExitNormal` は無効。手順は I8 の順序で、50ms 以上ブロックし得る手順（`RenderSession.Stop`、`OutputEngine.Stop`、`OutputEngine.Dispose`、Spout の join）は `Task.Run` で実行し UI スレッドを止めない。UI スレッド専用の手順（全画面ウィンドウ、タイマー）は Dispatcher で実行。時間上限は設けない（自動で強制へ落とさない）。
- `Forcing`: 追加確認なし。ログに「強制終了」を 1 行書き、`Environment.Exit(2)`。2 秒以内にプロセスが終わらない場合の番人として別スレッドで `Process.GetCurrentProcess().Kill()`。送信・GPU 完了を待たない。
- 全画面ウィンドウの × や Esc は全画面解除のみで、`Confirming` に入らない。
- 既存の `Window_Closing → Dispose()` は `ExitCoordinator` 経由に置き換える。`MainWindowResourceDisposer` の順序は変更しない（I8）。

### 5.2 デバイス消失復旧の状態機械（`GpuRecoveryState`、管理テスト対象）

状態: `Running` → `Lost` → `Recovering` → `Running`（自動、プロセス寿命で 1 回のみ）／`Failed`。`Failed` → 手動 → `Recovering`。

| 事象 | Running | Lost | Recovering | Failed |
| --- | --- | --- | --- | --- |
| `GpuDeviceLostException`（worker） | → Lost | 無視 | → Failed | 無視 |
| 自動復旧可（初回） | — | → Recovering | — | — |
| 自動復旧不可（2 回目以降） | — | → Failed | — | — |
| 復旧成功 | — | — | → Running | — |
| 復旧失敗（例外） | — | — | → Failed | — |
| 手動再試行（`BtnGpuRetry`） | 無効 | 無効 | 無効 | → Recovering |

- 復旧手順（GPU worker、`Recovering`）: Spout worker 停止 → 全 lease 返却・pool／Freeze／シェーダー／swapchain 破棄 → デバイス再作成（同じアダプター LUID。取れなければ既定アダプター）→ pool／シェーダー／キャンバス再構築 → 全画面が開いていれば同じ HWND に swapchain 再作成 → Spout が ON なら送信機を同じ名前で再初期化 → ソース再接続。
  - mpv 経路: `MpvSnapshotSource` のリングを新デバイスで作り直す。mpv は SW 描画で影響なし。次の snapshot から映像が戻る。
  - GStreamer 経路: UI スレッドで `GstBackendState` の player を destroy → `SetExternalDevice(新ポインタ)` → player 再生成 → 現在ファイルを再ロード → 直前の再生位置（`TimelineOutputState` の位置）へシーク → 再生中だったら再生再開。世代は +1（旧画像は世代排除で出ない）。
- 復旧中の表示: 最後の合成画像は失われるため黒。UI に「GPU デバイス消失、復旧中」（`GpuStatusText`）を出し、成功で「復旧」表示を 5 秒、`Failed` で「GPU 出力停止。再試行」と `BtnGpuRetry`。
- I9: 完了待ちの期限超過（fault）はこの状態機械に入れない（従来どおり記録して新規処理停止、資源保持）。デバイス消失（`DeviceRemovedReason` 負値、共有資源の abandoned）だけが `Lost` の入力。
- 試験フック: 環境変数 `TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS=<秒>[,<秒>]` で GPU worker が指定時刻に `GpuDeviceLostException` を投げる（起動時に 1 回読む。実デバイスは触らない）。実機ではこれで復旧経路を通す。GPU リセットやドライバー操作は行わない。

### 5.3 管理テスト

1. `ExitCoordinator` の遷移表（全セル）、既定ボタンがキャンセル、`Forcing` で追加確認が出ない。
2. `GpuRecoveryState` の遷移表、自動復旧が 1 回だけ、`Failed` からの手動再試行。
3. 復旧手順の順序（GPU 抜きのプラン列挙: Spout 停止が先、ソース再接続が最後）。
4. 進捗表示の手順名が I8 の順序と一致。

### 5.4 実機（親が実施）と E2E

- E2E: × → `ExitDialog` 表示、Enter → 継続（プロセス生存、`TimeLabel` 進行）。`BtnExitNormal` → exit 0、プロセス残存なし、ログに手順 5 行。Spout ON・全画面中に `BtnExitForce` → 3 秒以内に終了（exit 2）。
- 実機: `SIMULATE_DEVICE_LOSS=10` で 1080p GStreamer×Gpu → 12 秒以内に表示・Spout 復帰、再生位置が連続（ログの位置差 ≤ 1 秒）、error 1 件（消失）。`=10,20` で 2 回目は `Failed` → `BtnGpuRetry` で復帰。mpv×Gpu でも同じ。

## 段階 4・5 の依頼順

段階 4 → 段階 5。各段階 1 コミット。段階 4 は保存形式（4.1）を最初にコミットに含め、親が旧プロジェクトの読み込み互換を先に確認する。
