# A1 机上解析: 4K 実素材でギャップ進入後に「位置は正しいのに絵が更新されない」経路

- 作成: 2026-09-17、同期担当（`agent-a`）
- 基点: main 最新（D31/D31-b 統合後）
- 範囲: 机上解析（コード変更なし）と開発機での再現案
- 入力: 検証機の観測（F-1/F-3/F-4/F-5、候補 1・2 の両方で再現。生成素材では通る。参照 PNG の古さは検証機側で確認中）

## 0. 前提: 開発機のエンコーダ確認（2026-09-17）

`scripts/make-e2e-media.ps1` が優先する ffmpeg で次を確認した。

| エンコーダ | 有無 | 実測（4K、1 秒素材のエンコード） |
| --- | --- | --- |
| `prores_ks` | あり | 60fps・profile 3（422 HQ）・`yuv422p10le` で約 1.8 秒 / 5.3MB |
| `libsvtav1` | あり | 24fps・preset 10・`yuv420p` で約 2.5 秒 |
| `libaom-av1` | あり | 同一条件では大幅に遅く、再現用途は libsvtav1 を推奨 |
| `libx265` | あり | HEVC の代替（CPU プロファイル強制の組み合わせ用） |

## 1. pump 予算内にフレームが届かないときに何が起きるか

### 1.1 シム側

- pump は「一時停止中の seek」だけを対象にする。`pump_arm` は `p->paused` が真のときだけ期限（既定 4000ms、`TCS_PUMP_BUDGET_MS`）を張り、PLAYING へ上げて最初のフレームを待つ（`native/gst-shim/src/tcs_gstreamer.cpp:2226-2256`、特に 2231-2235）。ギャップ進入は `PauseForGap` の後に seek するため、この経路に入る。
- 期限超過は「診断」であり、seek は失敗にならない。`pump_preroll_tick` は期限到達で PAUSED へ戻し、`last_error` を記録し、`seek_boundary_expect` を 0 にして以後のフレームを落とし続けない（同 `2282-2306`、ログは `2316-2333`）。
- 期限後の位置クエリ `tcs_player_get_time_pos` は、一時停止中でも `latest_gen == generation` のときだけ配信済みフレームの PTS を返し、新世代のフレームが来ていなければ pipeline の `gst_element_query_position` に落ちる（同 `3382-3396`）。**位置は seek 目標（正しく見える）／絵は旧世代のまま**という組み合わせはここで成立する。D25-c が直したのは「pump 完了後に正しいフレームが届いている」場合で、届いていない場合は pipeline 値が使われる。
- 新世代のフレームが無い間、合成側は直前リース（`active`）を Ready として返し続ける（`src/TimecodeSyncPlayer/Output/GStreamerSource.cs:148-193`、特に 152-156。`src/TimecodeSyncPlayer/Output/OutputEngine.cs:1207-1266`、特に 1249-1252）。描画はその旧フレームのまま進む。
- 期限後に遅れて届いたフレームは `seek_boundary_expect` が既に 0 なので配信され得る（同 `1253-1277` の 5 秒安全弁、`2305`）。つまり「予算内に届かない」は恒久的な配信断ではなく、**アプリ側の確定窓を外れる**ことが本質。

### 1.2 アプリ側（フリーズ確定のゲート）

- フリーズ確定は「進入・再ロードの後に届いた、目標と一致するフレーム」だけを数える（D21）。`GapFrameCaptureCoordinator.Decide` は `frameSeenSinceCapture` が偽なら `None`（`src/TimecodeSyncPlayer/GapFrameCaptureCoordinator.cs:23-41`）。フレーム到着は `OnSourceFrameReady` がソースフレーム PTS で判定し、目標 ±2 フレームで `NotifyFrameArrived`、外れれば再シーク予約（`src/TimecodeSyncPlayer/MainWindow.xaml.cs:1956-1977` の 1968-1976）。
- `TryCompleteGapFreezeAsync` は状態が `EnteringFreeze`・ネイティブ seek 完了・一時停止のときだけ判定し、`RenderAndCapture` のときだけキャプチャ（状態機械の完了）を行う（同 `1911-1948`）。`IsNativeGapFreezeTargetReady` も同じく `frameSeenSinceCapture` を要求する（同 `2043-2056`）。`RenderSession.TryCaptureGapFreezeFrameAsync` 自体は画素を撮らず、世代と状態だけを確認する（`src/TimecodeSyncPlayer/RenderSession.cs:146-154`。画素は `ComposeLayer.SaveFreeze` が担う）。
- フリーズの timeout は 3.0 秒（`src/TimecodeSyncPlayer/GapFreezeHandler.cs:54` の `TimeoutSec`、判定 `189-192`）で、pump 予算 4000ms より**先に**切れる。タイムアウトは `ForceFreezeComplete`（同 `178-187`）で「確定画像なし」のまま `FreezeComplete` にし、確定済みの最終画像としては再利用しない。
- 進入中・確定待ちの描画は Hold（直前キャンバス）、確定後は GapFreeze。`frozen` が無ければ Held を描く（`src/TimecodeSyncPlayer/GapRenderFramePolicy.cs:14-25`、`src/TimecodeSyncPlayer/Output/ComposeLayer.cs:14-20`）。したがって、フレームが確定窓に間に合わないと**前の Held（直前に合成したキャンバス）が残る**。
- D21-b の再シークは最大 2 回（`GapFreezeHandler.cs:58` の `MaxSeekRetries`、`151-159`）。1 回ごとにシム世代が進み、追跡中のソース画像は破棄され（`OutputEngine.cs:1156-1172`、特に 1165）、`StartedAt` もリセットされて実効の待ちが延びる。救えないと旧 `frozen` か Held のままになる。

