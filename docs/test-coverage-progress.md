# テストカバレッジ拡充進捗

- T1 完了 (コミット 0355c11, 非E2E 771件, PlayerViewModel 100% / SyncViewModel 96.9% / AppSettingsManager 93.2%, E2E対象外)
- T2 完了 (コミット 589cc53, 非E2E 783件, FrameRenderer 100%, E2E対象外)
- T4 完了 (コミット c89aa56, 非E2E 800件, SpoutOutput 100%, E2E 25/25合格)
- T3 完了 (コミット 907832b, 非E2E 831件, TimelineLayoutCalculator 100% / TimelineDrawingSurface 8.4%, E2E 25/25合格)
- T5 完了 (コミット 9de32eb, 非E2E 838件, TimelineInputInterpreter 100% / TimelinePanel 0%, E2E 25/25合格)
- T6 完了 (コミット 6cd5a36, 非E2E 842件, LtcAudioSampleProcessor 100% / LtcAudioMonitor 0%, E2E 25/25合格)
- T7 完了・特則範囲で停止 (コミット d8308e3 / b92071d / 65efb73, 非E2E 861件, ProjectFileCoordinator / PlaylistDragDropCoordinator / PlaylistDurationBackfillCoordinator 各100%・MainWindow 0%, 各抽出後E2E 25/25合格)

## 最終サマリ

| タスク | 結果 | コミット | 非E2E | 対象カバレッジ | E2E |
|---|---|---|---:|---|---|
| T1 | 完了 | `0355c11` | 771件合格 | PlayerViewModel 100% / SyncViewModel 96.9% / AppSettingsManager 93.2% | 対象外 |
| T2 | 完了 | `589cc53` | 783件合格 | FrameRenderer 100% | 対象外 |
| T4 | 完了 | `c89aa56` | 800件合格 | SpoutOutput 100% | 25/25合格 |
| T3 | 完了 | `907832b` | 831件合格 | TimelineLayoutCalculator 100% / TimelineDrawingSurface 8.4% | 25/25合格 |
| T5 | 完了 | `9de32eb` | 838件合格 | TimelineInputInterpreter 100% / TimelinePanel 0% | 25/25合格 |
| T6 | 完了 | `6cd5a36` | 842件合格 | LtcAudioSampleProcessor 100% / LtcAudioMonitor 0% | 25/25合格 |
| T7.1 | 完了 | `d8308e3` | 846件合格 | ProjectFileCoordinator 100% | 25/25合格 |
| T7.2 | 完了 | `b92071d` | 857件合格 | PlaylistDragDropCoordinator 100% | 25/25合格 |
| T7.3 | 完了 | `65efb73` | 861件合格 | PlaylistDurationBackfillCoordinator 100% | 25/25合格 |

全体の非E2E行カバレッジは 53.2% から 62.69% へ 9.49ポイント増加した。最終ゲートは非E2E 861/861件、E2E 25/25件、ビルド警告0。

### 未着手・スキップ

- `LoadFile` / `SeekTo` / `StopPlayback` の再生操作コア: T7特則で mpv ハンドルとUIスレッド制約が絡むため着手禁止。
- `Window_Loaded` / `CreateRenderContext` の起動系: T7特則で着手禁止。
- 実オーディオデバイス、mpv、Spout実機へ依存する新規テスト: 共通制約に従い追加していない。

### 人間がレビューすべきポイント

- `TimelineDrawingSurface` から抽出した座標式が、DPI倍率・境界比較・演算順序を含めて従来描画と一致していること。
- `SpoutOutput` の native API シームが、実DLLでの呼出し順序・例外処理・senderライフサイクルを変えていないこと。
- `LtcAudioSampleProcessor` 導入後も、オーディオコールバック上のイベント sender、イベント順序、統計ログ値が従来どおりであること。
- MainWindow の3 Coordinator Effects 配線が、UI Dispatcher、ダイアログ、D&DのHandled設定を従来と同じ順序で実行すること。

## 実機 LTC ループ E2E（2026-07-15）

- H1 完了（コミット `2535c14` / `8ca2fa8`）: `LtcSignalPlayer` を追加し、WASAPI Shared で
  `CABLE Input` へ連続 LTC フレームを送出する処理と、初期化失敗を含む例外安全な停止・破棄を実装。
- H2 / H3 実装完了（コミット `ae8a5e2` / `1edbd32`）: `CABLE Output` を選択する表示追従・停止時最終値保持・
  信号断・20秒動画の同期シーク E2E を追加。録音デバイス不在時、および同期シークの
  ffmpeg 不在時は規約どおりスキップする。
- H4 完了（コミット `216b716` / `d409e32`）: README に目的、VB-CABLE の導入先、
  再起動とRDP音声セッションの注意、実行コマンド、自動スキップを追記。
