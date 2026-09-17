# D34 解析: キャッシュ済みプロファイルの速いロード後に尺・位置・フレームが来ない

- 作成: 2026-09-18、同期担当（`agent-a`）
- 基点: main の最新（`635a75b`）を `agent-a` へ通常マージ後（`33e22ef`。版上げ 2 コミットは保持）
- 範囲: 机上解析（コード変更なし、実機なし）。指示: `docs/prompts/2026-09-18-D34-analysis-fast-cached-load-dead-pipeline.md`

## 0. 観測の要約（検証機、実素材）

- ロードが `elapsedMs=110〜150` で `rc=0` になり `Playlist track loaded index=N` は出るが、その後 15 秒間 `FetchMetadata` 行が出ない。
- 表示は `timeLabel=0:00:00:00 / 0:00:00:00`、`metaLine` は前トラックのまま、`position` は切替前の値（例: 開始位置つきロードで要求 14.937 に対し 14.000 のまま 4.6 秒動かない）。
- 同じ素材でもプロファイル試行が 8 件走る 1,000ms 超のロードでは正常。メタデータ欠落 5/383 のうち 4 件が `elapsedMs 113〜224` のロード。
- 「速いロード = キャッシュ済みプロファイルの経路」は試行順からの推定で、コード上にキャッシュ専用の分岐は無い（後述）。

## 1. プロファイルキャッシュの仕組み

- キャッシュはプレイヤー内の `lastGoodProfile`（`native/gst-shim/src/tcs_gstreamer.cpp:266`）。成功した試行の index を `build_pipeline` 末尾で書き込む（同 `:2888`）。プレイヤーはセッションで 1 個（`src/TimecodeSyncPlayer/Gst/GstBackendState.cs:65` の `EnsurePlayer`）なのでトラックロードをまたいで永続する。ヘッダの契約も同じ（`native/gst-shim/include/tcs_gstreamer.h:202-209`）。
- 試行順は純関数 `tcs_decode_profile_order`（`native/gst-shim/include/tcs_decode_policy.h:24-51`）。
  - ハードウェア時: `last_good` を先頭 → 表順の全プロファイル → decodebin（同 `:43-49`）。
  - ソフトウェア時: CPU の `last_good` のみ先頭 → 残り CPU → decodebin → GPU を最後（同 `:31-41`）。
- **キャッシュ専用のロード分岐は存在しない。** キャッシュ経路と通常経路の差は「attempt 0 が `last_good` になる」ことと「attempt 0 の teardown 対象が生きた前パイプラインになる」ことだけ。
- 失敗時のフォールバックはあり: キャッシュが負けても順位が変わるだけで、表順の全プロファイル→decodebin を続けて試す。失敗プロファイルの降格（負のキャッシュ）は無く、`last_good` は成功時のみ更新される。

## 2. rc=0 の時点・`load.summary`・失敗時のフォールバック

- 正常時、`build_pipeline` の `TCS_OK`（`:2950`）は次の後に返る。
  1. 新パイプラインの `frames_decoded>0` ゲート（`:2812-2827`）
  2. 開始位置つきロードのシーク（`:2872-2882`）
  3. 尺のベストエフォート取得（`:2937-2939`。失敗しても `p->duration=-1.0` のまま rc=0）
- したがって建前は「プリロール完了後」。ただし attempt 0 には後述 H1/H2 の抜けがあり、**新パイプラインの実フレーム無しでゲートが成立し得る**。
- `load.attempt` / `load.summary` は両経路で同書式。キャッシュは `attempt=0 profile=<last_good>`、通常は `attempt=N` になるだけで、内容の差は無い（`:2660-2669`、`:2946-2949`）。

## 3. 差分表（キャッシュ経路 vs 通常経路）

| 観点 | キャッシュ経路（attempt 0） | 通常経路（attempt N>0） |
| --- | --- | --- |
| プロファイル順 | `last_good` が先頭（`tcs_decode_policy.h:43-44`） | 表順（キャッシュは既に試行済み） |
| teardown 対象 | 前トラックの**生きた**パイプライン（PLAYING/ストリーミング中）。`tcs_gstreamer.cpp:2672` → `:2415-2444` | 失敗済み attempt（既に NULL 化）または空 |
| 残骸フレームの混入 | 起こり得る。`frames_decoded=0` は `set_state(NULL)` より前（`:2429` vs `:2442`） | 成功 attempt の前に複数回 teardown が入るため通常は混入しない |
| 初回フレーム判定 | `frames_decoded>0` と `!failed && !rejected` のみ。`capsMismatch` は最終判定に含まれない（`:2826`） | 実フレームでゲート |
| rc=0 時点の尺・サイズ | 早期成立だと `duration=-1`・`width=0` のまま rc=0 になり得る | 実フレーム由来の値を取得 |
| `lastGoodProfile` | 成功扱いの index で上書き（`:2888`）。誤成功で汚染され得る | 実プロファイルで更新 |
| 失敗時のフォールバック | 同じ（順位のみ） | 同じ |

