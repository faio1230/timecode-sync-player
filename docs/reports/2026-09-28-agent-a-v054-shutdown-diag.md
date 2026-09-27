# 終了時の資源解放の段ごとの Debug の行（shutdown diag）

- 作成: 2026-09-28（サブエージェント、ブランチ `agent-a-v054`、HEAD `78c1ab7` の上）
- コミット: `ab52ed0`（shim、ビルドしていない）、`ef7bad9`（アプリとテスト）
- 背景: `release-0.5-plan.md` の「既知の間欠」の「終了時の資源解放が約 15 秒かかる」（1,652 回中 1 回、資源解放から終了まで 14.86 秒、`GstBackendState: プレイヤー破棄` の行が無い、shim のログも資源解放の開始で切れている）
- 実機・E2E は回していない。shim はビルドしていない（`native/tcs_gstreamer.dll` は触っていない）

## 1. アプリ側（`ef7bad9`）

### MainWindowResourceDisposer（`shutdown {Fields}` の Debug 行）

| 行 | 出る場所 | 中身 |
| --- | --- | --- |
| `stage.begin index=<n> step=<手順名> offUi=<bool> thread=<id>` | `RunNextStage` の各段の前 | 段の番号（0〜8）、終了ダイアログの手順名、UI スレッドの外で走るか、実際のスレッド |
| `stage.end index=<n> step=<手順名> elapsedMs=<ms>` | 各段の後（例外でも `finally` で出る） | 段の経過 |
| `action.begin name=<処理>` / `action.end name=<処理> ok=<bool> elapsedMs=<ms>` | 各処理（`TryCleanup`）の前後 | 処理の名前はコンストラクタの引数名（`stopAcceptingNewWork`・`stopRender`・`stopOutput`・`closeFullscreen`・`disposeTimer`・`disposeRenderContext`・`disposePlayer`・`disposeLtc`・`disposeOutput`・`disposeSpout`・`disposeTimeline`・`disposeBuffer`）。例外でも end が出る（ok=False） |
| `action.skip name=<処理> <理由>` | 条件で実行しなかった処理 | `disposeRenderContext`（stopped=False）、`disposePlayer`（contextFreed・outputStopped の値）、`disposeOutput`（outputStopped=False）、`disposeSpout`（stopped=False）、`disposeBuffer`（contextFreed=False） |

- 処理が null（渡されていない）のときは何も出さない（以前も何もしなかった）
- 条件と順序は変えていない。`_contextFreed = _stopped && TryCleanup(...)` は `_stopped ? TryCleanup(...) : Skip(...)` にした（Skip は false を返す。結果は同じ）
- 既定の出力は `Serilog.Log.Debug("shutdown {Fields}", …)`。テストのためにコンストラクタの最後に `debugLog`（省略可）を足した。MainWindow からは渡していない（既定を使う）

### GstBackendState.DisposePlayer（Debug 行）

- `プレイヤー破棄 開始（_gate 待ち）` → `_gate 取得 elapsedMs` → `通知の解除 終了 elapsedMs` →（既存の Information の `プレイヤー破棄`）→ `プレイヤー破棄 終了 elapsedMs`
- 破棄済み・プレイヤー無しで何もしないときは `プレイヤー破棄 なし disposed=… hasPlayer=…`
- `_gate` の取得を分けたのは、shim のストリーミングのスレッドからのフレーム通知（`OnNativeFrame`）も `_gate` を取るため。取得で待ったのか、shim の中で待ったのかを分ける

## 2. shim 側（`ab52ed0`、ビルドは親）

`tcs_player_destroy` と、destroy から呼ぶときだけの `teardown_pipeline` に、既存の `LOG` で次の行を足した（`[tcs-gst]` の行）。