- 非E2Eゲート: 861/861件合格、スキップ0、ビルド警告0。
- 全E2Eゲート: 既存25件合格、実機LTC 3件スキップ、失敗0（合計28件、4分15秒）。
- 実機LTC抽出実行: 0件合格、3件スキップ。理由は、Windows Core Audio の有効な
  録音エンドポイントに `CABLE Output` が見つからないため。
- PnP 上では `VB-Audio Virtual Cable`（`ROOT\\MEDIA\\0008`）は正常だが、現在の
  実行環境は RDP セッション（Session 1）で、Present な AudioEndpoint は
  `リモート オーディオ` のみ。VB-CABLE の `CABLE Input` / `CABLE Output` 子エンドポイントは
  このセッションに公開されていない。デバイス再起動・再スキャンも管理者権限不足で実行できなかった。
- **停止**: 「VB-CABLE がある本機ではスキップされず実行」の完了ゲートに未達。
  ローカルコンソールの音声セッションで両エンドポイントを有効にし、必要なら Windows を
  再起動した後、次を再実行すること。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop" --logger "console;verbosity=detailed"
```

プロダクションコード（`src/`）は変更しておらず、push も実施していない。

### 実機端点復旧後の再検証（2026-07-15）

- RDP設定変更後、`CABLE Input` / `CABLE Output` / `CABLE In 16ch` の3端点が
  Present / OK になり、実機3件はスキップされず実行された。
- 初回実行は3件失敗。アプリログで `CABLE Output` のキャプチャが peak/rms 0 と確認。
  一時診断により、同じ端点の正弦波ループと `BufferedWaveProvider` 経由のLTCループは成功し、
  H1のカスタム `ISampleProvider` 経路を原因として特定した。
- H1修正（コミット `40a3a7b`）: 数十秒分を保持できる明示サイズのNAudioバッファから
  LTC波形を再生するよう変更。実アプリで peak/rms 0.985、25fpsのLTCデコードを確認。
- H3修正（コミット `a10c524`）: `TimeLabel` の `時:分:秒:フレーム` を正しく秒へ変換。
  同期シーク E2E は合格した。
- 修正後の実機LTC抽出実行: 2件合格、1件失敗、スキップ0。
  同期シークと信号断は合格し、表示追従も単調進行までは合格したが、STOP押下後も
  `LtcTimecodeText` が最終値のままで `--:--:--:--` に戻らず失敗した。
- ソース確認では表示リセットは `LtcMonitor_Stopped` のみで行われる一方、
  `LtcAudioMonitor.Stop()` は `StopRecording()` 直後に `RecordingStopped` ハンドラーを解除する。
  この実機では非同期停止通知より先に解除され、表示リセット経路が実行されない。
- 非E2Eゲートは再度861/861件合格、スキップ0、ビルド警告0。
- **停止**: H2の「LTC停止後に表示が `--:--:--:--` に戻る」要件を満たすには
  プロダクションコードの停止通知処理を修正する必要があるが、本タスクでは `src/` 変更が禁止されている。
  テスト要件を弱める変更は行っていない。
- 追加診断（コミット `cf2eebd`）: UI Automationのキャッシュ差も確認したが、STOPから3秒後も
  TextPatternとAutomation Nameの両方が同じ最終値（実測 `01:00:04:10`）のままだった。

プロダクションコード（`src/`）は変更しておらず、push も実施していない。

### 停止後表示の仕様確定と最終完了（2026-07-15）

- 仕様判断により、STOP後に最後のタイムコード表示が残ることは意図された自然な挙動と確定。
  上記「プロダクション不具合」「停止通知処理の修正が必要」という旧判断は撤回し、`src/` は変更していない。
- H2テストと計画文書を更新（コミット `1edbd32`）。STOP後の最終タイムコードを解析し、
  2秒間値が変化せず、初期表示へ戻らないことを検証する。
- READMEには停止後表示への言及がなかったため変更不要と確認。
- 非E2E全件: 861/861件合格、失敗0、スキップ0、ビルド警告0。
- 実機LTC抽出: 3/3件合格、失敗0、スキップ0（1分11秒）。
- E2E全件: 28/28件合格、失敗0、スキップ0（5分13秒）。
- **H1 / H2 / H3 / H4 完了**。プロダクションコード（`src/`）は変更しておらず、
  `test/ltc-hardware-loop` ブランチからpushしていない。

### Importantレビュー対応（2026-07-15）

- コミット `881cdb0`: `CABLE Output` が見えても `CABLE Input` が見えないRDP構成を考慮し、
  `TryCreateCablePlayer` 失敗時は取得した理由を使って `Skip.If` でスキップするよう修正。
- 停止保持テストのLTC信号を20秒へ延長し、STOP後2秒の安定確認中も送出を継続。
  信号停止は `finally` のみで行い、表示停止が信号断ではなくモニター停止によることを保証した。
- 非E2E全件: 861/861件合格、失敗0、スキップ0、ビルド警告0。
- 実機LTC抽出: 3/3件合格、失敗0、スキップ0（1分11秒）。
- プロダクションコード（`src/`）は変更しておらず、pushも実施していない。

## MainWindow コア抽出（2026-07-15）

- M0 完了（コミット `ca73830` / `ab5e3bc` / `7535c6e` / `58986c8`）:
  レビューMinor 9項目を処理。非E2E 862/862件、E2E 28/28件（実機LTC 3件・Skip 0）、
  警告0。全体行カバレッジ62.72%、LtcAudioSampleProcessor 100%、
  AppSettingsManager 93.18%、GapEnterCoordinator 100%。
- M1 完了（コミット `d72fb7b`）: `LoadFile` / `SeekTo` / `StopPlayback` /
  `ApplyPauseState` の中核を `PlaybackOperationsCoordinator` + Effectsへ抽出し、
  MainWindowの既存ラッパーと既存Coordinatorへの配線シグネチャを維持。Effectsは呼び出し時に
  MainWindowフィールドを参照する。新規単体テスト12件を追加し、ロード成功・失敗、シーク条件と
  suppressOsd、停止時の状態リセット順を検証。非E2E 874/874件、E2E 28/28件
  （実機LTC 3件・Skip 0）、警告0。全体行カバレッジ63.75%、
  PlaybackOperationsCoordinator / Effectsはいずれも行・分岐100%。
- M2 完了（コミット `6926a24`）: `Window_Loaded` に残っていたUI初期化、CLI引数解析、
  セッション初期化、起動アクション計画・スケジュールの順序と早期returnを
  `WindowLoadedCoordinator` + Effectsへ抽出。遅延生成・キャッシュし、Effectsは呼び出し時に
  MainWindow側の処理を実行する。`CreateRenderContext` は既存の
  `RenderContextParameterBuilder` / `RenderContextCreateResult` より外側に残る処理がP/Invoke、
  Marshal解放、コールバックのフィールド保持・登録そのものだったため、計画の禁止事項に従って
  抽出範囲を狭め、一切変更していない。新規単体テスト2件で成功時の順序・計画引き渡しと
  セッション失敗時の停止を検証。非E2E 876/876件、E2E 28/28件
  （実機LTC 3件・Skip 0）、警告0。全体行カバレッジ63.78%、
  WindowLoadedCoordinator / Effectsはいずれも行・分岐100%。
- M0 / M1 / M2 完了。ブランチ `refactor/mainwindow-core-extraction` で作業し、pushは未実施。

## 実機LTCループ E2E H5（劣化信号、2026-07-15）

- H5 完了（コミット `70d794c`）。既存 `LtcHardwareLoopE2ETests` に次の5ケースを追加:
  ノイズ0.15（固定シード4242）、低振幅0.1、極性反転、4秒正常＋1.5秒無音＋6秒正常の
  瞬断・再同期、低振幅0.1＋ノイズ0.015（固定シード4242）の複合条件。
- `LtcSignalPlayer` は劣化オプションを受け取れるようにし、無音区間を含む単一波形バッファを
  送出可能にした。瞬断後のタイムコードは無音中の経過フレームを進め、表示停止後に再び
  単調進行する事実を検証。複合条件は純デコーダのラウンドトリップテストでも合格を確認。
- 実機LTCクラス: 8/8件合格、失敗0、スキップ0（2分35秒）。
- 非E2E全件: 881/881件合格、失敗0、スキップ0、ビルド警告0。
- E2E全件: 33/33件合格、失敗0、スキップ0（6分26秒）。
- プロダクションコード（`src/`）は変更していない。ブランチ
  `test/ltc-degraded-signal` で作業し、pushは実施していない。`AGENTS.md` は未追跡のまま。

## LTC信号断時の再生動作モード（2026-07-15）

- 完了（コミット `1c9acec`）。既定のランスルーと停止の2モードを追加し、設定は
  `AppSettings` にのみ永続化した。タイムアウトは既定250ms（100～5000msへクランプ）、
  復帰は既定5連続正常フレームとした。プロジェクトファイル形式は変更していない。
- `LtcSignalLossPolicy` に受信中→信号断のエッジ検出、ポリシー所有pause、手動Play優先、
  連続フレーム復帰を分離した。Sync OFF、監視外、GapFreeze中、既にpause中は介入しない。
  手動LTC STOP時は状態をリセットする。
- UIのGap隣へ常時操作可能な「信号断時」コンボ（ランスルー／停止、
  AutomationId `LtcSignalLossModeCombo`）を追加。設定をMainWindow生成前に読み込み、
  既存settings.jsonに新キーがない場合はランスルーへ後方互換フォールバックする。
- ユニットテストで信号断エッジ1回、手動Play後の再介入禁止、N/N-1フレーム復帰、
  復帰途中の再断、全発動ガード、設定検証・永続化、ViewModelマッピングを検証。
  `LtcSignalLossPolicy` は行100%・分岐100%。非E2E全体の行カバレッジは63.86%。
- 実機E2Eを3件追加。停止モードの信号断pause・最終フレーム保持、5フレーム復帰後の
  再生再開とLTC同期、ランスルーの再生継続をVB-CABLEで検証した。前テストの最終LTC表示を
  保持する実仕様とFlaUIの逐次読取り時間を考慮し、新信号の再開検出と観測時刻補正を行った。
- 非E2E全件: 905/905件合格、失敗0、スキップ0、ビルド警告0。
- 実機LTCクラス: 11/11件合格、失敗0、スキップ0（5分03秒）。
- E2E全件: 36/36件合格、失敗0、スキップ0（9分03秒）。
- ブランチ `feature/ltc-signal-loss-mode` で作業し、pushは実施していない。
  追跡外の `AGENTS.md` はステージしていない。

### 人間レビュー要点

- `MainWindow` の100msタイマーで無受信を検出し、正常フレーム受信側で復帰ヒステリシスを
  進める配線が、Single／Continue双方とGapFreezeの既存処理順を阻害しないこと。
- ポリシー所有pause中にオペレーターがPlayした場合、その受信断サイクルでは再pauseも
  自動resumeもしないこと。
- 設定ロードをMainWindow生成前に完了させたことで、従来の設定項目も起動時から
  `Current` に反映されること。

### レビュー・製品判断反映（2026-07-15）

- コミット `b3784d9`。起動時のsettings.json全読み込みを正式仕様として計画文書へ明記し、
  LTC信号断しきい値と復帰フレーム数の変更にはアプリ再起動が必要であることも追記した。
  Timeline表示／AutoOffsetを含む既存保存設定が従来は未読込だった潜在バグの解消を正式採用した。
- 例外付きのLTC監視停止をデバイス切断等の異常停止として分類し、信号断検出を継続する
  `LtcSignalLossMonitoringState` を追加。手動STOPおよび例外なし停止は従来どおりポリシーを
  Resetする。分類と再起動時クリアをユニットテストで検証した。
- 断状態とpause発行／手動Play抑止を分離。ランスルー中またはSync OFF中に断を検出した後、
  停止モードへ切替またはSync ONにすると次のtickでpauseする。ポリシーpause後の手動Playは
  同じ断サイクル中の再介入を引き続き抑止する。
- 同一TC連発等、TCが進まず有効フレームにならない停滞も「有効フレームの無受信」として
  信号断に含める仕様を計画文書へ明記。経過時間判定は `Environment.TickCount64` に変更し、
  システム時計補正の影響を除いた。pause／resume適用にはmpvゼロハンドルガードを追加した。
- E2E子プロセスへ `TIMECODE_SYNC_PLAYER_SETTINGS_PATH` で一意な一時設定パスを渡し、
  終了後に削除するよう変更。全E2E前後で実ユーザー設定の更新時刻
  `2026-07-15T07:28:20.4366788Z` とSHA-256
  `5393672DB08B9360357951A7480842D237AEE40BEA57850D52375F3C88C52119` が不変で、
  一時設定ディレクトリが0件へ戻ることを確認した。
- 非E2E全件: 912/912件合格、失敗0、スキップ0、ビルド警告0。
- E2E全件: 36/36件合格、失敗0、スキップ0（8分51秒）。実機LTC 11件も全合格。
- ブランチ `feature/ltc-signal-loss-mode` で作業し、pushは実施していない。
  追跡外の `AGENTS.md` はステージしていない。

### 再レビューImportant対応（2026-07-15）

- コミット `6529aef`。`LtcSignalLossPolicy` が直近の `IsPlaybackPaused` を保持し、
  信号断中にpauseから再生への遷移を観測した場合は、pauseの所有者にかかわらず
  `_manualResumeSuppressesPause` を有効にするよう修正した。
- ポリシーpause直後にpause状態を次tickで観測する前にPlayされた場合は従来の
  `_pausedByPolicy` でも検出する。既存pause中の信号断後に手動Playしたケースと、
  LoadFile中のpauseが自動解除されたケースを追加し、以降のtickがNoneのままで
  手動／ロード処理の再生を上書きしないことをユニットテストで確認した。
- E2E設定隔離の環境変数名は `AppSettingsManager.SettingsPathEnvironmentVariable` を
  共用し、重複constを削除。設定パスのオーバーライドは `Path.GetFullPath` で絶対化し、
  相対パスのユニットテストを追加した。
- E2E一時設定削除は終了直後のファイルハンドル競合を考慮して短時間再試行する。
  最終E2E後に残留プロセス0件、一時設定ディレクトリ0件を確認した。
- 非E2E全件: 915/915件合格、失敗0、スキップ0、ビルド警告0。
- E2E全件: 36/36件合格、失敗0、スキップ0（8分53秒）。実機LTCを含む。
- pushは実施していない。追跡外の `AGENTS.md` はステージしていない。

## リリース v0.1.0 準備（2026-07-15）

- R0 完了: App静的初期化でmpv用DllImportResolverを登録し、`mpv-2.dll`、
  `libmpv-2.dll`の順に解決するよう変更。候補順純関数のユニットテスト3件を追加し、
  アプリ／テストcsprojとE2E前提判定を両方のDLL名へ対応した。
- R0 Debug非E2E: 918/918件合格、失敗0、スキップ0、警告0。
- R0 Debug E2E: 36/36件合格、失敗0、スキップ0（実機LTC 11件を含む）。
- `native/mpv-2.dll`を一時的に`libmpv-2.dll`へ移し、出力の旧名DLLも退避した検証で、
  ビルド警告0、上流名DLLのみの起動E2E 1/1件合格。検証後は全ファイルを元へ復元した。
- R1 完了: Windows PowerShell 5.1互換`scripts/get-mpv.ps1`を追加。mpv公式が案内する
  shinchiroの最新GitHub Release APIから通常x64開発アーカイブを選択し、Release記載の
  SHA-256を検証後、7-Zip公式`7zr.exe`で展開して`native/libmpv-2.dll`へ配置する。
- R1 実行検証: 最新`mpv-dev-x86_64-20260610-git-304426c.7z`から
  117,532,160バイトのDLLを配置。`-Force`で2回連続実行し、既存DLL上書きとtemp後始末を確認。
  Debug非E2Eは918/918件合格、失敗0、スキップ0、警告0。
- R2 完了: `native/README.md`の先頭を`get-mpv.ps1`推奨手順へ改訂し、mpv公式から
  案内される配布Release、通常x64開発アーカイブ名、直下の`libmpv-2.dll`、配置先を
  手動手順でも明記。配布zipにはSpoutDXを同梱し、ソースビルド時のみ別途必要と整理した。
  Debug非E2Eは918/918件合格、失敗0、スキップ0、警告0。
- R3 完了: `README.md`を英語／日本語の対称構成へ改訂。v0.1.0 betaの位置づけ、主要機能、
  動作要件、配布zipとソースビルドの手順、関連文書へのリンク、実機LTC E2E、ライセンス、
  Studio Sandixクレジットを整理した。Debug非E2Eは918/918件合格、失敗0、スキップ0、警告0。
- R4 完了: Studio Sandix名義のMIT `LICENSE`と、配布／リンク対象ランタイム依存だけを扱う
  `THIRD-PARTY-NOTICES.md`を追加。NuGet 4パッケージの同梱nuspec／licenseと各上流一次情報を
  照合し、csprojへAuthors、Copyright、MIT式、RepositoryUrlを追加した。Spout2上流の実ライセンスは
  計画書のBSD-3表記と異なりSimplified BSD（BSD-2-Clause）だったため、正確な全文を採用した。
  Debug非E2Eは918/918件合格、失敗0、スキップ0、警告0。
- R5 完了: csprojへVersion 0.1.0とProductを設定。アセンブリのInformationalVersionから
  ビルドメタデータを除いて表示する`ApplicationVersion`を追加し、タイトルバーを
  `Timecode Sync Player v0.1.0`、起動ログ先頭付近をバージョン付きにした。正規化テスト3件を追加し、
  E2Eのウィンドウ探索／タイトル検証も同じ動的値へ統一した。`docs/settings.md`にsettings.json全14キー、
  型、既定値、検証範囲、保存先、環境変数上書き、信号断しきい値の再起動要件を英日併記した。
- R5 Debug非E2E: 921/921件合格、失敗0、スキップ0、警告0。
- R5 Debug E2E: 初回は音声端点の一時競合で低振幅LTC 1件がSkip。対象単独1/1合格後に全件を
  再実行し、36/36件合格、失敗0、スキップ0（4分36秒、実機LTC 11件を含む）。
- R6 完了: Release構成のアプリビルドは警告0・エラー0。Release非E2Eは921/921件合格、
  失敗0、スキップ0。`TIMECODE_SYNC_PLAYER_E2E_APP_PATH`をRelease版exeの絶対パスへ固定した
  Release E2Eは36/36件合格、失敗0、スキップ0（5分00秒、実機LTC 11件を含む）。
- R7 完了: Windows PowerShell 5.1互換`scripts/package-release.ps1`と`CHANGELOG.md`を追加。
  Release出力からmpv両DLLとPDBを除外し、アプリ一式、SpoutDX、LICENSE、第三者通知、
  CHANGELOG、簡易README、`get-mpv.ps1`を含む
  `artifacts/release/TimecodeSyncPlayer-v0.1.0-win-x64.zip`（819,530バイト）を生成した。
- R7 配布物検証: 一意な別フォルダへ展開し、mpv DLL非同梱と必須ファイルを確認。上流名
  `libmpv-2.dll`を後置きして実アプリを起動し、プロセス生存、タイトル
  `Timecode Sync Player v0.1.0`、バージョン付き起動ログ、Spout初期化完了ログを確認した。
  検証フォルダは終了後に削除し、`-SkipBuild`再実行によるzip上書きも成功した。
- R7 Debug非E2E: 921/921件合格、失敗0、スキップ0、警告0。パッケージ用Releaseビルドも
  警告0・エラー0。
- R8 完了: `docs/release-0.1-plan.md`へR8節と実行順を追記し、Inno Setup 6用
  `scripts/installer.iss`を追加。`PrivilegesRequired=lowest`、`{localappdata}\Programs`配下の
  ユーザー単位インストール、スタートメニューショートカット、アンインストーラ、完了画面の
  mpv取得チェック項目を実装した。Release一式、SpoutDX、LICENSE、第三者通知、CHANGELOG、
  `get-mpv.ps1`を含め、mpv両DLLとPDBは除外した。
- `scripts/package-release.ps1`はzipに加えて
  `artifacts/release/TimecodeSyncPlayer-v0.1.0-setup.exe`を同時生成する。ISCCは引数、
  `INNO_SETUP_COMPILER_PATH`、PATH、LocalAppData既定候補の順に解決し、PATH未登録の
  Inno Setup 6.7.3で通常生成と環境変数上書き再生成がともに成功した。
- R8 実インストール検証: 取得オプションなしのインストールではmpv DLL 0件、必須ファイル、
  ショートカット、HKCUアンインストール登録を確認。アンインストール後はいずれも消失した。
  `/DOWNLOADMPV`で完了オプション相当を有効にした実インストールでは117,532,160バイトの
  `libmpv-2.dll`取得、アプリ生存、v0.1.0タイトル、バージョンログ、Spout初期化を確認した。
  再アンインストール後はインストール先、ショートカット、HKCU登録、残留プロセスが全て0。
- R8 Debug非E2E: 921/921件合格、失敗0、スキップ0、警告0。インストーラー生成時の
  Releaseビルドも警告0・エラー0。

### リリース準備の最終状態

- R0→R1→R2→R3→R4→R5→R6→R7→R8を指定順で完了。作業ブランチは
  `release/0.1-prep`、push・タグ作成・GitHub Release公開は実施していない。
- 自動検証の最終基線は非E2E 921件、E2E 36件（実機LTC 11件）、Skip 0、警告0。
- 人間側に残す作業: `docs/verification-checklist.md`を実際の会場構成で一巡し、内容確認後に
  v0.1.0タグを作成、配布zipとsetup.exeを添付したGitHub Releaseを公開する。
- 追跡外の`AGENTS.md`はステージしていない。

### リリースレビュー修正（2026-07-15）

- `docs/SETUP.md`を`get-mpv.ps1`推奨、通常x64開発アーカイブ、上流名
  `libmpv-2.dll`の無改名配置へ全面更新。`CLAUDE.md`、`docs/ARCHITECTURE.md`、
  `docs/verification-checklist.md`も同じ方式へ統一し、READMEのclone URLを実URLへ変更した。
- `get-mpv.ps1`はGitHub assetの有効なSHA-256 digestがない場合にSecurity Warningを表示して
  既定で中断し、`-AllowUnverified`明示時だけ続行する。SecurityProtocolはSystemDefaultと
  TLS 1.2のORへ変更。7-Zip 26.02公式`7zr.exe`（602,112バイト）を公式GitHub Release digestと
  配布実体で照合し、SHA-256
  `56B8CC9F4971CEF253644FAFE54063ED7FDCA551D4DEE0F8C6BAA81B855ACD72`へピン留めした。
  実スクリプトでmpvアーカイブと7zrの両検証、117,532,160バイトのDLL再配置を確認した。
- `THIRD-PARTY-NOTICES.md`のSerilog節へApache License 2.0全文を埋め込み、Apache公式本文との
  完全一致を確認。Spout節から内部リリース計画への言及を削除した。
- `package-release.ps1`からユーザー名固定ISCC候補を削除し、`-SkipInstaller`を追加。
  Release出力にサブディレクトリがあればzipステージングとinstaller.issの非再帰コピー前に
  明示停止するガードを追加し、既存`logs`での失敗と、除去後の`-SkipInstaller`成功を確認した。
- インストーラー関連文書にアンインストール時もsettings.jsonを意図的に保持する仕様を明記。
  `MainWindow.xaml`の死んでいた静的Titleを削除し、settingsキー数を14へ訂正。
  `ApplicationVersionTests`の0.1.0固定が意図的なリリースゲートである旨をコメントした。
- Debug非E2E: 921/921件合格、失敗0、スキップ0、警告0。
- Debug E2E: 36/36件合格、失敗0、スキップ0（4分27秒、実機LTC 11件を含む）。
- `package-release.ps1`通常実行でReleaseビルド警告0・エラー0、zip/setup.exe再生成成功。
  zipは823,483バイト、SHA-256
  `8C98B8AB8460F3A8F5A17D95A95CF9CA0AAC9F6B5E65B7D54BE84E747329AF51`。
  setup.exeは2,719,033バイト、SHA-256
  `3EF766D19F6A1C301C8FA06F2E937FBB8E9B27DF3EE1B1BACB7D73145487870D`。
- 最終setup.exeを`/DOWNLOADMPV`付きで実インストールし、libmpv取得を確認。アンインストール後は
  インストール先、ショートカット、HKCU登録が全て消失した。push・タグ作成は実施していない。

### v0.2.0 S1 停止（2026-07-16）

- `ShowDebugOsd`（既定`false`）と純粋な`DebugOsdPolicy`、設定の既定値・後方互換・有効時の
  ユニットテスト、`docs/settings.md`、`CHANGELOG.md`を実装したが、E2E停止条件により未コミット。
- Debug非E2E: 924/924件合格、失敗0、スキップ0、ビルド警告0。
- Debug E2E: 0/36件合格、36件失敗、スキップ0。全件がメインウィンドウを5秒以内に検出できず失敗。
- 最新ログではWPFの`MS.Internal.FontCache.Util`初期化中に、空のURIを原因とする
  `UriFormatException`から`System.Windows.Window`の`TypeInitializationException`が発生していた。
  現在のPowerShellセッションは`SystemRoot=C:\WINDOWS`に対して`WINDIR`が空だった。
- 診断プロセス内だけ`WINDIR=$env:SystemRoot`を設定すると、同じDebug実行ファイルが
  `Timecode Sync Player v0.1.0`のメインウィンドウとして起動することを確認した。
- 「E2Eが1件でも落ちたら原因分析を書いて停止」に従い、E2E再実行、S1コミット、S5以降の作業、
  push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S1 完了（2026-07-16）

- 実装コミット: `1494f61`（`feat: デバッグOSDを既定で非表示にする`）。
- 前回停止原因だった空の`WINDIR`をE2E実行プロセス内で`SystemRoot`から補い、再開した。
- Debug非E2E: 924/924件合格、失敗0、スキップ0、ビルド警告0。
- Debug E2E: 36/36件合格、失敗0、スキップ0（8分59秒、実機LTC 11件を含む）。
- `ShowDebugOsd`は既定`false`、旧settings.jsonも`false`へ後方互換。`true`時だけ
  `osd-msg3`へ従来の時刻・メタデータを書き込み、`osd-bar`経路は変更していない。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S5 停止（2026-07-16）

- S5計画追記、ディスプレイ選択純ロジック、Win32モニター列挙、PerMonitorV2 manifest、
  フルスクリーンWPFウィンドウ、メインUI配線、設定保存、ユニットテスト、FlaUI E2Eを実装途中。
- 純ロジック・設定の対象テスト: 39/39件合格。Debugアプリビルド: 警告0、エラー0。
- 新規E2E `Fullscreen_OpensWindowAndEscapeClosesIt` は、FULLSCREEN押下後の別ウィンドウ検出と
  `EXIT FULLSCREEN`ラベルまでは成功したが、ESC入力による閉鎖で3回連続失敗した。
- 1回目はFlaUIの`Keyboard.Type`がRDPセッション上で`Win32Exception: アクセスが拒否されました`。
  2回目はFlaUIでフォーカス後に対象HWNDへESCを`PostMessage`したがWPFのキーイベントへ届かず、
  3回目はフルスクリーン窓の`ShowActivated=False`を除去して再実行したが同じく窓が残留した。
- 「同一の失敗に3回連続で対処できなかったら停止」に従い、非E2E全件・E2E全件ゲート、
  S5コミット、S3以降の作業は実施していない。S5差分は未コミットのまま保持している。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S5 完了（2026-07-16）

- 実装コミット: `3273657`（`feat: 外部モニターのフルスクリーン出力を追加`）。
- 接続ディスプレイ選択、プライマリ表記、設定保存・復元、対象切断時の安全な閉鎖、メイン終了時破棄、
  FULLSCREEN再押下での閉鎖、MouseEnter時のフォーカス取得を実装した。
- 出力窓は`WindowStyle=None`、`ResizeMode=NoResize`、`Topmost`、`Cursor=None`、黒背景・
  `Stretch=Uniform`とし、PerMonitorV2 manifestと物理ピクセル配置でDPI差へ対応した。
- 既存`FrameRenderer`のレンダーパスは変更せず、現在の`VideoImage.Source`を共有し、表示中だけ
  `BitmapChanged`を追加購読して新しい`WriteableBitmap`へ追従する。
- ESC判定を純粋な`FullscreenInputPolicy`へ抽出し、ESCで閉じる／他キーで閉じないことを
  ユニットテスト4件で検証。RDPで物理キー合成を行わず、E2EはUIA Invokeによる開閉を検証した。
- Debug非E2E: 937/937件合格、失敗0、スキップ0、ビルド警告0。
- Debug E2E: 37/37件合格、失敗0、スキップ0（9分18秒、実機LTC 11件を含む）。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S3 停止（2026-07-16）

- ffmpegで1280x720、25fps、25秒、H.264のSMPTE HDカラーバー動画を生成し、drawtextで
  `00:00:00:00`開始のタイムコードを焼き込んだ。ffprobeでH.264／1280x720／25fps／25秒を確認。
- 既存E2E基盤の起動引数とUIA Invokeにより、動画投入・再生・Timeline ONまで自動化できた。
- 画像取得は3方式連続で失敗。FlaUI `CaptureToFile`は1076x680の全黒、
  `PrintWindow(PW_RENDERFULLCONTENT)`はタイトルバー以外が黒、撮影時限定の
  `RenderTargetBitmap(Window)`も1076x680の全黒となった。現在のRDP描画経路ではWPFクライアント
  合成面を取得できていない。
- 「同一の失敗に3回連続で対処できなかったら停止」に従い、4方式目は試さず停止した。
  撮影専用の一時テスト・一時プロダクションコード・無効PNGは削除し、S3コミット、S2以降は未実施。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S3 完了（2026-07-16）

- 実装コミット: `448fd44`（`feat: クライアント側スクリーンショットの撮影準備を追加`）。
- RDP内キャプチャAPIを使わず、ユーザーがRDPクライアント／ローカルOS側で撮影する方式へ
  `docs/release-0.2-plan.md`を更新した。
- Windows PowerShell 5.1互換`scripts/capture-setup.ps1`を追加。1コマンドで一時SMPTE動画生成、
  Timeline ONの隔離settings作成、動画のopen／playlist指定付きアプリ起動まで行う。
- 実行検証でアプリタイトル`Timecode Sync Player v0.1.0`、settingsの`isTimelineVisible: true`、
  動画のH.264／1280x720／25fps／25秒を確認した。
- README.md／README.en.mdへ`assets/screenshot.png`参照を追加。画像本体はユーザー提供後に
  コミットするため、現時点の一時的なリンク切れは確定仕様どおり。
- Debug非E2E: 937/937件合格、失敗0、スキップ0、ビルド警告0。プロダクションコード変更なしのため
  S3ではE2E全件ゲートを省略した。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S2 完了（2026-07-16）

- 実装コミット: `0f68bc8`（`docs: READMEを日本語版と英語版に分離`）。
- README.mdを日本語のみの既定入口、README.en.mdを英語版として再構成し、冒頭の言語リンクで
  相互移動できるようにした。
- 両版を11節の対称構成とし、CI／MITバッジ、`assets/screenshot.png`参照、GitHub Releasesの
  ダウンロード導線、S5外部モニター出力、導入・利用・ビルド手順、ドキュメント、実機LTC E2E、
  ライセンス、Studio Sandixロゴを維持した。
- スクリーンショット（後日ユーザー提供）以外の全ローカルリンクが存在すること、両版の
  screenshot／Releases／ロゴ参照数が一致することを検証した。
- Debug非E2E: 937/937件合格、失敗0、スキップ0、ビルド警告0。プロダクションコード変更なしのため
  S2ではE2E全件ゲートを省略した。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 S4 完了（2026-07-16）

- 実装コミット: `7dbf475`（`release: バージョンを0.2.0へ更新`）。
- `TimecodeSyncPlayer.csproj`とリリースゲートの`ApplicationVersionTests`を0.2.0へ更新し、
  `CHANGELOG.md`の0.2.0節を2026-07-16付で確定した。先行テストでは実体が0.1.0のため意図どおり
  1件失敗し、実装後は対象3/3件が合格した。
- `package-release.ps1`は`-Version`省略時にcsprojの`Version`を取得するよう変更した。
  `installer.iss`の版数フォールバックを廃止し、パッケージスクリプトからの明示指定を必須にした。
- Release非E2E: 937/937件合格、失敗0、スキップ0、ビルド警告0。
- Release E2E: 37/37件合格、失敗0、スキップ0（9分9秒、実機LTC 11件を含む）。
- `package-release.ps1`を版数引数なしで実行し、Releaseビルド警告0・エラー0で次を生成した。
  - `TimecodeSyncPlayer-v0.2.0-win-x64.zip`: 829,003バイト、SHA-256
    `5E1F494BC4B2683FAFF9E3930201B50A5701C1773B8A8D5559830CE67825CD31`
  - `TimecodeSyncPlayer-v0.2.0-setup.exe`: 2,723,175バイト、SHA-256
    `88A19D1364F0716381E1D02A2E5EF6E87B0867B0AC6B77CD4C33DFEB546BD9E0`
- zipの必須配布物、`SpoutDX.dll`、ライセンス・通知・CHANGELOG・`get-mpv.ps1`を確認し、
  mpv DLLとPDBが含まれないことを確認した。setup.exeの製品バージョンは0.2.0。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。