## 4. 疑わしい順の仮説

### H1（最有力）: 残骸フレーム＋プロファイル不一致で「死んだパイプライン」を成功扱いする

1. 前パイプラインを PLAYING のまま teardown する。`frames_decoded=0`（`:2429`）の後、`set_state(NULL)`（`:2442`）が完了する前に旧 appsink が 1 フレーム配信すると `frames_decoded=1`（`:1460`）が残る。
2. attempt 0 のキャッシュプロファイルが現素材と不一致だと `on_video_pad` は `capsMismatch=true` を立て、映像チェーンを繋がずに戻る（`:2032-2036`）。
3. 状態待ちは `capsMismatch` で即 break（`:2795-2806`。`gst_element_get_state` の FAILURE も区別せず break）。初回フレーム待ちは残骸カウントで即通過し、最終判定 `frames_decoded>0 && !failed && !rejected`（`:2824-2827`）は **`capsMismatch` を見ない**ため成功扱いになる。
4. 結果: 映像チェーン未接続のまま `rc=0`・`load.attempt result=ok`・`Playlist track loaded`。以降フレームは来ず、`TryGetSize` は `width=0` で失敗（`:3449-3458`）、`get_duration` も失敗（`:3411-3428`）して尺は 0 のまま。さらに `lastGoodProfile` が誤プロファイルで汚染され再発しやすい。
5. 観測との整合: 110〜150ms で返る（旧パイプラインの pad 検出は約 100ms 間隔）、`FetchMetadata` が 1 行も出ない（`width=0`）、絵と位置が止まる、`metaLine` が前トラックのまま。

### H2: 残骸フレーム＋プロファイル一致でプリロール前に rc=0

- attempt 0 のプロファイルが一致して映像チェーンは繋がっても、ゲートが残骸カウントで成立すると、新パイプラインの初回フレーム到着前に rc=0 となり、尺クエリとサイズ取得が間に合わない。
- パイプラインが後から復帰すれば絵は戻るが、`_duration` が 0 のままだとメタ行は前トラックのまま残る（A1 が増幅）。
- 状態待ちは本来プリロール完了まで待つため、H1 より発生には条件が付く（`load.attempt first_frame_ms` / `frames` の実測で判別）。

### H3: 新パイプラインの state 変更が FAILURE で早期 break

- 状態待ちは `if (sr != GST_STATE_CHANGE_ASYNC) break;`（`:2792-2796`）で戻り値を破棄するため、FAILURE でも素通りする。bus error が `p->failed` を立てる前に最終判定へ入ると rc=0 になる（bus error は `:2369-2379`）。
- `load.summary` 直後に `bus error:` が出るかで H1 と区別できる。

### A1（アプリ側・確定的な寄与）: D33-b は単発で、サイズ未取得だと再試行されない

- `ScheduleMetadataFetch`（`src/TimecodeSyncPlayer/MainWindow.xaml.cs:2280-2288`）はロード成功後に `FetchMetadata` を 1 回だけディスパッチする。`FetchMetadata` は `TryGetSize` 失敗で無ログ return（同 `:2299-2300`。ログは `:2303`）。
- タイマーの再試行は `_duration > 0` が条件（同 `:1815-1820`。`_duration` は `TryGetDuration` 成功時のみ更新）。尺も 0 のままだと二度と呼ばれず、`metaLine` は前トラックの表示のまま残る（`ResetPlayerStateForNewTrack` は `_duration`/`_fps`/`_metadataFetched` のみ戻す: 同 `:2402-2412`）。
- D33-b の予約は `PlaybackOperationsCoordinator.LoadFile`（`src/TimecodeSyncPlayer/PlaybackOperationsCoordinator.cs:85`、`LoadFilePaused` は `:108-109`）から呼ばれるため、自動トラック切替（`ContinueOnTrackCoordinator.cs:56-85` → `MainWindow.xaml.cs:955` → `LoadFile`）でも通る。効かないのは「呼ばれていない」からではなく「呼ばれた瞬間にサイズが 0 で、再試行の道が尺ゲートしか無い」ため。

### 位置が切替前の値で固まる件

