# D33 解析: Single の MediaOut 終端停止と FetchMetadata 欠落

- 作成: 2026-09-17、同期担当（`agent-a`）
- 基点: main（D32 統合後）
- 範囲: 机上解析（コード変更なし）。検証機の候補 2 で Single の S-3 が実素材で失敗した件と、`FetchMetadata` のログ欠落の解析
- 前提: 実素材の A は MediaIn=5 / MediaOut=25、素材の全長は 2 分超。生成素材では尺 = MediaOut のため EOS で止まり、この差が見えない

## (1) LTC が MediaOut を越えたとき、MediaOut で止まる仕組みは無い

- D29 の clamp は「シーク先」を [MediaIn, MediaOut] に収めるだけです。粗い判定は `clipOut = MediaOutSeconds ?? DurationSeconds` を作り `target = Math.Clamp(ltcSeconds, clipIn, clipOut)`（`src/TimecodeSyncPlayer/SyncDecisionEngine.cs:54-63`）。先行補償後も同じ範囲へ clamp します（同 `:74-78`）。着地後は `|clipOut − playback| ≤ tolerance` で `None` になり、以降シークは出ません。
- Single の同期本体（`src/TimecodeSyncPlayer/SingleModeSyncCoordinator.cs:23-69`）は「総合判定 → シーク発行」だけで、終端の監視・一時停止はありません。したがって **MediaOut に着地したあとも再生は進み続けます**。
- 実際に止まるのは GStreamer の EOS だけです（`src/TimecodeSyncPlayer/Output/GStreamerSource.cs:159-164` が `SourceStatus.Ended` を返す。`Output/OutputEngine.cs:954` はトレースのみ。`onEnded` は `_gstBackendState.Seeking.NotifyEnded` に接続され、シーク状態の後始末だけ: `src/TimecodeSyncPlayer/MainWindow.xaml.cs:654-655`、`:699-700`）。素材の全長 > MediaOut の実素材では EOS が来ないため、位置が 31.4 / 31.1 まで進みます。
- 進み続ける力は補正です。Single 分岐の残差は `residual = ltcSeconds − playback`（例: 40 − 25 = 15 秒）、`targetSeconds = ltcSeconds`（生値）で（`src/TimecodeSyncPlayer/LtcSyncController.cs:713-720`）、Smooth は `rate = 1 + clamp(e/1.0, ±0.10)`（`src/TimecodeSyncPlayer/SyncCorrectionController.cs:37`、`:56`、`:256-259`）。LTC が前方にあるほど +10% で進み、MediaOut から離れます。粗い判定は差が tolerance を超えると 25 へ戻すシークを出しますが、その間も位置は進み、保留・デバウンス・セトルの抑止で 25 に留まりません。
- **Jump 補正は D29 の clamp を通っていません**。`SyncCorrectionController.EvaluateJump` は生の `targetSeconds` を返し（同 `:155-184`）、`LtcSyncController` がそれをそのままシークします（`src/TimecodeSyncPlayer/LtcSyncController.cs:741-751`）。Jump 設定では MediaOut を越えるシークが発行され得ます（既定の Smooth では上記のレート暴走）。
- 終端の自動前進は sync ON では動きません（`src/TimecodeSyncPlayer/ContinueModePlaybackPolicy.cs:12-15`、`MainWindow.xaml.cs:1832-1855` の `TryAdvancePlaylistAtEnd` は `ShouldAutoAdvanceAtMediaEnd` が偽で早期 return）。ここが sync ON の終端処理の空席です。

### 直し方の当たり

- `SingleModeSyncCoordinator.Apply` に「`ltcSeconds >= clipOut` かつ `playback >= clipOut − tolerance` なら、シークせず終端ホールド（新しい effect で一時停止＋ラッチ）」を追加する。LTC が `clipOut − tolerance` 未満へ戻ったらラッチを解除して再開する（S-3 の復帰工程がこれを要求）。`SyncPlaybackState` には Single でも `MediaInSeconds` / `MediaOutSeconds` が入っています（`src/TimecodeSyncPlayer/MainWindow.xaml.cs:919-921`）。
- 終端の基準は既存の `GetEffectiveDuration`（MediaOut 基準、`src/TimecodeSyncPlayer/PlaylistTrack.cs:19-24`）と `PlaylistEndAdvancePlanner`（`PlaylistEndAdvancePlanner.cs:44`）と同じ考え方です。ただしこれらは sync OFF 専用なので、sync ON の終端判定として流用します。
- `ApplyCorrection` の Single の残差・シーク先も同じ [MediaIn, MediaOut] に clamp する（Jump の越えを封じる）。
- 一時停止すれば Smooth は `IsPlaybackPaused` ガードで止まります（`src/TimecodeSyncPlayer/LtcSyncController.cs:687-689`）。追加の抑止は不要です。
- 注意: 生成素材（尺 = MediaOut）では従来どおり EOS が受け持つため、二重に止めないこと（ホールドは `MediaOut < 尺` のときだけ意味を持つ）。