| 行 | 位置 |
| --- | --- |
| `destroy: enter` | 入口 |
| `destroy: notify cleared elapsed_ms=` | `frame_lock` の中で通知を外した後（行は `frame_lock` の外） |
| `destroy: bus join begin joinable=` / `destroy: bus join end elapsed_ms=` | バスのスレッドの join の前後 |
| `destroy: frames released elapsed_ms=` | `frame_lock` の中のフレームの解放と、GOP の記録の消去の後 |
| `destroy: set_state(NULL) begin` / `destroy: set_state(NULL) end elapsed_ms=` | `gst_element_set_state(NULL)` の前後 |
| `destroy: pipeline unref end elapsed_ms=`（パイプラインが無ければ `destroy: no pipeline`） | `gst_object_unref(pipeline)` の後 |
| `destroy: pipeline torn down elapsed_ms=` | teardown の後 |
| `destroy: ring and hap released elapsed_ms=` | リング・HAP・単一テクスチャの解放の後（既存の `ring: destroyed …` の行は残る） |
| `destroy: spout release begin` / `destroy: spout release end elapsed_ms=` | shim が Spout の送信者を持っているときだけ |
| `destroy: exit elapsed_ms=` | 出口（`delete p` の後） |

- teardown の中の elapsed は直前の段からの差、destroy の行は destroy の入口からの経過
- `teardown_pipeline (TcsPlayer* p, const char* diag = nullptr)`: 既存の呼び出し（ロード・停止など 8 か所）は引数なしのままなので行は増えない
- ロックの規則 I13: `LOG` を `frame_lock` の中に置いていない。状態変更（`set_state(NULL)`）の位置は変えていない（もとから `frame_lock` の外）。
  `python scripts/check-shim-lock-rule.py` の結果: `OK: no state change under frame_lock`、`result: PASS`（許可済みの 3 か所 `state_mutex` の下の set_state は以前と同じ）
- ビルドしていないので、コンパイルが通るかは確かめていない（使った関数 `qpc_now`・`qpc_diff_ms` と `p->qpc_freq` は同じファイルの既存のもの。既定引数は定義で 1 回だけ、前方宣言は無い）

## 3. テスト（`MainWindowResourceDisposerTests`、追加 4 件）

- すべての段（9）と処理（12）の begin と end が、同じ名前で入れ子に対になって出る（処理の順序も固定）。end の行には elapsedMs がある。skip は出ない
- 処理が例外を投げても、その処理の `action.end … ok=False` と段の `stage.end` が出る
- 出力の停止が失敗すると、`action.skip name=disposePlayer contextFreed=True outputStopped=False` が出て、`disposePlayer` の begin は出ない
- 段の begin に手順名・offUi・スレッドが入る
- 非E2E: 2757 件合格、失敗 0、スキップ 0（`ef7bad9`）。アプリのビルド 0 エラー
- GstBackendState の行と shim の行には単体の試験が無い。実機の終了 1 回で順に出るかを見る必要がある

## 4. 前回の 1 件について（推測、記録には関係しない）

- 事実（計画書の記述）: 資源解放から終了まで 14.86 秒、`GstBackendState: プレイヤー破棄`（Information）が無い、最後は正常に `=== TimecodeSyncPlayer 終了 ===`
- 事実（コード）: `ExitCoordinator.RunStagesAsync` の段の実行に時間切れは無い（段が返るまで待つ）。`DisposePlayer` は `PlayerDestroy` が返れば必ず Information の行を出す。
  例外なら `終了手順の失敗`（Error）が出る。`disposePlayer` は `contextFreed && (outputStopped || stopOutput == null)` のときだけ呼ばれる
- 推測: 行が無く、その後に正常に終わっているので、「shim の destroy の中で 14.86 秒止まっていた」なら返った後に行が出るはずで、合わない。
  `disposePlayer` が条件で飛ばされたか（出力の停止か描画コンテキストの解放が失敗）、例外で終わったかのどちらかで、14.86 秒は資源解放のほかの処理（`disposeRenderContext`・`disposeLtc`・`disposeOutput`・`disposeSpout`・`disposeTimeline`・`disposeBuffer`）で使われた可能性がある。
  その回のログに `終了手順の失敗` の行があるかを親が確認すれば、どちらか分かる。今回の行（action.skip・action.begin/end）で、次に出たときは 1 回で分かる

## 5. 未解決の疑問

1. shim の変更のコンパイルと実機の出力は未確認（親のビルド・ロード待ち）
2. 前回の 1 件のログ（`終了手順の失敗` の有無）は、この作業ツリーのログに無く、確認していない
3. `RenderSession.FreeContext`（`disposeRenderContext`）と `RenderSession.Dispose`（`disposeBuffer`、ネイティブのスレッドの join）の中には行を足していない。今回の段の行で、そこが遅いと分かったら中を細かくする