## 2. ギャップ進入の経路ごとの結論

判定は `GapFreezeHandler.BuildFreezeEnterAction`（`src/TimecodeSyncPlayer/GapFreezeHandler.cs:328-418`）が行い、実行は `src/TimecodeSyncPlayer/GapEnterCoordinator.cs` が担う。

### (a) 現在の絵を確定（UseCurrentFrame）

- 条件: 直前トラック＝ロード中トラックで、位置が最終フレーム ±1 フレーム（`GapFreezeHandler.cs:405-413`。位置は `get_time_pos`）。
- 実行: `EnterFreezeCaptureWithCurrentFrame` が `frameSeenSinceCapture=true` で進入し、seek しない（`GapFreezeHandler.cs:138-142`、`GapEnterCoordinator.cs:118-150`）。
- 届かない問題は起きない代わりに、**確定条件が位置だけ**。§1.1 の「位置は目標・絵は旧世代」の状況では、古い絵を最終フレームとして確定し得る。チャネルとしては最有力の一つ。
- この経路は render 世代を進めないため、前回のフリーズ画像（`frozen`）が残っていれば後述の再利用に当たる。

### (b) SeekToFinalFrame（同じトラック内で最終フレームへ）

- 実行順: `EnterFreezeCapture`（フレーム未到着）→ `PauseForGap` → `SeekTo(target)`（`GapEnterCoordinator.cs:77-112`、capture 開始が seek より先なのは D21-b の意図）。
- フレームが届かない場合: capture 不成立のまま 3 秒で `ForceFreezeComplete` → GapFreeze + `frozen==null` → Held（直前キャンバス）が残る。位置は pause 時点の再生位置のまま。
- 追加の盲点: **この経路は render 世代を進めない**（load も `ClearGapFreezeFrame` も通らない）。`ComposeLayer.Compose` は `frozen == null` のときだけ `SaveFreeze` するため（`Output/ComposeLayer.cs:131-152`、特に 140）、前回のフリーズ画像が残っていると**新しい目標のフレームが届いても保存されず、前回の絵を描き続ける**。`frozen` のクリアは render 世代変更か `SetCanvas`/`Dispose` のみ（`OutputEngine.cs:1139-1144`、世代は `MainWindow.xaml.cs:523-540` が `SubmitOutputState` で渡す）。
- 観測 F-4 の「1 エピソード前の tail」はこの型が第一候補（b を、直前トラックがロード済みのギャップで使った場合）。

### (c) LoadPreviousTrackFinalFrame（直前トラックをロード）

- 実行順: `LoadPausedAt`（開始位置つき一時停止ロード）→ `ResetPlayerStateForNewTrack` → `SeekTo(target)`（`GapEnterCoordinator.cs:230-280`、seek は 262）。
- `ResetPlayerStateForNewTrack` は `_renderSession.Invalidate()` を呼び（`MainWindow.xaml.cs:2335-2345`）、`PlaybackOperationsCoordinator.LoadFile` もロード時に `ClearGapFreezeFrame` を呼ぶ（`src/TimecodeSyncPlayer/PlaybackOperationsCoordinator.cs:42-43`）。この経路は前回フリーズを引きずらない。
- フレームが届かない場合は Held（直前キャンバス）が残る型。4K CPU は「単独 open 1.2〜2.3 秒」＋プロファイル試行があるため、ロード直後のフレームが 3 秒を超えやすい。

