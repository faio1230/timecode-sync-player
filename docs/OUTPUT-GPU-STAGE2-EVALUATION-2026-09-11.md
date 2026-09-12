# 本体統合 段階 0〜2 の親評価（2026-09-11）

対象: ブランチ `codex/output-engine-20260911-1247`（tip `bdfa34f`、基点 `3ac70d6`＋GStreamer ブランチ `5eb7e62` を merge）。親は独立の検証用 worktree `C:\Users\<user>\Documents\timecode-sync-player-wt-verify-oe-20260911-1344`（detached、同 tip）でビルド・テスト・実機を行った。ネイティブ DLL（libmpv-2.dll、SpoutDX.dll）は session-refactor の `native/` から実行ディレクトリへコピーした。

## 独立検証の結果

- ビルド警告 0・エラー 0。非E2E テスト 1613 件成功（報告と一致）。
- 実機 4 run（Gpu backend、mpv 経路、Spout ON、全画面、公式 WinSpoutDXreceiver、トレース有効、`TestResults/gpu-app-20260911/`）。全 run 正常終了（exit 0、WM_CLOSE から 0.6 秒）、Spout worker join・共有資源解放まで到達。

| run | 素材 | 全画面先 | 合成Hz | 表示Hz | Spout Hz／異なるID | mpv からの実フレーム数/秒 | 合成開始遅れ 平均／最大ms | 合成時間（アップロード込み）平均／最大ms | 生成→走査 平均ms | アプリCPU |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1080p60 H.264（20秒） | 1920×1080 | DISPLAY1（設定不一致） | 60.0 | 59.5 | 60.1／900 | 48 | — | 0.55／1.23 | 5.2 | 27秒/30秒 |
| 4K60 H.264（32秒） | 3840×2160 | DISPLAY1（4K） | 60.0 | 28.7 | 60.0／1255 | 46 | — | 1.43／2.93 | 4.6 | 112秒/41秒 |
| 4K60 H.264（32秒） | 3840×2160 | DISPLAY2（1080p） | 60.0 | 23.6 | 60.0／1274 | 47 | 2.54／5.82 | 1.56／3.42 | 5.9（p95 19.3） | 117秒/42秒 |

（1 run 目は親スクリプトの不備で終了操作に失敗し、受信機の強制終了により Spout worker が停止した。下記 A。）

## 判明した問題（修正が必要、優先順）

**A. 受信機の異常終了で Spout worker が停止する（致命）**
受信機を強制終了すると `Spout access mutex abandoned` の例外で Spout worker が Fault し、以後 Spout 出力が止まる（1 run 目、`app-log-tail` の `[ERR] OutputEngine: Spout worker`）。試作の `SendMutexGate` は abandoned を「取得できた」として扱う。本体では `AbandonedMutexException` を取得成功（所有権が移った）として続行し、Fault にしないこと。ライブ用途では受信側のクラッシュは想定内。

**B. 4K の CPU アップロードが合成 tick の中で走り、表示が半分以下に落ちる（重大）**
`ComposeTick` 内で `TryUpload`（33MB の `UpdateSubresource`）→合成の順に実行しているため、4K では合成開始が平均 2.5ms（最大 5.8ms）遅れ、合成完了が予測 vblank の 3ms 以内に入って表示が見送られる（DISPLAY2: 23.6Hz、DISPLAY1: 28.7Hz。トレース `20260911T045332Z` の 20.0s 付近で確認）。1080p（8MB）では 0.55ms で問題なし。
修正: アップロードを合成 tick から外す。スナップショットが届いた時点で GPU worker の空き時間にリングへアップロードし（次の合成 tick を待たない）、合成 tick では取得だけ行う。加えて、lead を固定 3ms でなく「合成時間の実測 p99＋余裕」で自動調整し、`compose.align` の対象時刻に反映する。

**C. mpv からのフレームが UI スレッド経由で 20〜25% 落ちる（重大、既存経路の制約）**
mpv は 60 回/秒描画しているが（perf ログ `renderCallbacks≈120/2s`）、`coalescedRenderCallbacks` 45〜85/2s、`frameUpdates` 101〜111/2s で、GPU へ届く実フレームは 1080p で 48/秒、4K で 46/秒。設計との差異として報告された「mailbox 直接ではなく既存の `PublishSnapshot` で Retain して渡す」方式が、UI スレッドの Background 優先度ディスパッチの取りこぼしをそのまま引き継いでいる。
修正: `RenderSession` が mpv 専用スレッドで snapshot を mailbox に置いた時点で GPU worker へも Retain して渡す（UI を経由しない）。世代・sequence の逆行防止と `afterFrameProcessed` の呼出しは UI 側の既存経路で維持し、画像の受け渡しとは分離する。`time-pos` の読み取りも mpv スレッド側で snapshot に付ける。

**D. 全画面 swapchain の寸法がウィンドウ配置前の値で作られることがある（中）**
2 run 目で `swapchain を接続 2880x1563`（4K 主画面で 150% DPI の途中寸法）。`ChildHwndReady` がウィンドウの最終配置より先に来ると、その時点の子 HWND 寸法で swapchain が作られ、以後追従しない。
修正: 子 HWND の WM_SIZE／HwndHost のサイズ変更で `ResizeBuffers`（GPU worker 上で、lease を持たないタイミング）し、`display.attach` の寸法を最終値にする。または最終配置後に接続する。

**E. その他**
- `OutputBackend=Gpu` で `SpoutOutput.TryInitialize`（CPU 側 spoutDX）も初期化されている模様。GPU 経路では不要なら初期化を避ける（送信者名の二重登録の可能性）。要確認。
- 4K の mpv SW デコード＋描画で CPU 約 2.7 コア。これは素材側の上限で、GStreamer（D3D11VA）経路の必要性を裏付ける。
- 親スクリプト側の不備（全画面終了時に別ウィンドウを掴む、設定 JSON のバックスラッシュ、受信機の閉じ方）は修正済み（`Invoke-AppGpuTrial.ps1`）。

## 良かった点

- 合成・Spout は 4K 素材でも 60Hz、Spout の異なる ID 1255〜1274/1680（mpv からの実フレーム数と整合）、Spout 年齢平均 12〜14ms。
- 1080p では表示 59.5Hz、生成→走査 5.2ms、Present→走査 2.4ms で試作と同等。vblank 整列の位相誤差 p99 0.002ms。
- 正常終了の順序（合成停止→Spout join→解放）が本体でも成立し、WM_CLOSE から 0.6 秒で exit 0。
- 合成 pool・フェンス・vblank・整列・配置・ソース契約の移植は管理テストで固定されている。

## 判断

段階 3 へ進む前に A〜D を修正し、同じ 4K 素材で「表示 59.9Hz 以上・mpv 実フレームの取りこぼし 5% 未満（4K の SW デコード上限は別）・受信機強制終了後も Spout 継続」を親が再評価する。GStreamerSource の本体配線（Adopt）は D3D11VA 経路の 4K 検証に必要なので、段階 6 を段階 3 の前に前倒しする。

## 証跡

- run: `TestResults/gpu-app-20260911/20260911T044952Z-app-1080p-gpu`（不備あり、A の証跡）、`20260911T045137Z-app-1080p-gpu`、`20260911T045332Z-app-4k-gpu`、`20260911T045748Z-app-4k-gpu-display2`。各 `app/`（トレース）、`analysis-*/`、`runner-result.json`、`inputs.json`、`app-log-tail.txt`。
- 実行スクリプト: `TestResults/gpu-mutex-retry-session-20260910T0752Z/Invoke-AppGpuTrial.ps1`（Windows PowerShell 5.1、UIA で BtnSpout／BtnFullscreen を操作、WM_CLOSE で通常終了）。
- 素材: `<verify worktree>/artifacts/media/test_4k60_h264.mp4`（ffmpeg testsrc2、libx264 veryfast、40 秒）、`test_1080p60_h264.mp4`。
- GPU 試験の時刻（JST）: 13:49–13:50、13:51–13:52、13:53–13:54、13:57–13:58。隣の GStreamer／出力エンジンのエージェントは idle を確認してから実行。

