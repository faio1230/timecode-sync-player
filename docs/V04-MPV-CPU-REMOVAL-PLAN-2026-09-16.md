# v0.4: mpv と CPU 合成の除去計画（改訂版）

作成: 2026-09-16、親。対象: main `8074747`。
前提の決定: mpv の完全除去（2026-09-13、再確認 2026-09-15）、CPU 合成（`outputBackend=Cpu`）の除去（2026-09-14）、
型名にランタイム名を入れない（2026-09-15）。いずれも利用者の決定。

旧計画（`docs/V04-SCOPE-mpv-removal-decode-mode.md` の 4 節「コミット A → コミット B → 既定切替」）を置き換える。
旧計画のまま進めると壊れることが、再検証（`docs/V04-SCOPE-mpv-removal-recheck-2026-09-15.md`）と
今回の実行経路の調査（下記 1 節）で分かった。

---

## 1. 計画を変える理由（事実。親が要点をコードで確認済み）

### 1-1. E2E は出荷構成をほとんど通っていない

`E2ESettingsIsolation` は空の設定ファイルを渡すので、**E2E の大半は既定値の mpv + CPU 合成で起動している**
（`AppSettings` の既定は `Backend=Mpv`、`OutputBackend=Cpu`）。

| E2E | 設定 | 実際の構成 |
| --- | --- | --- |
| `D5BlackAfterSwitchReproE2ETests` | `backend=1, outputBackend=1` | GStreamer + GPU（出荷構成） |
| `SyncAccuracyE2ETests`（V3） | 測定ディレクトリの `settings.json` を渡す | 実行スクリプトの指定どおり（V3 は GStreamer + GPU） |
| `GStreamerBackendE2ETests` | `backend=1` | GStreamer + **CPU** |
| `CanvasTestCardE2ETests`、`ExitDialogE2ETests` | `outputBackend=1` | **mpv** + GPU |
| その他 | なし | **mpv + CPU** |

**旧計画の順序（消してから既定を切り替える）だと、E2E が初めて出荷構成で走るのは削除の後になる。**
失敗したとき、削除が壊したのか、もともと出荷構成で壊れていたのかを区別できない。

### 1-2. 名前に mpv が付いていても GStreamer 経路が使っているもの

| ファイル | GStreamer 経路での役割 |
| --- | --- |
| `MpvRenderNative.cs` | 型 `MpvRenderParam` / `MpvRenderUpdateFn` が毎フレームのコールバック契約（`IMpvRenderApi`、`GstBackendState`、`RenderSession`）。P/Invoke 本体（`mpv-2.dll`）は mpv 専用 |
| `MpvLibraryNameResolver.cs` | 起動時に必ず登録される DllImportResolver。**GStreamer の DLL 解決もここ**が振り分けている |
| `MpvSessionInitializer.cs` | GStreamer の player 生成も `IMpvApi.Create/Initialize` 経由でここを通る |
| `MpvStartupPropertyApplier.cs` | GStreamer でも実行される。効くのは `pause=yes` だけで、他は no-op |
| `MpvPlaybackCommandBuilder.cs` | `no-osd loadfile "..." replace` を生成し、`GstCommandTranslator` が解析し直す共有経路 |
| `MpvRenderFrameExecutor.cs` | `RenderSession` が生成する（GStreamer + GPU では生成だけで動かない） |
| `Output/MpvSnapshotSource.cs` | `OutputEngine` が常に生成し、**GStreamer ソースが未接続の間（起動〜接続、GPU 復旧中）は mpv 分岐の合成が走る** |
| `SpoutOutput.cs` | 実装は mpv 構成専用だが、定数 `DefaultSenderName` は GPU 経路も使う |

### 1-3. CPU 合成の部品が GPU 構成でも動いている

- **`RenderSession` は GStreamer + GPU でも毎フレーム動く。** shim のフレーム通知 → `DrainNativeUpdatesAsync` →
  `MainWindow.ProcessRenderFrameUpdateAsync` で、シークバー・時刻表示・自動送り・`SubmitOutputState`・ギャップ完了判定が回る。
  スナップショット生成だけが `SuppressFrameSnapshots` で止まっている。**`RenderSession` はファイルごと消せない**
- ギャップが Black のとき、GPU 構成でも `RenderGapAsync` が CPU の黒フレームを作って `FrameRenderer` と Spout 発行へ流している（画面には出ない）
- **GPU 構成ではギャップの Freeze が `Hold` になる。** `GetGapRenderDecision` が CPU 側のフリーズバッファの有無で `GapFreeze` を判定するが、
  GPU 構成ではそのバッファを埋めない（`RenderSession.TryCaptureGapFreezeFrameAsync` が `SuppressFrameSnapshots` で即 `copied=true` を返す）。
  `ComposeLayer` の `GapFreeze` / `SaveFreeze` 経路には到達しない。見た目は多くの場合同じだが、**意図した経路ではない**

### 1-4. GPU 合成が使えない環境の逃げ道が消える