- `tcs_player_get_time_pos` はパイプラインの位置クエリが失敗すると、**世代を見ずに** `latest_pts_ns`（旧フレーム PTS）を返す（`native/gst-shim/src/tcs_gstreamer.cpp:3387-3395`。世代チェックは一時停止時の分岐のみ `:3382-3383`）。
- 位置つきロード直後の `RefreshPositionDisplayAfterLocatedLoad`（`src/TimecodeSyncPlayer/MainWindow.xaml.cs:1338-1339`、`:1347-1358`）がこれを拾うと、表示が「切替直前の位置」で固まって見える。観測の 14.000 はこの経路（または旧世代の残骸 PTS）の可能性が高い。要ログ確認。
- なお `acquire` は要求世代以外のフレームを捨てる（`native/gst-shim/src/tcs_gstreamer.cpp:3541-3544`）ため、残骸フレームが絵として描かれることはない。

## 5. 「速い経路」だけに固有の列挙

1. attempt 0 = `last_good` プロファイル（`native/gst-shim/include/tcs_decode_policy.h:43-44`）
2. attempt 0 の teardown だけが「生きた」前パイプラインを落とす（`native/gst-shim/src/tcs_gstreamer.cpp:2672`）
3. 残骸フレームが attempt 0 の初回フレームゲートにだけ入り得る（同 `:2429` → `:2815`、`:2826`）
4. 誤成功で `lastGoodProfile` を上書きし、次回も速い失敗を誘発（同 `:2888`）
5. D33-b の単発予約と尺ゲート（`src/TimecodeSyncPlayer/PlaybackOperationsCoordinator.cs:85`、`src/TimecodeSyncPlayer/MainWindow.xaml.cs:2299`、`:1819`）
6. 位置つきロード直後の表示が旧世代 PTS を拾い得る（同 `:1347-1358`）

## 6. 検証機のログで見るべき行

- `load.attempt ... result=ok attempt=0 profile=<名前> first_frame_ms= frames=` — `frames` が 1 で `first_frame_ms` が極小なら H1/H2。
- `pad caps mismatch for profile <名前>` / `load.skip` の有無と順序 — H1 の核心。
- `load.summary` の直後に `bus error:` が出るか — H3 の判別。
- `seek: send begin/end` — 開始位置つきロードでシークが送られたか。
- `D25: dropped pre-seek sample` — シーク境界フィルタの挙動。
- アプリ: `Gst loadfile ... rc=0 elapsedMs=`、`Load path=... loadOk=`、`Playlist track loaded index=`、`FetchMetadata` の有無、`Continue mode: switching to track ...`、`Continue mode: sync seek ...`（フレームが来ない間は sync seek が出ない）。
- `diag: pipe cur=... pending=...` — timeout 時のみ出力。状態失敗の傍証。

## 主要な参照（ファイル:行）

- `native/gst-shim/src/tcs_gstreamer.cpp:266`、`:2888`（`lastGoodProfile`）
- `native/gst-shim/include/tcs_decode_policy.h:24-51`（試行順）
- `native/gst-shim/src/tcs_gstreamer.cpp:2672`、`:2415-2444`（attempt ごとの teardown）、`:2429`、`:2442`（リセットと NULL 化の順）
- `native/gst-shim/src/tcs_gstreamer.cpp:2704-2715`（attempt ごとの状態リセット。`frames_decoded` は含まれない）
- `native/gst-shim/src/tcs_gstreamer.cpp:1460`、`:1448-1450`、`:1591-1604`、`:2010-2052`（フレームと caps）
- `native/gst-shim/src/tcs_gstreamer.cpp:2792-2807`、`:2812-2827`（状態待ちと初回フレームゲート）、`:2826`（`capsMismatch` 非考慮）
- `native/gst-shim/src/tcs_gstreamer.cpp:2872-2882`、`:2937-2939`、`:2950`（シーク・尺・rc=0）
- `native/gst-shim/src/tcs_gstreamer.cpp:2369-2379`（bus error）、`:3369-3409`（`get_time_pos`。`:3387-3395` が旧 PTS フォールバック）、`:3411-3428`（`get_duration`）、`:3449-3458`（`get_size`）、`:3541-3544`（世代違いの破棄）
- `src/TimecodeSyncPlayer/PlaybackOperationsCoordinator.cs:46-87`、`:85`、`:108-109`（ロード後の予約）
- `src/TimecodeSyncPlayer/MainWindow.xaml.cs:955`、`:1332-1341`、`:1347-1358`、`:1815-1820`、`:2280-2312`、`:2402-2412`（ロード経路・表示・メタデータ）
- `src/TimecodeSyncPlayer/Gst/GstPlaybackApi.cs:56-87`（ロードログ）、`src/TimecodeSyncPlayer/Gst/GstBackendState.cs:65`（プレイヤー寿命）