### D22（先頭オフセット）LoadNextTrackFirstFrame

- 実行: 次トラックを `MediaIn` で一時停止ロードするだけで、再シークしない（`GapEnterCoordinator.cs:190-228`）。
- ロードのプリロールフレームが届かないと `frameSeenSinceCapture` が立たず、タイムアウトで Held（直前サイクルの絵）が残る。観測 F-5 の「前サイクル側の tail に近い絵」と整合。

### 候補の整理と判別

| 候補 | 機構 | 出る観測 | 判別に使う記録 |
| --- | --- | --- | --- |
| P1 | フレーム未到着 → capture 不成立 → Held が残る（(a) 以外の全経路） | 位置は pause 点のまま、絵は直前キャンバス | `pump deadline` 行、`gap freeze final-frame capture timed out`、`compose.sourcePending` |
| P2 | (a)/(b) が render 世代を進めず、`frozen==null` ガードで前回フリーズを再利用 | 1 エピソード前の tail が残る（F-4 の型） | `gap.enter`、`load.issue/load.return`、render 世代の遷移（`lifecycle` トレース）、`compose.acquire` の PtsNs |
| P3 | D25 の境界近似で「先行 seek のフレーム」が新世代の最初として配信され、±2 フレームゲートが目標の近い別 seek の絵を通す（`tcs_gstreamer.cpp:2501-2513` のコメントが「owner の着地ゲートが弾いて再シークする」と明記） | 誤った絵が `SaveFreeze` される | `lease: acquire gen/seq/slot`、`source.acquire`、`compose.acquire`、`stale gap freeze source frame, reissuing final-frame seek` |
| P4 | (a) は位置だけで確定するため、位置クエリのフォールバック（pipeline 位置＝目標）で古い絵を確定 | 古い絵が最終フレームとして固定 | `GapRenderFrameDecision` を決めた入力（位置・目標・fps）、`TryGetTimePos` の値、`lease: acquire` |

## 3. F-1「終端 0.17 秒手前（60fps で 10 フレーム）」の順序

- 同期 ON では終端の自動前進をしない（`src/TimecodeSyncPlayer/ContinueModePlaybackPolicy.cs:12-15`）。`GapFreezeHandler.EndAdvanceThresholdSec`（0.15 秒、`GapFreezeHandler.cs:55`）は sync OFF 用で、F-1 の契機は「LTC が A.End を越えて写像がギャップになった」こと（`src/TimecodeSyncPlayer/LtcSyncController.cs:932` 以降の Gap 分岐）。
- 順序: (1) LTC フレームでギャップを検知、(2) `atFinalFrame` を位置クエリで判定（`GapFreezeHandler.cs:405-413`）、(3) 偽なら `PauseForGap`、(4) `EnterFreezeCapture`、(5) `SeekTo(duration − 1/fps)`（`GapEnterCoordinator.cs:77-100`）。**pause が先、最終フレームへの seek は後**なので、seek が成立しない間の保持位置は pause した時点の再生位置のままになる。
- 60fps で 10 フレーム（0.167 秒）になる条件は固定値ではない。候補は (i) LTC 25fps と映像 60fps の格子差（LTC 1 フレーム＝2.4 映像フレーム）、(ii) ギャップ検知〜pause までの LTC／パイプライン残差、(iii) 合成遅延の合計。`atFinalFrame` の許容は ±1 フレーム（`GapFreezeHandler.cs:407-410`）なので、残差が 2 フレームを超えると seek 経路に落ち、(iii) が効く。位置 24.833 は「pause 点がそのまま残った」値として説明できる。
- EOS と重なった場合（終端へ seek 中に EOS 到達）は新世代のフレームが来ず、位置は EOS 側の値、絵は Held という組み合わせになり得る（`GStreamerSource.cs:159-164`、`tcs_gstreamer.cpp:2525-2529`）。正確な値の由来は再現時に「ギャップ検知時の position / seek target / pump 期限 / latest_pts と世代」を同時記録して切り分ける。

## 4. 開発機での再現案（実素材不要）

### 4.1 素材

`scripts/make-e2e-media.ps1` に 4K の色素材を 3 本追加した（A/B/C の 3 トラック分。別コミット）。head 0〜1 秒（C には無し）・body・tail 最終 1 秒の構成は既存 3 本と同じで、12 秒。ファイル名は既存 3 本より後ろに並ぶ新規名のため、「名前順の先頭 3 本」を選ぶ既存の実行には影響しない。3 本とも CPU デコードになる組み合わせ。