- 現在: 起動時の D3D11.4 検出に失敗すると、**警告ログだけで CPU 合成へ黙って落ちる**（`OutputBackendState`）。ダイアログは出ない
- 検出は通ったが GPU スレッドの初期化で例外が出た場合は、ループが止まり、**UI に何も出ないままプレビューも来ない**
  （`Faulted` を読むコードが無い）
- GStreamer の player 生成に失敗すると「mpv_create 失敗。mpv-2.dll を確認してください。」と出る（文言が mpv 前提）

**CPU 合成を消すと、1 つ目の逃げ道が無くなる。代わりに何を見せるかを決める必要がある**（3 節の判断 1）。

### 1-5. 再生の操作が mpv のコマンド文字列で行われている

`IMpvApi` 越しの文字列呼び出しが約 60 か所ある（MainWindow 32、`MpvStartupPropertyApplier` 10、
`PlaybackOperationsCoordinator` 8 ほか）。アプリが mpv の文法（`no-osd loadfile "<path>" replace -1 start=...`、
`seek 12.346 absolute+exact`、`pause=yes`）で書き、`GstCommandTranslator` がそれを解析し直して shim を呼んでいる。

- 型名を変えるだけでは、**mpv の文法がアプリ全体に残る**
- 実害の例: `MainWindow.xaml.cs` の相対シークは秒数の書式に `InvariantCulture` を付けていない。小数点がカンマのロケールでは解析に失敗する
- 知らないコマンドやプロパティは黙って 0 を返す（`osd-bar`、`osd-msg3`、`hwdec` など）

---

## 2. 改訂した順序

**「出荷構成に切り替えて全部通す」→「使われなくなったものを消す」→「名前と API を整える」の順にする。**
各段は独立に統合し、段ごとに下の検証を通してから次へ進む。

### 段 1: 出荷構成への切替（削除はまだしない）

1. `AppSettings` の既定を `Backend=Gstreamer`、`OutputBackend=Gpu` にする
2. **全 E2E を出荷構成で走らせる。** 個別に `backend` / `outputBackend` を書いているテストは、その指定を外す
3. 出荷構成で落ちる E2E を直す（削除より前に、出荷構成そのものの欠陥として扱う）
4. GPU 構成のギャップ Freeze が `Hold` になる件（1-3）を、`GapFreeze` 経路で動くように直すか、`Hold` を正式な挙動と決めるか
   → **V4 と合わせて親が確認し、結果を報告する**
5. GPU 合成の初期化失敗時の見せ方を、判断 1 の結論どおりに実装する

この段が終わった時点で、mpv と CPU 合成は「設定で明示しない限り使われない死に経路」になる。

### 段 2: mpv の除去

先に移設（振る舞いを変えない）:
- `MpvLibraryNameResolver` の GStreamer 振り分けを中立なファイルへ移す
- `MpvRenderNative` の型（`MpvRenderParam`、`MpvRenderUpdateFn`）を中立名で残し、P/Invoke 本体を消す
- `SpoutOutput.DefaultSenderName` を中立な場所へ移す
- `MpvSnapshotSourceTests` に同居している GPU 共通テスト 2 件
  （`TimelineOutputMailbox_KeepsOnlyLatestState`、`ComposeLayerPolicy_HoldsWithoutInsertingBlackForNotReady`）を移す

そのうえで消す:
- mpv 実装: `Mpv.cs`、`MpvApi.cs`、`MpvRenderApi.cs`、`SpoutOutput.cs`（定数の移設後）
- `PlayerBackend` の設定・分岐・DI（`App.xaml.cs` の 3 か所）、`MainWindow` の mpv + GPU 分岐（`GpuFrameSink`、`PositionSecondsProvider`、`mpv.frame` トレース）
- `OutputEngine` の mpv 経路: `mpvSource`、`CreateMpvSnapshotSource`、`snapshotInput`、`SubmitFrame`、`UploadPendingSnapshot`、
  `WaitUntilOrStopOrSignal` の signal 引数、`SnapshotInputMailbox` 型。
  **GStreamer ソースが未接続の間に何を合成するか**（今は mpv 分岐が走る）を明示的に決めて置き換える（黒または Hold）
- csproj の `libmpv-2.dll` / `mpv-2.dll` の Content Include、mpv 専用テスト、`docs/SETUP.md` の mpv 記述
- player 生成失敗時の mpv 前提の文言
- **v0.3 の設定ファイルに `"backend":0` や `"outputBackend":0` が保存されていても起動できること**（値は無視して警告ログ。設定ファイルは書き換えない）。テストで固定する

### 段 3: CPU 合成の除去

- `OutputBackend` の `Cpu` と、`OutputBackendState` の CPU フォールバック（判断 1 の実装に置き換わっている）
- `RenderSession` のスナップショット経路（`RenderFrameWorker`、`RenderFrameParameterBuilder`、`MpvRenderFrameExecutor`、
  `RenderedFrameSnapshot`、`LatestRenderedFrameMailbox`、フリーズバッファ）。**フレーム通知の駆動と寿命管理は残す**