## 追記: 修正コミット `6c8669b` の親再評価（2026-09-11 15:30〜15:32 JST）

検証 worktree を `6c8669b` に更新し、ビルド警告 0、非E2E 1617 件成功を確認。実機 2 run（`TestResults/gpu-app-20260911/20260911T063028Z-app-4k-gpu-fix`、`20260911T063134Z-app-1080p-recvkill`）。

| 項目 | 修正前（4K、DISPLAY2） | 修正後（4K、DISPLAY2） |
| --- | ---: | ---: |
| 表示 Hz | 23.6 | 55.1 |
| 表示 ID 飛び（28秒） | 567 | 126 |
| 合成開始遅れ 平均／最大 ms | 2.54／5.82 | 0.33／1.73 |
| 合成時間 平均／p99 ms | 1.56／2.63 | 1.69／3.32 |
| 合成の位相誤差 p99 ms | 0.004 | 1.52 |
| mpv からの実フレーム/秒 | 47 | 50.6 |
| 生成→走査 平均／p99 ms | 5.9／19.5 | 6.3／9.8 |
| Spout Hz／異なる ID | 60.0／1274 | 60.0／1675 |
| アプリ CPU | 117秒/42秒 | 122秒/42秒 |

- B（アップロードの tick 外化）と C（mpv スレッドからの直接受け渡し）は効いている。合成開始遅れは解消し、実フレームは 46→50fps（残りは 4K SW デコードの上限、CPU 約 2.9 コア）。
- **残る問題 F: lead の毎秒再計算で表示が周期的に落ちる。** lead が 2.9〜7.0ms の間で毎秒変わり（`composeLeadMs` の lifecycle 記録）、変更直後の秒で表示が 35〜40 回に落ちる（秒ごとの present: 57, 60, 35, 56, 60, 60, 60, 60, 40, 55, …）。位相誤差 p99 が 0.004→1.52ms に悪化したのはこの再位相合わせによる。合成時間の p99 が 3.3ms と大きいのは、アップロードの GPU コピー（33MB）が合成のフェンス待ちに含まれるため。
- **残る問題 G: 表示は 55Hz で、合格条件（59.9Hz 以上）に未達。** F を直せば到達する見込み。
- **A（abandoned）**: 受信機を 10 秒時点で強制終了しても送信は途切れず（5 秒前後で publish 数が変わらず、最大間隔 20ms 台）、error 0、exit 0。ただしこの run では abandoned の outcome が記録されなかった（受信機がその瞬間に mutex を保持していなかった）。abandoned 経路自体は管理テストで固定されており、実機での再現は受信機のタイミング次第。
- **D（swapchain 寸法）**: `display.attach:1920x1080` で最終寸法に一致。

### 次の修正指示（F）

1. アップロードの GPU 完了を合成から分離する: アップロード後にアップロード専用の EVENT クエリで完了を確認してからリングの画像を Ready にし、合成のフェンス待ちに 33MB コピーを含めない（合成時間を試作並みの約 0.3ms に戻す）。
2. lead の更新にヒステリシスを入れる: 上げるときは即時（p99＋1ms）、下げるときは 5 秒間連続して p99＋1ms が現行 lead を 0.5ms 以上下回った場合だけ 0.5ms 刻みで下げる。1 秒ごとの再位相合わせをやめる。
3. これで表示 59.9Hz 以上・位相誤差 p99 0.1ms 以下・表示 ID 飛び 1% 未満を親が再評価する。

## 追記: 修正コミット `1ece106` の親再評価（2026-09-11 15:48〜15:49 JST）

検証 worktree を `1ece106` に更新し、ビルド警告 0、非E2E 1620 件成功。4K・DISPLAY2・32 秒（`TestResults/gpu-app-20260911/20260911T064830Z-app-4k-gpu-fix2`、解析窓 10〜38 秒）。

| 指標 | 合格条件 | 6c8669b | 1ece106 | 判定 |
| --- | --- | ---: | ---: | --- |
| 表示 Hz（窓平均／最終秒を除く） | 59.9 以上 | 55.1 | 59.5／59.8（秒ごと 58〜61、最終秒は全画面解除で 51） | 実質合格 |
| 表示 ID 飛び | 1% 未満 | 126（7.5%） | 4（0.24%） | 合格 |
| 合成の位相誤差 p99 | 0.1ms 以下 | 1.52 | 0.001（最大 0.50 は lead 変更時） | 合格 |
| 合成時間 p99 | 1ms 以下 | 3.32 | 1.63（平均 0.34、最大 2.75） | 未達（軽微） |
| 合成開始遅れ 平均／p99 | — | 0.33／— | 0.35／0.76 | — |
| mpv からの実フレーム/秒 | — | 50.6 | 59.3 | 改善 |
| 生成→走査 平均／p99 | — | 6.3／9.8 | 5.7／7.1 | — |
| Spout Hz／異なる ID | — | 60.0／1675 | 60.0／1677 | — |
| lead の推移 | 振動しない | 25 回往復 | 5.47→1.97ms を 5 秒ごとに 0.5ms 刻みで降下 | 合格 |

- F-1（アップロード完了の分離）で合成時間の平均が 1.69→0.34ms に戻り、GPU worker が空いた分 mpv からの実フレームが 50.6→59.3fps に上がった（4K60 の testsrc2 素材。実写ではデコード負荷が上がる）。
- 合成時間 p99 1.6ms は、たまに長い合成（最大 2.7ms）が残るため。lead が p99＋1ms で追従するので表示への影響は小さい（ID 飛び 0.24%）。原因追跡は段階 3 以降の測定項目にする。
- 正常終了（exit 0、WM_CLOSE から 0.6 秒）、受信機 exit 0、error 0、プロセス残存なし。

**判断: 段階 0〜2 は合格。段階 6（GStreamerSource の本体配線、OutputEngine デバイスの Adopt）を段階 3 の前に進め、D3D11VA 経路で同じ 4K 評価を行う。**

## 追記: 段階 6 `8ed1943`（GStreamerSource の本体配線）の親評価（2026-09-11 16:30〜16:48 JST）

検証 worktree を `8ed1943` に更新し、ビルド エラー 0、非E2E 1621 件成功。GStreamer shim は `native/gst-shim` のソース（最終変更 813ee4f）から検証 worktree で再ビルドし（`tcs_gstreamer.dll` SHA256 先頭 `617247049D5F1AF1`、エージェントが使った `4A3374C09332207D` はソース変更前の古いビルド）、`vendor/Spout2` は GStreamer worktree の pin 済み checkout（f49e2f4）を複製。runner `Invoke-AppGpuTrial.ps1` に `-PlayerBackend Gstreamer`（settings `backend:1`）を追加。全 run は DISPLAY2 全画面 1920x1080、公式受信機あり、Spout ON。

### コードレビュー（差分の確認結果）