## (2) position=25.100 の判定失敗はテスト側の期待値と D29 の不一致

- S-3 は `expectedEnd = A.SingleTarget(outOfRange)` を期待します（`tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs:123-133`）。`SingleTarget` は `Math.Clamp(ltcSeconds, 0, Duration.TotalSeconds)`（同 `:568`）で、`TrackInfo.Duration` には `track.MediaDuration`（素材の全長）が入ります（同 `:556-569`、構築 `:684-686`）。
- 実素材は `MediaDuration`（2 分超）> `MediaOut`(25) のため、期待値は clamp されず `outOfRange`(40) のままになります。製品は D29 で 25 に clamp します。**生成素材は `MediaDuration == MediaOut` なので両者が一致し、EOS で位置が止まって見える**、というのが「生成素材では気づかない」理由です。
- したがって 25.100 の回は「製品は 25 近辺に居たが、期待 40 に届かない」、31.4 の回も同じ不一致です。LTC が回り込みで 3631 だった回は、期待値が素材全長 clamp（120 など）になり比較対象外で妥当です。
- D33 では、製品の MediaOut ホールド（(1)）と、テストの期待値を [MediaIn, MediaOut] clamp に揃える修正の両方が要ります。テスト単独の修正では「止まらない」製品側が残り、製品単独の修正では期待値が合いません。

## (3) FetchMetadata が出ない 5/383 は「100ms タイマー＋ロード毎リセット」の競合

- メタデータ取得は 100ms タイマーの tick だけで行われます（`src/TimecodeSyncPlayer/MainWindow.xaml.cs:94` の `TimerIntervalMs=100`、`:1802-1807` の `if (!_metadataFetched && _duration > 0) FetchMetadata();`。`_duration` は同じ tick の `TryGetDuration` で更新）。`FetchMetadata` は `TryGetSize` が未取得だと `_metadataFetched` を立てずに戻る保険付きです（同 `:2262-2276`）。
- トラックロード毎に `ResetPlayerStateForNewTrack` が `_metadataFetched=false` / `_duration=0` に戻します（同 `:2335-2345`。呼び出しは `src/TimecodeSyncPlayer/PlaybackOperationsCoordinator.cs:74`、`:100`、ギャップ系コーディネーター）。キャッシュ済みプロファイルで 250ms 未満で返るロードが次のロードに 1 tick 以内で置き換わると、`_duration > 0` の tick が来ないままリセットされ、そのトラックのログ行が出ません（tick 冒頭の `if (!IsPlayerReady) return;`（`:1796`）でも同じ）。
- 影響はログ欠落だけでなく、`_vm.Player.MetaLine` が前トラックの表示のまま残り得ることです（更新は成功時のみ）。

### 直し方の当たり

- ロード完了側で 1 回取得を予約する（`PlaybackOperationsCoordinator.LoadFile` の成功後、または位置つきロードで呼ばれる `RefreshPositionDisplayAfterLocatedLoad`（`:1337-1348`）から `Dispatcher.BeginInvoke(FetchMetadata)`）。サイズ未取得ならフラグが立たないため、タイマー再試行と共存できます。
- 根本策は shim のロード結果（`loaded ... %dx%d@fps`）から幅・高さ・fps を返し、ロード時にメタデータを確定することです。

## 主要な参照（ファイル:行）

- `src/TimecodeSyncPlayer/SyncDecisionEngine.cs:54-63`、`:74-78`（D29 の clamp）
- `src/TimecodeSyncPlayer/SingleModeSyncCoordinator.cs:23-69`（Single の同期本体。終端処理なし）
- `src/TimecodeSyncPlayer/LtcSyncController.cs:681-753`（補正。Single の残差・生値シーク、Jump シーク）、`:687-689`（一時停止ガード）
- `src/TimecodeSyncPlayer/SyncCorrectionController.cs:37`、`:56`、`:155-184`、`:256-259`
- `src/TimecodeSyncPlayer/ContinueModePlaybackPolicy.cs:12-15`、`src/TimecodeSyncPlayer/MainWindow.xaml.cs:1832-1855`（sync ON では終端の自動前進をしない）
- `src/TimecodeSyncPlayer/PlaylistTrack.cs:19-24`、`src/TimecodeSyncPlayer/PlaylistEndAdvancePlanner.cs:44`（MediaOut 基準の実効尺）
- `src/TimecodeSyncPlayer/Output/GStreamerSource.cs:159-164`、`src/TimecodeSyncPlayer/Output/OutputEngine.cs:954`、`src/TimecodeSyncPlayer/MainWindow.xaml.cs:654-655`、`:699-700`（EOS はシーク状態の後始末のみ）
- `tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs:123-141`、`:556-569`、`:684-686`（S-3 と期待値の計算）
- `src/TimecodeSyncPlayer/MainWindow.xaml.cs:1802-1807`、`:2262-2284`、`:2335-2345`、`:94`、`:1796`、`:1337-1348`（FetchMetadata とロード毎リセット）