- 4K60 ProRes: `prores_ks` profile 3（422 HQ）・`yuv422p10le`・`-g 60`（CPU デコード）
- 4K24 AV1: `libsvtav1` preset 10・`yuv420p`・`-g 24`（CPU デコード）
- 4K60 ProRes（C 用）: 同じ ProRes 設定。head 色なし・tail は既存 C と同じ色

### 4.2 実行

```
scripts\run-ltc-scenarios.ps1 -MediaDir artifacts\media -Media <4K 2 本と必要な C> -Cycles 3
  -ReportDir TestResults\ltc-scenarios\<任意>
  -Filter "FullyQualifiedName~F1_|FullyQualifiedName~F3_|FullyQualifiedName~F4_|FullyQualifiedName~F5_"
```

### 4.3 決定論的にするための環境変数

- `TCS_PUMP_BUDGET_MS=500〜1000`: 到着遅れを強制（4K 素材でも再現が速く、揺れが小さい）
- `TCS_FORCE_DECODER_ADAPTER_MISMATCH=1` + HEVC 4K60 10bit: CPU プロファイルを強制（`tcs_gstreamer.cpp:1704`）
- `decodeMode=software`: 既存の CPU デコーダ優先設定（設定ファイル側）

### 4.4 判別に使うログ・トレース

- アプリ: `pump deadline`（目標・復号数・経過・位置）、`stale gap freeze source frame, reissuing final-frame seek`、`gap freeze final-frame capture timed out`、`gap freeze activated, final frame captured`、`Continue mode: entering gap freeze ... target=...`
- 出力トレース（`TIMECODE_SYNC_PLAYER_OUTPUT_TRACE`）: `compose.acquire`（PtsNs）・`source.acquire`・`compose.sourcePending`・`compose.fencePending`・`gap.enter`・`lifecycle`（世代遷移）
- シム: `lease: acquire gen/seq/slot`、`seek: ts gate opened`、`seek boundary`
- シナリオジャーナル: `freeze-observation` / `hold-observation` の nearest color、`jump-black-summary`

### 4.5 見込み所要

- 素材生成: 4K 1 本あたり 30 秒前後（実測ベース）、3 本で 2 分前後
- E2E: F-4 3 サイクルで 2 分前後、F-1/F-3/F-5 を含めて 10〜20 分
- 注意: `CheckFreeze` の待ちは 3.0 秒（`tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs:1402-1407`）でハンドラ timeout と同値。「遅れて更新」と「更新されない」の区別には、待ちを延ばした手動観測を併用する。

### 4.6 P1/P2 の判別（再現時に同時記録する）

再現で確かめる仮説を P1（ロード／シークのフレームが 3 秒の timeout に間に合わず Held が残り、遅れて届いたフレームでは更新されない）と P2（(a)/(b) が render 世代を進めず `frozen==null` ガードで前回のフリーズ画像を再利用）に絞り、次を 1 つの時系列で同時に記録する: `pump deadline`、`gap freeze final-frame capture timed out`、`gap freeze activated, final frame captured`、`stale gap freeze source frame, reissuing final-frame seek`、`lifecycle`（render 世代と shim 世代の遷移）、`compose.acquire`（PtsNs・status・seq）、`compose.sourcePending`、`gap.enter`、シナリオジャーナルの `freeze-observation`。

- P1 の判定: 進入〜保持の間に、目標へ一致する PtsNs の `compose.acquire` が 1 度も無く、`pump deadline` と `capture timed out` が出る。遅れて届いた直後に PtsNs が目標へ一致し、`final frame captured` が出ない（または絵が更新されない）ことを確認する。
- P2 の判定: 直前のフリーズ以降に render 世代が進んでいない（`lifecycle` に遷移が無い）ことを確認し、今回のギャップで目標一致の PtsNs が届いても `freeze-observation` の nearest color が前エピソードのままであることを見る。`SaveFreeze` は `frozen==null` のときだけ走るため、**一致フレームの到着と絵の更新が食い違えば P2**。
- 両者は排他ではない。P1 で Held が残った後に P2 が乗る複合があり得るため、判別は「目標一致フレームの到着有無」×「絵の更新有無」の 2×2 で行う。

## 5. 未確定点

- 検証機の参照 PNG が古い可能性（F-3/F-5 の d=0.0）は検証機側の確認待ち。ここが古い場合、観測の一部は画面と無関係に出る。
- P2 と P3 のどちらが F-4 の主因かは、世代遷移と `compose.acquire` の PtsNs を突き合わせるまで確定しない。
- F-1 の 10 フレームの内訳（(i)〜(iii) の寄与）は、上記の同時記録で切り分ける。