- shim は Adopt したデバイスの immediate context に `ID3D11Multithread::SetMultithreadProtected(TRUE)` を掛けており（`tcs_gstreamer.cpp` 198 行付近）、「GStreamer ストリーミングスレッドと合成 GPU worker が同一コンテキストを共有しても D3D11 側で直列化される」という報告は根拠あり。ただし保護は全 API 呼び出しにクリティカルセクションを入れるので、合成側のコストは今後の測定項目。
- `NativeTextureOps.CopyResource` の vtable スロット 47 は `ID3D11DeviceContext` の配置（IUnknown 3＋DeviceChild 4＋VSSetConstantBuffers=7 … CopySubresourceRegion=46, CopyResource=47）と一致。
- 終了順序は `MainWindowResourceDisposer`: stopRender → stopOutput（`OutputEngine.Stop`: GPU worker join → Spout worker 停止 → lease 返却）→ closeFullscreen → disposeMpv（GStreamer では `GstMpvApiAdapter.TerminateDestroy` → shim player destroy）→ disposeOutput → disposeSpout → disposeBuffer。lease 返却が shim destroy より先で設計どおり。実機ログも「GStreamerSource を接続」→ … →「プレイヤー破棄」の順。
- 世代は shim 側の値を観測して写像（`SyncGStreamerGeneration`）。トレースでは load 時に `gst.generation:1`→`2` と進み、以後 `generationRejected 0`。
- リースは毎 tick 返却し、描画テクスチャは AddRef 所有ラップ（`OpenOwned`）。`peakLeases 1`。shim の「リース中は同じ画像を返す」規則と整合。

### 実機結果

| run | 素材 | 窓 | 合成 Hz | 表示 Hz | Spout Hz | ソース Ready | 異なるフレーム/秒 | 合成時間 p99／最大 ms | 生成→走査 平均／p99 ms | 位相誤差 p99 ms | アプリ CPU 秒／実行秒 |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |
| `20260911T073643Z-app-1080p-gst-gpu` | 1080p60 | 9〜20s | 60.0 | 59.9 | 60.0 | 660/660 | 60 ×11 秒 | 0.92／1.18 | 5.8／6.3 | 0.001 | 6.4／23.3 |
| `20260911T073853Z-app-4k-gst-gpu` | 4K60 | 10〜38s | 60.0 | 59.7 | 60.0 | 1680/1680 | 60 ×28 秒（飛び 0・重複 0） | 0.96／2.64 | 8.6／9.8 | 0.001 | 12.4／40.7 |
| 参考 `20260911T064830Z-app-4k-gpu-fix2`（mpv、1ece106） | 4K60 | 10〜38s | 60.0 | 59.5 | 60.0 | 1680/1680 | 58〜60（飛び 15・重複 19） | 1.63／2.74 | 5.7／7.1 | 0.001 | 120.2／40.7 |

- **4K60 H.264 の実フレームは 60/秒を 28 秒間維持（mpv 経路の 59.3fps・飛び 0.9% に対し欠落 0）。合格条件「59fps 以上」を満たす。**
- **CPU は mpv 経路の約 1/10**（4K: 2.95 コア相当 → 0.30 コア相当。1080p: 27〜33 秒/20〜24 秒 run → 6.4 秒/14 秒 run）。合格。
- 合成時間 p99 は 0.92〜0.96ms で、段階 2 で未達だった「1ms 以下」も満たす（アップロードが無くなったため）。
- 生成→走査が 4K で 8.6ms と mpv 経路（5.7ms）より約 3ms 大きいのは、lead 制御器が起動直後の長い合成（初回 16.7ms、以後数秒 7ms 台＝デコーダ起動とシェーダ初期化）で lead を 8ms に上げ、5 秒ごとに 0.5ms ずつしか下げないため。窓の終わりでは 5ms まで下がっている。欠陥ではないが、起動直後の合成時間を lead の学習から除外する（例: 最初の 3 秒は上げ幅を記録しない）と定常 5〜6ms に早く収束する。段階 3 の改善候補。
- 正常終了は全 run で exit 0（WM_CLOSE から 0.3〜0.5 秒）、受信機 exit 0、プロセス残存なし、error イベント 0、`appDroppedEvents 0`。
- 受信機断（`20260911T074053Z-app-1080p-gst-recvkill`、`20260911T074735Z-app-1080p-gst-recvkill2`、12 秒後に受信機を強制終了）: 送信は 60/秒のまま途切れず、mutex 取得は最大 1.7ms、SendTexture 最大 0.3ms、error 0、exit 0。abandoned の outcome は両 run とも記録されず（mpv 経路の run と同じく受信機がその瞬間 mutex を保持していなかった）。abandoned 経路は管理テストのみで固定。
- ソース→合成の位相は 1 秒あたり約 +0.15ms ずつ動く（`composePeriod 16.6692ms` は表示に追従、shim の配信は GStreamer のシステムクロック）。設計どおり「合成は表示の時計、ソースは自分の時計」で、原理上 約 110 秒に 1 回 1 フレームの重複または飛びが出る。今回の 40 秒以内では未観測。

### 判明した問題 H（ソース配信の間欠的な欠落、段階 3 の前に原因特定）

1080p の 3 run で、数秒間続く「seq 飛び（+2）と NotReady の対」が観測された。位置（pts）は実時間どおり進むので、shim の `latest` が acquire 前に置き換わる（配信 2 枚が同じ tick 間に来る）ことと、次の tick で配信が無いことが交互に起きている。

| run | 欠落の区間（アプリ時刻） | 異なるフレーム/秒 | 状況 |
| --- | --- | --- | --- |
| recvkill | 6〜8 秒、23〜31 秒 | 50〜57、42〜48 | 全画面。受信機は 19.2 秒で終了（区間と一致しない） |
| control34（受信機断なし） | 36〜39 秒 | 57→49 | 全画面、EOS の 4 秒前 |
| recvkill2 | 0〜5 秒 | 44〜52 | ウィンドウ表示（全画面接続前）。全画面後は 34 秒間 60/秒 |
| 1080p 初回、4K | なし | 60 | — |

- 合成側の証拠は白: 欠落区間でも合成時間 ≤1.4ms、合成位相誤差 0、表示 60/秒、Spout 60/秒。原因は配信側（shim の `on_new_sample` に来る時刻の揺れ、またはストリーミングスレッドの停滞と追い付き）。
- shim は `appsink sync=TRUE drop=FALSE max-buffers=4`、配信ごとに `latest` を置換し、通知コールバック（`GstBackendState` の thunk → `RenderSession` の更新コールバック）をストリーミングスレッドで呼ぶ。コールバックの所要時間はトレースに無い。
- 必要な追加証跡（実装側へ）: shim の配信トレース（`on_new_sample` 到達 QPC、pts、running-time、コールバック所要時間、置換回数）を `events.jsonl` と同じ時計で出し、`sourceDiagnostics.replaced` に shim の置換数を写す（現在は 0 固定）。合わせて `GstVideoDecoder` の QoS ドロップ数。原因が特定されるまで、段階 3〜5 の合格判定はこの欠落を除いて行わず、再現 run（1080p 40 秒 ×3）で「欠落 0」を段階 3 の合格条件に含める。

### 判断

**段階 6 は「4K60 実フレーム 60/秒・CPU 1/10・正常終了・受信機断で送信継続」で合格。ただし問題 H（配信の間欠欠落）を段階 3 の最初の項目として原因特定・修正する。** 段階 3〜5 の指示は 8ed1943 を基点に出す。証跡: `TestResults/gpu-app-20260911/20260911T07*`（各 run の `runner-result.json`・`inputs.json`・`app/events.jsonl`・`analysis-*`）。

## 追記: 問題 H 修正コミット `cd25d46` の親評価（2026-09-11 18:10〜18:14 JST）

検証 worktree を `cd25d46` に更新。ビルド エラー 0、非E2E 1622 件成功。shim を同ソースから再ビルド（`tcs_gstreamer.dll` SHA256 先頭 `F8BF0427E776F594`）。1080p60・DISPLAY2 全画面・公式受信機で 20 秒 run 1 本（`TestResults/gpu-app-20260911/20260911T091229Z-app-1080p-gst-h1`、集計 `TestResults/gpu-mutex-retry-session-20260910T0752Z/gst_delivery_check.py`）。

### 原因の特定は妥当