- `FrameRenderer`、`PreviewFramePresenter`、`RenderFramePublish*`、`PixelBufferManager`、`StartupBufferInitializer`、
  CPU の黒フレーム生成（`OutputFrame` の Normal 系）、CPU Spout 初期化、全画面ウィンドウの `Image` 経路
- `GstMpvRenderApiAdapter.Render*` と `GstBackendState.RenderInto`（`LeasedCpuCopy`）、**shim の `tcs_player_leased_cpu_copy`**（C ABI の削除。I13 の管轄）
- `RenderSessionTests` は分割し、フレーム通知の駆動と寿命のテスト（`NativeCallback_*`、`Callback_*`、`Dispose_*`）を残す
- 参照されていない `RenderWorkerShutdownWaiter` とそのテスト

### 段 4: 名前と API の整理

判断 2 の結論による。

- **案 A（改名のみ）**: `IMpvApi` → 中立名、`IMpvRenderApi` → 中立名、`GstMpvApiAdapter` / `GstMpvRenderApiAdapter`、
  `MpvSessionInitializer`、`MpvStartupPropertyApplier`、`MpvPlaybackCommandBuilder` を機械的に改名。文字列の文法は残る
- **案 B（型付きの操作 API に置き換える）**: `Load(path, start, paused)`、`Seek(seconds)`、`SetPaused`、`SetSpeed`、
  `SetVolume` / `SetMute`、`TryGetTimePos` などのメソッドを持つ中立な再生 API を定義し、GStreamer 実装が直接実装する。
  `MpvPlaybackCommandBuilder`、`GstCommandTranslator`、`MpvStartupPropertyApplier`、mpv 用語の定数が消える。
  文字列を組み立てて解析し直す往復と、未知のコマンドを黙って捨てる挙動が無くなる

### 段 5: 文書・配布物・リリースノート

`docs/SETUP.md`、`docs/verification-checklist.md`、`docs/ARCHITECTURE.md`、`CLAUDE.md` の mpv 前提の記述
（データフロー図、既知のクセの節）、インストーラー、リリースノート草案（`docs/release-0.4-plan.md` の 3 節）。

---

## 3. 利用者の判断が要る事項

1. **GPU 合成が使えない環境で何を見せるか**（段 1 の 5）。今は黙って CPU 合成に落ちている。
   - 親の推奨: **起動時に原因（D3D11.4 対応の GPU が必要、ログの場所）を示すダイアログを出し、再生は無効のままアプリは開く。**
     設定やプロジェクトの確認はできる。GPU スレッドの初期化失敗・player 生成失敗も同じ見せ方にそろえる
   - 他の案: ダイアログを出して終了する
2. **名前の整理を改名だけにするか、型付き API に置き換えるか**（段 4）
   - 親の推奨: **案 B（型付き API）。** 「ランタイム依存の名前がクラスにあるのは良くない」の趣旨は、mpv の文法が
     アプリ全体に残る案 A では満たせない。呼び出しは約 60 か所で、段 2・3 の後なら GStreamer 実装 1 つだけを相手にできる

---

## 4. 各段の検証

**段ごとに全部を通す。** 親が独立に確認する。

| 項目 | 内容 |
| --- | --- |
| ビルド | shim（Debug）、アプリ、テスト。警告 0・エラー 0 |
| 単体 | 非E2E 全件、shim テスト、`check-shim-lock-rule.py` |
| **E2E** | **全件を出荷構成で**。段 1 の結果を基準に、以降の段で落ちる件数が増えないこと |
| 実機 | V3 を Smooth で 1 本（Debug、`scripts/run-v3-accuracy.ps1`）。ロード完了ログが切替ごとに出ること、定常誤差が T7b（平均 -26.0ms、p95-p5 73.0ms）から大きく外れないこと |
| シンボル | 行番号ではなくシンボルで確認する（下表） |

段 2・3 の完了条件（grep で 0 件）:

| 対象 | 段 |
| --- | --- |
| `mpv-2.dll` / `libmpv` への参照、mpv の `DllImport` | 2 |
| `PlayerBackend` | 2 |
| `mpvSource`、`CreateMpvSnapshotSource`、`snapshotInput`、`SnapshotInputMailbox`、`UploadPendingSnapshot`、`SubmitFrame` | 2 |
| `WaitUntilOrStopOrSignal` の signal 引数 | 2 |
| `OutputBackend.Cpu`、`FallbackApplied` | 3 |
| `FrameRenderer`、`RenderFrameWorker`、`RenderedFrameSnapshot`、`LeasedCpuCopy`、`tcs_player_leased_cpu_copy` | 3 |
| 型名・ファイル名の `Mpv`（段 4 の後） | 4 |

## 5. 旧計画の制約の扱い

- 「mpv を消す前に V3 の mpv 基準値を測り切る」（旧 2 節）: V3 は固定値の基準で判定する方針になった
  （利用者 2026-09-15「GStreamer だけ基本的に見ればいい」、`docs/V3-CRITERIA-PROPOSAL-2026-09-15.md`）。**この制約はもう無い**
- `docs/V04-SCOPE-mpv-removal-decode-mode.md` の 3 節（decodeMode）と 5 節（V11）は実施済みで、この計画の対象外