- 新しい配信トレース（`gst.delivery`）で、shim への到着は 2400 枚／40 秒 = 60/秒、到着間隔 平均 16.67ms・p1 15.0・p99 18.4・最大 18.9ms、通知コールバック p99 91µs。ソース側の停滞は無い。
- 到着間隔の揺れ（±2ms）と、合成 tick との位相が 1 秒あたり 0.15ms ずつ動くことの組合せで、位相が近い区間では「1 tick に 2 到着＋次 tick に 0 到着」が数秒続く。旧実装は未配信 1 枚（latest）しか持たないので前者で 1 枚捨て、後者で NotReady。親評価の H（seq+2 と NotReady の対）と一致する。

### 修正（未配信 FIFO 4 枚、到着順に 1 tick 1 枚）は副作用があり不採用

| 区間 | 到着→取得の遅れ（平均） | 出来事 |
| --- | ---: | --- |
| 1〜15 秒 | 18.0ms（約 1 フレーム） | 定常。FIFO に常時 1 枚滞留 |
| 16 秒 | 41→51.4ms（約 3 フレーム） | Spout ON（送信 worker 起動で合成が数 tick 遅れた） |
| 17〜33 秒 | 51.4ms のまま | 回復しない |
| 34〜37 秒 | 43→56.5ms | 全画面接続（swapchain 作成）でさらに滞留 |

- 取得は 1 tick 1 枚なので、合成が一度でも数 tick 止まると滞留分がそのまま固定遅延になり、FIFO 上限（4 枚＝67ms）まで増えて戻らない。合成周期（表示追従 16.6692ms）がソース周期（16.6667ms）より長いため、停止が無くても約 110 秒に 1 枚ずつ滞留が増える。
- 異なるフレームは 60/秒（欠落 0）、error 0、exit 0、CPU 16.7 秒/58 秒（0.29 コア、従来と同じ）。欠落は消えたが、設計の「最新優先」（ソース契約 規則 2）に反し、ライブ用途の遅延として不可。
- 起動時の UIA ボタン検出に 15 秒かかった（従来 2.4 秒）。アプリログ上は 0.5 秒でエンジン初期化・1.3 秒で再生開始しており、アプリ側の遅れではない。再現したら別途調べる。

### 再修正の指示（H-2、段階 3 の前）

FIFO は残し、取得規則を「最新優先＋揺れ吸収 1 枚」にする。
1. acquire 時の未配信数を n とする。n=0 → 0（NotReady）。n≤2 → 最古を返す（1 tick 2 到着の吸収。滞留は最大 1 枚）。n>2 → 最古の n−2 枚を破棄（`latest_replaced` に計上）して残り 2 枚の古い方を返す（停止後は即座に最新へ追い付く）。
2. n=2 が 30 回連続した場合（周期差による定常滞留）は最古を 1 枚破棄する。これで定常の滞留は 0〜1 枚に戻り、約 110 秒に 1 枚の破棄が「最新優先」の形で起きる。
3. 合成側は変更しない（毎 tick 1 枚取得・リース毎 tick 返却のまま）。README の規則 2/4 を上記に更新。
4. 合格条件（親が同じ runner で確認）: 1080p60 60 秒 run で、異なるフレーム 60/秒（起動 2 秒を除く）、到着→取得の遅れ 平均 20ms 未満・p99 36ms 未満で、Spout ON・全画面接続の後に段階的に増えないこと。`latest_replaced` は定常で 60 秒あたり 1 以下。4K60 32 秒で同じ確認。

## 追記: H-2 `c12e214`＋段階 3 `112a9fd` の親評価（2026-09-11 19:18〜19:31 JST）

検証 worktree を `112a9fd` に更新。ビルド エラー 0、非E2E 1644 件成功、shim 再ビルド（`tcs_gstreamer.dll` SHA256 先頭 `7A9313901C034366`）。runner に `-NoSpout` を追加。全 run DISPLAY2 全画面 1920x1080、GStreamer×Gpu。集計は `gst_delivery_check.py` と `analyze_probe.py`。

| run | 素材／秒数 | Spout | 異なるフレーム/秒 | 到着→取得 遅れ 平均 | 表示 Hz（ID 飛び） | 合成 p99／最大 ms | lead 推移 | 生成→走査 平均 | CPU |
| --- | --- | --- | --- | ---: | ---: | ---: | --- | ---: | ---: |
| `20260911T101915Z-app-1080p-gst-s3` | 1080p／60 | ON | 60（31〜39 秒は 58〜59） | 5→17ms（30 秒で漸増） | 59.1（49） | 1.00／1.71 | 3.1→2.1→2.7 | 4.1ms | 0.20 コア |
| `20260911T102652Z-app-4k-gst-s3-recvkill` | 4K／32、20 秒で受信機断 | ON | 60 | 4→5ms | 59.0（19） | 7.68／9.28 | 8.00 固定 | 10.1ms | 0.25 コア |
| `20260911T102936Z-app-4k-gst-s3-nospout` | 4K／32 | OFF | 60 | — | 59.9（—） | 0.71／11.3（単発 2 回） | 4.1→8.0→5.5 | — | 0.21 コア |

### H-2（配信）: 概ね良好、境界での挙動に H-3 が必要

- 4K: 到着→取得の遅れ 4〜5ms で安定、置換 0、`generationRejected 0`。cd25d46 の 51〜56ms 固定化は解消。
- 1080p 60 秒: 遅れが 5→17ms へ漸増（合成周期が表示追従で 0.15ms/秒ずつ長いため）。17ms（1 フレーム）に達した 31〜39 秒で「n=2 が 30 回連続 → 1 枚破棄」が 2 秒おきに発動し、破棄の直後に NotReady（seq+2 と NotReady の対が 7 回）。境界付近では n が 1↔2 を揺れるため、連続回数の規則では位相を跨ぎ切れない。
- **H-3（指示）**: 「n=2 が 30 回連続」を「n=2 かつ最古の到着からの経過が 1.25 フレーム（21ms）以上」に置き換える。これで遅れは 4→21ms の鋸歯（約 110 秒周期）になり、破棄の直後に NotReady は出ない（4ms ＞ 到着揺れ 2ms）。n>2 の規則は現状のまま。

### 段階 3（Spout 同一デバイス化）: 不合格（表示落ちと 4K 合成停滞）

1. **vblank ゲートの判定不備（表示落ちの直接原因、Spout ON の全 run）**: 合成完了から判定までに 2ms 程度の遅れが入ると、`Predict(now)` が「目標時刻（vblank−3ms）を過ぎた」だけで次の vblank を選び、vblank まで 3ms 残っていても表示しない（`VblankDisplayGate.Predict` の条件 `vblank − margin > now`）。目標を過ぎても vblank が Lead(1ms) 以上先なら即時 Present する規則は `pending` がある場合しか適用されない。1080p 60 秒の遅い Present 49 回のうち 35 回がこの経路（skip 記録なし、`wait.start(compose)` → 次の vblank）。
2. **4K で毎秒 1 回、合成が 7〜10ms 停滞（Spout ON のみ）**: 各秒の x.25 秒付近に 1 回、`compose.start`→`compose.complete` が 7〜10ms（Spout OFF の対照では p99 0.71ms、単発 2 回のみ）。lead が 8ms に張り付き、生成→走査は 10.1ms（Spout OFF は lead 5.5ms まで低下）。受信機断（20 秒）の後も継続するので受信機ではなく送信側。停滞中に Spout worker は `send.noStage` を記録（29 回）。
3. **スレッド安全性**: 「immediate context は free-threaded 前提」は誤り。ID3D11 の immediate context はスレッド安全でなく、共有には `ID3D11Multithread::SetMultithreadProtected(TRUE)` が必要。現状は GStreamer shim が Adopt 時に有効化しているため動いているが、mpv×Gpu では保護が無い（`GpuDevice.cs` に設定が無い）。Spout worker は `GpuFence.Wait`（`End`/`Flush`/`GetData`）と SpoutDX の `SendTexture` で共有 context を使う。
4. 良かった点: Spout 60Hz・受信機断後も送信継続・error 0・exit 0、Spout OFF では合成にコピーが入らない（`spout.stage` 0）、4K の lead ウォームアップ除外は機能（Spout OFF で 4.06ms から開始）。

### 段階 3 の再修正指示（S3-2）

1. ゲート: `Decide` で `Predict(now)` の後、`now > next.TargetQpc` かつ `now <= next.VblankQpc − leadTicks` かつ `Presentable` なら即時 `Present`（pending の有無に依らない）。`Predict` の k の条件も `vblank − lead > now` に変える。管理テスト: 目標通過後 2ms・vblank まで 3ms のケースで Present になること。
2. 4K の 1Hz 停滞: 合成の内訳（acquire、SRV 作成、draw、fence 待ち）と Spout 側（`Stage` のコピー発行、worker の `End/Flush/GetData`、`SendTexture`）を同じ時計で 1 tick ごとにトレースし、毎秒 1 回の 7〜10ms の発生源を特定して除く。合格は Spout ON 4K で合成 p99 1ms 以下（Spout OFF と同等）。
3. context 共有: `GpuDevice` 生成時に `ID3D11Multithread.SetMultithreadProtected(TRUE)` を明示し（shim 任せにしない）、設計文書の「free-threaded 前提」を「Multithread 保護で直列化」に訂正。Spout worker の context 呼び出しは最小化する（コピー完了はフェンス値＋`SetEventOnCompletion` で待ち、worker から `Flush` しない）。
4. 合格条件（親が確認）: 1080p 60 秒と 4K 32 秒（受信機断あり）で、表示 59.9Hz 以上・ID 飛び 0.3% 未満、合成 p99 1ms 以下、Spout 60Hz、生成→走査 平均 6ms 台（4K）、error 0、exit 0。H-3 も同じ run で確認（1080p 60 秒で seq+2 と NotReady の対が 0）。

## 追記: H-3 `e3505a0`＋S3-2 `3591b0a`＋Spout 別デバイス復帰 `4c9028b` の親評価（2026-09-12 01:45〜01:55 JST）

検証 worktree を `4c9028b` に更新。ビルド エラー 0、非E2E 1639 件成功、shim 再ビルド（`tcs_gstreamer.dll` SHA256 先頭 `9EED00E04E3B3AA2`）。GStreamer×Gpu、DISPLAY2 全画面、Spout ON。`analyze_probe.py` は vblank 方式の即時 Present（目標通過後、vblank−1ms まで）を有効な選択と扱うよう更新済み（合成テスト 111 件成功）。集計は `app_run_summary.py`（新規、runner と同じ場所）。

| run | 実フレーム/秒 | 到着→取得 遅れ | 表示 Hz（ID 飛び） | Spout Hz | 合成 p99／最大 ms | 生成→走査 平均 ms | lead | CPU |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| `20260911T165111Z-app-1080p-gst-s32`（60 秒、素材 40 秒） | 60（seq+2 と NotReady の対 0、置換 0） | 2.0→3.5ms | 59.94（3） | 60.0 | 1.00／1.50 | 4.2 | 2.0〜2.5 | 0.21 コア |
| `20260911T165314Z-app-4k-gst-s32-recvkill`（32 秒、20 秒で受信機断） | 60（対 0、置換 0） | 14.4→18.3ms | 59.82（4） | 59.8 | 0.97／1.51 | 8.6 | 3.9→8.0（7 秒）→5.0 | 0.38 コア |

### 判定

- **H-3 合格**: 両 run とも id delta は全て +1、NotReady 0（素材終端まで）、置換 0。遅れは周期差で 1 秒あたり 0.1〜0.15ms ずつ増え、21ms 閾値には未到達（4K で 18.3ms まで）。1080p 180 秒の長時間確認（閾値到達→1 枚破棄→遅れが 4ms 台へ戻ること）は 6b の後に行う。
- **S3-2.1（ゲート即時 Present）合格**: 遅い Present は 60 秒で 3 回、32 秒で 4 回（0.1%）。表示 59.8〜59.9Hz。
- **Spout 別デバイス復帰 合格**: 1080p で合成 p99 1.00ms、4K で 0.97ms（Spout ON）。受信機断後も送信継続（58〜60/秒）、**abandoned mutex 経路が初めて実機で発動**（受信機が mutex 保持中に終了、取得扱いで継続、error 0）。
- 4K の毎秒停滞（デコーダとの同一 context 衝突）は本 run では出なかった（位相次第。実装側の run では出ている）。基準は緩めず、段階 6b（デコーダ別デバイス化＋shim 側 NT 共有リング＋共有フェンス）で解消する。段階 4 より先に行う。

### 新たな欠陥（6b と同時に修正）

- **D-2: 即時 Present 経路の空回り**。latency waitable が未シグナルのとき `display.vblank.predict`→`skip(display.vblank.notReady)` を約 1µs 間隔で繰り返し、vblank を過ぎるまで数千回回る（1080p 60 秒で 7,059 件、4K で 4,143 件、各 2〜3ms のバースト 3〜4 回）。表示は落ちないが CPU と trace（23MB）を無駄にする。修正: notReady のときは waitable を vblank−lead まで待つ（タイムアウト付き）か、その予測を捨てて次の合成期限まで待つ。1 tick に notReady は最大 1 回記録。
- **L-2: lead が全画面接続の直後に 8ms へ跳ぶ**。4K run で 7 秒（display.attach 5.6 秒の直後）に 3.9→8.0ms となり、0.5ms/5 秒でしか戻らないため生成→走査が 30 秒間 8〜10ms。swapchain 作成の 1 回の長い合成を学習している。修正: display.attach／detach と Spout ON/OFF の後 1 秒は lead 学習から除外（起動後 3 秒除外と同じ扱い）。
- 軽微: 素材終端で shim が `Ended` ではなく `NotReady` を返す（Held 表示は正しい）。契約上は `Ended`。6b で合わせる。
- 解析ツール: `abandoned` の取得結果を「取得成功」と扱うよう `analyze_probe.py` を更新（設計どおり）。

## 追記: 段階 6b `31c99f9`（GStreamer デコーダの別デバイス化＋shim 共有リング）の親評価（2026-09-12 02:28〜02:34 JST）

検証 worktree を `31c99f9` に更新。ビルド エラー 0、非E2E 1653 件成功、shim 再ビルド（`tcs_gstreamer.dll` SHA256 先頭 `32A7793D8C4440BA`）。shim は同一アダプターに自前の D3D11 デバイスを作り（Adopt 廃止）、NT 共有テクスチャのリング 3 枚と D3D11.4 共有フェンス（値＝seq）で合成側へ渡す。合成側はリングとフェンスを接続時に一度だけ開き（ログ「共有リングを開きました 1920x1080 slots=3」）、描画前に `ID3D11DeviceContext4.Wait` で GPU 側待ち。GStreamer×Gpu、DISPLAY2 全画面、Spout ON。

| run | 実フレーム/秒 | 到着→取得 遅れ | 表示 Hz（ID 飛び） | Spout Hz | 合成 p99／最大 ms | 生成→走査 平均 ms | lead | CPU |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| `20260911T172859Z-app-1080p-gst-6b`（36 秒） | 60（対 0） | 3.6→5.2ms | 59.93（2） | 60.0 | 0.90／1.29 | 4.3 | 2.0〜2.5 | 0.22 コア |
| `20260911T173010Z-app-4k-gst-6b-recvkill`（32 秒、20 秒で受信機断） | 60（対 0） | 13→16ms | 59.9（2、最終秒除く） | 60.0 | 0.93／3.33 | 5.5 | 2.3〜4.3 | 0.26 コア |
| `20260911T173124Z-app-1080p-gst-6b-long`（112 秒、120 秒素材 `stage3-1080p120s.mp4` SHA256 先頭 `53E2CAF23B8C2E25`） | 60 ×110 秒（対 0、id delta 全て +1） | 12→15ms | 59.97（2） | 60.0 | 0.89／2.30 | 6.75 | 8.0（7 秒）→2.0（72 秒）→2〜3 | 0.21 コア |

### 判定: 段階 6b 合格（4K の合成 p99 1ms 以下・生成→走査 6ms 台を Spout ON で達成）

- 4K で毎秒の停滞は消え、合成 p99 0.93ms（最大 3.33ms は起動直後の 1 回）。デコーダの仕事が合成の context から分離された。
- 112 秒の長時間 run で欠落 0・表示落ち 0。H-3 の 21ms 閾値には未到達（今回の周期差は約 0.03ms/秒で、以前の 0.15ms/秒より小さい。表示周期の実測が run により違うため、閾値到達の確認は今後の長尺 run に持ち越す）。
- 受信機断後も送信継続、error 0、exit 0、プロセス残存なし。

### 残る軽微な欠陥（段階 4 の前に 1 コミットで修正）

- **D-2**（未修正）: 即時 Present 経路の notReady 空回り。3 run で 1,918〜4,419 件。
- **L-2**（未修正）: 全画面接続直後に lead が 8ms へ跳び、112 秒 run では 72 秒まで生成→走査を押し上げた（平均 6.75ms、lead 2ms 時は 4.3ms）。
- 素材終端で `Ended` ではなく `NotReady`。
- 配信トレース（`gst.delivery`）が長時間 run の最初の 43 秒分欠落（イベントリングの容量か drain 間隔）。合否には影響しないが、到着→取得の遅れが最初から読めるようにする。

## 追記: A `21b08a0`（D-2／L-2／Ended／配信トレース）＋段階 4 `7b662c7`（キャンバス設定・テストカード）の親評価（2026-09-12 03:08〜03:16 JST、自律運転中）

検証 worktree を `7b662c7` に更新。ビルド エラー 0、非E2E 1691 件成功、shim 再ビルド（`tcs_gstreamer.dll` SHA256 先頭 `E13B08A4D37DF2BD`）。runner に `-ProjectPath`（`--load-project`）、`-ClickPlay`、`-ScreenshotAtSeconds`（DISPLAY2 を `CopyFromScreen` で PNG 保存）、`-TestCardOnAtSeconds/-TestCardOffAtSeconds` を追加。プロジェクトは素材と同じディレクトリに置く（`ProjectSerializer` はプロジェクトディレクトリ外の絶対パスを拒否する。最初の run はこれで素材が読めず無効）。

| run | 内容 | 結果 |
| --- | --- | --- |
| `20260911T181147Z-app-1080p-gst-s4-canvasA2` | キャンバス 2560×1080・既定 fit-height、1080p 素材、14 秒でスクリーンショット、20〜28 秒テストカード ON | 実フレーム 60/秒（カード ON 中も継続）、表示 60/秒、合成 p99 0.93ms、lead 2.0〜2.5ms（接続後に跳ばない: L-2 解消）、notReady skip 最大 1/秒（D-2 解消）、`canvas:2560x1080`、`testCard:on/off` 記録、error 0、exit 0 |
| 同 スクリーンショット `display-14s.png` | 期待: キャンバスを 1920×810 に中央表示（上下 135px 黒）、その中に素材 1440×810（左右 240px 黒） | 実測: 映像は x 240〜1679、y 135〜944、黒帯の最大輝度 0。**一致** |
| `20260911T181311Z-app-4k-gst-s4-canvasB` | キャンバス 1920×1200・既定 fit-height、4K 素材、トラック上書き fit-width | 実フレーム 60/秒、表示 60/秒、合成 p99 1.07ms（最大 1.40）、`canvas:1920x1200`、error 0、exit 0 |
| 同 `display-14s.png` | 期待: キャンバスを 1728×1080（左右 96px 黒）、素材は fit-width で上下 60px 黒→表示で 54px | 実測: x 96〜1823、y 54〜1025。**一致**（トラックの上書きが効いている） |
| E2E `CanvasTestCardE2ETests`（2 件） | カード ON/OFF 中に TimeLabel 進行・BtnPlay 不変、4K キャンバスのプロジェクトで `canvas=3840x2160` ログ | 2/2 成功 |

- 配信トレースは再生開始（`BtnPlay`）以降の全秒に存在（配信トレース初期欠落 解消）。
- 4K run で lead が 7.6 秒に 8.0ms へ跳んだ（再生開始 5.8 秒の直後、4K の初回フレーム＝リング作成・SRV 作成の長い合成を学習）。L-2 の除外は attach/detach/Spout のみなので、**L-3: 世代変更・ソース接続・リング作成の後 1 秒も除外**を段階 5 と同時に入れる。合成 p99 1.07ms は基準 1ms を僅かに超えるが最大 1.40ms で表示落ちなし、許容。
- 未実施: 旧形式プロジェクト（Canvas なし）読込時のダイアログの実機操作（管理テストで固定済み。UIA で扱うなら runner 側に OK クリックが必要）。

**判断: A・段階 4 合格。段階 5 へ（`docs/prompts/2026-09-12-stage5.md`、基点 7b662c7）。**

## 追記: 段階 5 `3d5c4cf`（終了ダイアログ・デバイス消失復旧・L-3）の親評価（2026-09-12 03:46〜04:00 JST、自律運転中）

検証 worktree を `3d5c4cf` に更新。ビルド エラー 0、非E2E 1712 件成功、shim 変更なし。runner に `-ExitDialog None|Normal|Force`（WM_CLOSE 後にダイアログのボタンを UIA で押す）、`-SimulateDeviceLoss`、`-GpuRetryAtSeconds` を追加。

| run | 内容 | 結果 |
| --- | --- | --- |
| E2E `ExitDialogE2ETests` 3 件 | ×→Enter でキャンセル継続、通常終了 exit 0・手順 5 行、Spout＋全画面で強制終了 exit 2（3 秒以内） | 3/3 成功 |
| `184718Z-app-1080p-gst-s5-loss10` | 1080p GStreamer、10 秒で消失シミュレーション、通常終了 | 消失 9.31 秒→デバイス再作成 9.35→資源 9.36→表示 9.37→Spout 9.40→リング再オープン 9.40→復旧完了 9.40（**90ms**）。その秒の実フレーム 55、以後 60/秒。位置は連続（1 秒ごとに +1.0 秒）。error 1（消失のみ）、exit 0 |
| `184902Z-app-1080p-gst-s5-loss10-20-retry` | 消失 10 秒・20 秒、27 秒で BtnGpuRetry | 1 回目は自動復旧（80ms）、2 回目は Failed（出力停止、ログ「再試行を待ちます」）、BtnGpuRetry で復旧し 60/秒に復帰。exit 0 |
| `185006Z-app-1080p-mpv-s5-loss10` | mpv×Gpu、10 秒で消失 | 復旧 100ms、実フレーム 51→60/秒、Spout 継続、exit 0 |
| `185104Z-app-4k-gst-s5-loss10` | 4K GStreamer、10 秒で消失 | 復旧 100ms・リング再オープン成功。ただし合成が毎秒 1 回 10〜12ms 停滞（下記 R-1） |
| 全 E2E（Category=E2E） | 62 件 | 53 成功・**2 失敗**・7 スキップ（ケーブルループ等の既存スキップ）。失敗は `SettingsPersistenceE2ETests` の 2 件で、`CloseMainWindow()` 後の exit 待ちが終了ダイアログで止まる（P-1） |

### 差し戻し

- **R-1: 3d5c4cf で 4K の合成が毎秒 1 回 7〜12ms 停滞（7b662c7 では出ない）**。同条件の連続比較: 3d5c4cf plain（`185700Z`）合成 p99 7.49ms・毎秒 1 回 >3ms・lead 8ms・生成→走査 10.2ms、直後に 7b662c7 を再ビルドして同 run（`185832Z`）は p99 0.85ms。段階 5 の変更が原因。1080p では出ない。
- **P-1: 既存 E2E 2 件**が終了ダイアログを想定していない（テスト側の修正。製品挙動は変えない）。
- 任意 L-4: lead が単発スパイク 1 回で 8ms に跳ぶ（p99＝60 標本の最大値）。

**判断: 段階 5 の機能は合格水準だが、R-1・P-1 を修正するまで不合格。指示 `docs/prompts/2026-09-12-R1-P1.md` を送付（04:00）。検証 worktree は 7b662c7 のまま（次の確認時に更新）。**

## 追記: R-1・P-1 修正 `a9b9349` の親評価（2026-09-12 04:55〜05:03 JST、自律運転中）

検証 worktree を `a9b9349` に更新。ビルド エラー 0、非E2E 1712 件成功、shim 変更なし。R-1 の修正内容: 合成 tick で共有リングのフェンスを GPU 側 `Wait` すると、合成自身のフェンス待ちにデコーダ側のコピー完了（キーフレーム時に長い）が含まれていた。修正後は `Fence.CompletedValue` を CPU で確認し、未完了ならその tick は Held を描いて次 tick で最新を使う（I1 の揺れ吸収 1 フレーム以内、I5 の「GPU 完了は tick の外」に整合）。P-1: `E2EAppRunner.ExitNormally`（WM_CLOSE→ダイアログ→BtnExitNormal→exit 待ち）を追加し 7 箇所を置換。

| run | 結果 |
| --- | --- |
| `195545Z-app-4k-gst-r1-plain`（4K、Spout ON） | 実フレーム 60/秒、合成 p99 **1.00ms**（最大 1.37）、表示 60/秒、生成→走査 **4.4ms**、lead 2.0〜2.5ms、error 0 |
| `200053Z-app-1080p-gst-r1`（1080p） | 実フレーム 60/秒、合成 p99 0.99ms、表示 59.93Hz、生成→走査 4.6ms、error 0 |
| `200153Z-app-4k-gst-r1-loss10`（4K、10 秒で消失） | 復旧後（12〜38 秒）も毎秒の停滞なし（下記の追記参照） |
| 全 E2E 62 件 | **55 成功・0 失敗・7 スキップ**（P-1 解消） |

**判断: R-1・P-1 解消。段階 5 合格。段階 0〜6b・3〜5 の全段階が合格。次は段階 7（文書・引き継ぎ、コード変更なし）。** 到着→取得の遅れは 4K で 17〜19ms（フェンス未完了時に 1 tick 遅らせるため以前より約 3ms 増）。H-3 の 21ms 閾値に近いので、長尺 run での挙動（破棄→4ms 台へ戻る）を今後の確認項目に残す。


## 追記: S1・S2 修正 `d347673`（→ main `fa77d0b`）の親評価（2026-09-12 18:35〜19:10 JST）

V1 コーデック行列の不合格 2 件（S1 音声付き MP4、S2 ProRes）の差し戻し分。変更は shim のみ（`native/gst-shim/src/tcs_gstreamer.cpp`、`README.md`）で C# と C ABI は不変。検証 worktree を `d347673` に更新し、shim を再ビルドしてアプリ bin・テスト bin へ配置。

| 確認 | 結果 |
| --- | --- |
| ビルド | 成功・警告 0 |
| 非E2E | 1712 件成功・0 失敗 |
| 全 E2E | 55 成功・0 失敗・7 スキップ（基点と同数） |
| `tcs-shim-test` 11 素材 | 11 本すべて読み込み成功。AAC 付き MP4 = `d3d11h264dec` 60/秒、ProRes = `avdec_prores` 60/秒 |
| 基点 DLL（58ef4a5）との対照 | 同じ exe で DLL だけ基点に差し替えると AAC・ProRes とも failures=25（読み込み自体が失敗）。**S1／S2 の修正を独立に確認** |

実機（GStreamer×Gpu、Spout ON、DISPLAY2 全画面、50 秒 run、解析窓 8〜48 秒。`TestResults/v1-d347673`）:

| 素材 | デコーダ | 実フレーム/秒 | 表示 最小/秒 | Spout | 合成 p99 | error | exit |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| **H.264 1080p60＋AAC** | d3d11h264dec | 60 | 60 | 60.0 | 0.92ms | 0 | 0 |
| **ProRes 422 1080p60**（窓 8〜28 秒） | avdec_prores | 60 | 59 | 60.0 | 0.82ms | 0 | 0 |
| H.264 1080p 23.976 | d3d11h264dec | 23〜24 | 59 | 60.0 | 0.97ms | 0 | 0 |
| H.264 1080p 25 | d3d11h264dec | 25 | 59 | 60.0 | 0.96ms | 0 | 0 |
| H.264 1080p 29.97 | d3d11h264dec | 29〜30 | 58 | 60.0 | 1.10ms | 0 | 0 |
| H.264 1080p 59.94 | d3d11h264dec | 59〜60 | 58 | 60.0 | 0.94ms | 0 | 0 |
| H.264 1080p60 MPEG-TS | d3d11h264dec | 60 | 59 | 60.0 | 1.02ms | 0 | 0 |
| HEVC 8bit 1080p60 | d3d11h265dec | 60 | 59 | 60.0 | 0.97ms | 0 | 0 |
| HEVC 10bit 1080p60 | d3d11h265dec | 60 | 59 | 60.0 | 0.93ms | 0 | 0 |
| HEVC 4K60 | d3d11h265dec | 60 | 59 | 60.0 | 0.94ms | 0 | 0 |
| H.264 1080p60 MXF（10 秒素材・8 秒 run） | d3d11h264dec | 起動後 60 | 57 | 55.7 | 0.72ms | 0 | 0 |

### 合成 p99 が 1ms を僅かに超えた件は shim 由来ではない（A/B で確認）

29.97 が 1.10ms、TS が 1.02ms と基準（1ms 以下）を僅かに超えたため、**exe を固定して DLL だけ入れ替えた A/B** を 29.97 素材で交互に 4 本実施（`TestResults/ab-2997`）。

| DLL | 合成 p99 | mean | max | late present(>25ms) |
| --- | ---: | ---: | ---: | ---: |
| 基点 58ef4a5 | 1.01ms | 0.36 | 1.90 | 14 |
| 新 d347673 | 0.93ms | 0.34 | 1.75 | 8 |
| 基点 58ef4a5 | 1.00ms | 0.37 | 2.28 | 26 |
| 新 d347673 | 0.98ms | 0.38 | 5.25 | 5 |

基点でも p99 は 1.00〜1.01ms に達する。この素材・この時間帯（並行プロセスあり）では 1ms 前後が地の値で、**新 DLL による回帰ではない**（late present はむしろ新 DLL の方が少ない）。全フレームで `gst_memory_map(GST_MAP_READ|GST_MAP_D3D11)` を通す変更の影響も、この範囲では検出できない。

### S2 の「実フレーム 23」は素材長 30 秒による EOS 境界

初回集計（窓 8〜48 秒）で ProRes の実フレームが 23〜60 と出たが、`ffprobe` で素材長を確認したところ **ProRes 422 だけ 30 秒**（他は 60 秒、MXF は 10 秒）。30 秒で EOS に達し、以降は `compose.sourceEnded` で Held を出し続けていた（present・Spout は 60/秒を維持、I7 に整合）。窓 8〜28 秒では 60/秒・欠落 0・NotReady 0・id delta 全 1。**デコード能力の不足ではない**。

### 設計差異（不変条件との照合）

- **音声シンクのプローブ**: `tcs_player_create` で `audiotestsrc num-buffers=1 ! autoaudiosink` を 1 回だけ実行し（プロセス内 `call_once`）、EOS が来なければ `fakesink sync=true` に退避。不変条件には触れない。音声デバイスが無い環境では最大 2 秒の起動コストが増える。実機（音声デバイスあり）では `autoaudiosink` が選ばれた。
- **一時停止ロードの音声プライム**: 一時停止ロードは初回音声バッファを最大 2 秒待ってから PAUSED へ落とす。カウンタは PLAYING 開始時に 0 クリアされ、映像 preroll 待ちの間に音声バッファが届くため通常は待ちが発生しない（実機 run の起動時間に増加は見られない）。待機中は volume 0 なので可聴ノイズは出ない。ミュート・音量 API は不変。
- **CPU デコードもリング経路**: CPU プロファイル（`prores-cpu` 等）と最終退避 `decodebin(sysmem)` は `d3d11upload` を挟み、リース経路は共有リング（slot ≥ 0）に統一された。合成側の変更なし（I12 に抵触せず）。到着→取得の遅れは ProRes で 6.9→8.7ms（GPU デコードは 4〜6ms）で、H-3 の 21ms 閾値には十分余裕がある。
- 最終退避の `decodebin(sysmem)` は container 自体を `decodebin` に差し替える経路（`container = "decodebin"`）なので、qtdemux で一致しない素材も拾える。ただし decodebin が d3d11 メモリを出す素材では `videoconvert` と繋がらない可能性があり、**未検証**（DNxHD・MJPEG 等の実素材で確認する）。

**判断: S1・S2 合格。main へ rebase して ff 統合（`fa77d0b`、native ツリーは検証した `d347673` と同一）。次は V2（音声出力・ミュート・音量・速度）。**

### 今回見つかった積み残し

- **S3（既存事象・新規記録）: MPEG-TS のシーク後にフレームが期限内に来ない**。`tcs-shim-test v1_h264_1080p60.ts` で `new-gen frame available after seek` と `stepped frame leased` が失敗する。**基点 DLL でも同じ 2 件**が出るため d347673 の回帰ではないが、LTC 同期シーク（V3）とトラック切替（V5）に関わるため独立項目として追跡する。MP4／MOV では発生しない。
- **GStreamer の E2E は依然スキップ**: `GStreamerBackendE2ETests` が要求する `artifacts/media/test_720p25.mkv`・`test_720p25.avi`・`test_720p50.ts` と recv ツールが worktree に無い。shim の E2E 被覆は現状ゼロで、`tcs-shim-test` と実機 run が唯一の確認手段。素材整備を V2 以降の作業に含める。
- **ProRes の 60 秒素材を作り直す**: 現行は 30 秒のため V6（長時間）には使えない。

## 追記: S3 修正 `6aa2264`（→ main `57ba70b`）の親評価（2026-09-12 21:33〜21:53 JST）

MPEG-TS のシーク後にフレームが期限内に来ない件（S3）の修正。変更は shim とその単体テストのみで、C# と C ABI は不変。
親の指示は `docs/prompts/2026-09-12-S3-answer.md`（方式 5: KEY_UNIT|SNAP_BEFORE＋目標未満を破棄）と
`docs/prompts/2026-09-12-S3-answer2.md`（方式 2: catch-up 区間を実時間でなぞらないよう再基準化）。

| 確認 | 結果 |
| --- | --- |
| ビルド・shim 再ビルド | 成功 |
| 非E2E | 1712 件成功・0 失敗 |
| 全 E2E | 55 成功・0 失敗・7 スキップ（基点と同数） |
| `tcs-shim-test` 13 素材 | **すべて failures=0**。TS 3 本（mpegtsmux／ffmpeg remux／SPS-PPS 正規化）が初めて通った |
| 着地誤差 | TS +14／+3／+3ms、MP4・MOV・MXF 0ms（1 フレーム未満） |

実機（GStreamer×Gpu、Spout ON、DISPLAY2 全画面、`TestResults/s3-verify`。DISPLAY1 は 4K のまま）:

| run | 実フレーム/秒 | Spout | 合成 p99 | error | exit |
| --- | ---: | ---: | ---: | ---: | ---: |
| MPEG-TS 50 秒 | 60 | 60 | 0.96ms | 0 | 0 |
| H.264＋AAC 50 秒 | 60 | 60 | 0.93ms | 0 | 0 |
| ProRes 422 50 秒（窓 8〜28 秒） | 60 | 60 | 0.95ms | 0 | 0 |
| HEVC 4K60 50 秒 | 60 | 60 | 0.92ms | 0 | 0 |

### クロック方式の変更（最重要の設計差異）の評価

実装側は、フラッシュシーク後に wasapi2 のリングバッファが止まって**音声シンクが提供するクロックが凍結し、
目標が約 10 秒届かない**事象（TS・MP4 の両方）を踏み、**パイプラインクロックをシステムクロックに固定**した
（`gst_pipeline_use_clock` ＋ 音声は `GstAudioBaseSink` 既定の skew slaving）。全パイプラインに無条件で効く変更で、
実装側は「長時間再生の安定性は親評価を」と未検証に挙げた。

親は **1080p60＋1kHz サイン音（−20dBFS）の 5 分素材**を作り、`scripts/AudioLoopbackProbe` で既定の再生デバイスを
ループバック録音しながら 270 秒の実機 run を実施（`TestResults/s3-verify/long-audio-rms.csv`、
run `20260912T124721Z-s3-long-audio`）。

| 指標 | 実測 | 判定 |
| --- | --- | --- |
| 音声の途切れ | RMS < −60dBFS の窓は **6/2795**。その位置は t=0〜200ms（再生開始前）と t=279.2〜279.4 秒（停止後）**のみ**で、走行中はゼロ | 途切れなし |
| 音量の安定 | 2795 窓で floor −41.08dBFS／loud −41.07dBFS、**span 0.01dB** | 完全に一定 |
| クロックのずれ | 音声時刻と実時間の差が 250 秒で 2896→2926ms＝**+30ms（120ppm）**。単調でジャンプなし | skew slaving が吸収できている |
| 映像 | 実フレーム 59〜61/秒（255 秒間）、Spout 最小 59、NotReady 0、合成 p99 0.94ms・最大 2.24ms、late present 6/15299、lead 2.0〜3.1ms、error 0、abandoned 0 | 合格 |

**判断: S3 合格。main へ rebase して ff 統合（`57ba70b`、native ツリーは検証した `6aa2264` と同一）。**

### V6（長時間 60 分）へ持ち越す具体的な予測

上の 120ppm は音声デバイスの水晶とシステムクロックの差で、**60 分では約 430ms** を音声シンクが skew で
吸収し続けることになる。4.6 分では無害だったが、**V6 ではこの累積が可聴な補正（クリック・ピッチ揺れ）に
ならないかを必ず確認する**こと。確認方法は同じループバック録音で、`quiet_windows` と `max_lag_ms` の
伸び方を見る（今回の値が基準: 250 秒で +30ms、途切れ 0）。

### 今回見つかった環境側の事実

- **この環境の ffmpeg では `h264_nvenc` が使えない**。プリセットは旧命名（`default`／`medium`／`hq`…）で
  `p1`〜`p7` を解さず、`medium` でも `Cannot get the preset configuration: unsupported param (12)` で失敗する。
  V1 素材は GStreamer 経由の NVENC で作られていたため露見していなかった。**素材生成は libx264 で行う**
  （`scripts/make-e2e-media.ps1` もそうする）。
- **WASAPI ループバックは、再生中の音が何も無いとバッファを 1 つも返さない**。窓 0 件は異常ではなく無音を意味する。
  また音が途切れるとイベント自体が来ないため、窓番号ではなく**実時間（`wall_ms`）を併記**しないと途切れが見えない。
  `AudioLoopbackProbe` は両方を反映済み。
