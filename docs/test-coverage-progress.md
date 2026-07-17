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
  - `TimecodeSyncPlayer-v0.2.0-win-x64.zip`: 828,993バイト、SHA-256
    `BBCC4A87A75153B7586A9CDBE5C28C8532C30E883A9D18DF5D507BF908544E40`
  - `TimecodeSyncPlayer-v0.2.0-setup.exe`: 2,723,060バイト、SHA-256
    `98C43414BF6B9C21147536EE82F91E1B27B4631DF43B673ADFB4EDBFD6B92B17`
- S4記録コミット後に成果物を再生成し、Release EXEの情報バージョンが
  `0.2.0+9012cc1a2648f0b54fd0375fedf4857b375ea012`を指すことを確認した。
- zipの必須配布物、`SpoutDX.dll`、ライセンス・通知・CHANGELOG・`get-mpv.ps1`を確認し、
  mpv DLLとPDBが含まれないことを確認した。setup.exeの製品バージョンは0.2.0。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### v0.2.0 リリースレビュー対応（2026-07-16）

- 実装コミット: `96f9e28`（`fix: OSDとフルスクリーン設定のレビュー指摘を修正`）。
- `ShowDebugOsd=false`時は起動プロパティを`osd-level=1`、`osd-bar=yes`とし、mpv標準の
  ステータス行を抑止しながらシークバーを維持するよう修正した。`true`時は`osd-level=3`と
  既存`osd-msg3`更新を維持した。実際のmpv property名と値を検証するテストを2件追加した。
- フルスクリーン出力ウィンドウのOwner指定を撤廃し、メインウィンドウ最小化から独立させた。
  ディスプレイ選択の自動フォールバックでは保存値を書き換えず、ユーザーの選択操作時だけ
  `UpdateAsync`で`fullscreenDisplayDeviceName`を保存するよう変更した。
- E2Eのフルスクリーン開閉テストをヘルパー群からテスト群へ移動。MouseEnterによるフォーカス取得は
  維持し、`docs/settings.md`へ挙動を追記した。CHANGELOGにはPerMonitorV2 DPI対応と設定キーを追記した。
- Release非E2E: 939/939件合格、失敗0、スキップ0、ビルド警告0。
- Release E2E: 37/37件合格、失敗0、スキップ0（9分8秒、実機LTC 11件を含む）。
- `package-release.ps1`を版数引数なしで再実行し、Releaseビルド警告0・エラー0で再生成した。
  - `TimecodeSyncPlayer-v0.2.0-win-x64.zip`: 829,065バイト、SHA-256
    `F89AA5D63C8A36093B1CD2969ADDEB5AE676AF5A8A9D49AF02D01BB58A2BD9F9`
  - `TimecodeSyncPlayer-v0.2.0-setup.exe`: 2,723,142バイト、SHA-256
    `59649E91CE1D517E891E1C6708A321299658E9BBAA72D09F8A15E91C1FDDE5B9`
- Release EXEの情報バージョンは`0.2.0+96f9e28b0c6ef0c62b71f2cfd041b6bba5216f65`。
  zipの必須配布物とmpv DLL/PDB除外、setup.exeの製品バージョン0.2.0を確認した。
- push・タグ作成・GitHub Release公開は実施していない。未追跡`AGENTS.md`もステージしていない。

### テスト強化 V1 完了（2026-07-16）

- 実装コミット: `2c971ce`（`test: 永続化のアトミック保存と破損耐性を強化`）。
- `ProjectSerializer`と`AppSettingsManager`の保存を、保存先と同じディレクトリの一時ファイルへ
  従来同一バイト列を書き、既存ファイルには`File.Replace`、新規ファイルには`File.Move`を使う
  アトミック方式へ変更した。同一パスは直列化し、並行保存後も片方の完全なJSONだけが残る。
- 切詰め・ゴミ文字列・空・BOMのみ・不正UTF-8バイト、欠落キー・未知キー・型不一致を検証。
  プロジェクトは`JsonException`を呼出元へ伝播し、WindowLoadedからの`--open`相当経路では
  スケジューラが失敗を捕捉する。設定は既定値へ復旧し、直後の`UpdateAsync`で正常再生成する。
- 一時書込み例外と保存先ロックで既存ファイルが無傷、一時ファイルが残らないことを検証。
  記録型ファイル操作シームはJSON生成・保存経路を実物のまま、OS操作境界だけに限定した。
- 新規起動エラーテストと既存ProjectSerializerテストが静的`ProjectPath`をクラス並列で競合する
  テスト干渉を確認。両クラスを同一xUnitコレクションで直列化し、組合せを3回連続27/27合格させた。
- ミューテーション確認1: 一時パスを保存先自身へ変更すると
  `SaveAsync_ExistingDestinationWritesTemporaryFileThenReplacesIt`が
  `Expected temporaryPath not to be ...atomic-existing.tsp`で失敗した。変更は復元済み。
- ミューテーション確認2: 既存判定を反転してReplace/Moveを逆にすると
  `SaveAsync_NewDestinationWritesTemporaryFileThenMovesIt`が`operations.Moves`空で失敗した。
  変更は復元済み。復元後の代表2件は2/2合格。
- V1対象: 68/68件合格。Debug非E2E: 970/970件合格、失敗0、スキップ0、警告0。
- Debug E2E: 37/37件合格、失敗0、スキップ0（4分16秒、実機LTC 11件を含む）。
- ブランチは`test/hardening-v1`。pushは実施せず、未追跡`AGENTS.md`もステージしていない。

### テスト強化 V8 停止（2026-07-16）

- `tests/TimecodeSyncPlayer.Tests/Integration/SyncScenarioHarness.cs` に、実物の `PlaylistState`、
  `TimecodeSyncService`、`SyncDecisionEngine`、`GapFreezeHandler`、`LtcSignalLossPolicy`、
  `ContinueOnTrackPlanner`（`ContinueOnTrackCoordinator` 内から使用）と、3つの coordinator を
  MainWindow と同じ分岐で接続する統合ハーネスの最小再現を追加した。mpv 境界のみ記録型とし、
  LTC供給、100ms tick、手動 Play/Pause、シークバー操作、単調ミリ秒を公開した。
- ハーネス自己検証
  `HarnessSelfTest_LtcOnTrack_UsesRealPlaylistAndCoordinatorToLoadTrack` は成功し、LTC の OnTrack 判定から
  実 coordinator を経て期待する `loadfile` が記録されることを確認した。
- V8 の pause 所有権シナリオで実バグを検出したため、共通ルールに従い V8 を停止した。
  再現手順は `Continue + Sync ON → track 1 → 手動 Pause → Freeze gap → freeze capture 完了 → track 2`。
  gap 終了時に `ContinueOnTrackCoordinator` が `ResumeMpvPause` と `ApplyPauseState(false)` を無条件実行し、
  手動 Pause を解除する。これは「解除するのは所有者だけ」「手動操作は常に勝つ」という V8 の不変条件に反する。
- 失敗証跡: `dotnet test ... --filter "FullyQualifiedName~SyncScenarioHarnessTests"` は 2件中1件成功・1件失敗。
  `ManualPause_RemainsPaused_WhenReturningFromFreezeGap` の最終 assertion が
  `Expected harness.IsPaused to be true because manual playback control must always win, but found False.` で失敗した。
- 本体 `src/` は変更していない。実バグ検出による停止のため、ミューテーション確認、V8 全シナリオ実装、
  非E2E全件ゲートは未実施。V9 以降にも進んでいない。push は実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V8 完了（2026-07-16）

- ユーザー承認に基づき、V8 が検出した pause 所有権バグのみ本体を修正した。ギャップ進入前の
  `PlaybackControlState.IsPaused` を `GapFreezeHandler` に記録し、ギャップ自身が pause を掛けた場合だけ
  脱出時に resume する。進入前から手動 Pause の場合は正位置への Seek、OSD、ラベル更新だけを行い、
  pause を維持する。既存の gap 脱出ログメッセージは変更していない。
- 赤→緑証跡: 修正前の
  `ManualPause_RemainsPaused_WhenReturningFromFreezeGap` は
  `manual playback control must always win, but found False` で失敗し、修正後は合格した。
  `GapFreezeHandler` と `ContinueOnTrackCoordinator` の直接テストでも、gap 所有 pause のみ解除し、
  手動所有 pause は SeekTo 後も維持することを固定した。
- 実物の `PlaylistState`、`TimecodeSyncService`、`SyncDecisionEngine`、`GapFreezeHandler`、
  `LtcSignalLossPolicy`、`ContinueOnTrackPlanner` と3 coordinator を MainWindow 相当で接続し、mpv 境界だけを
  記録する `SyncScenarioHarness` を完成させた。単調ミリ秒、LTC供給、100ms tick、モード/Sync切替、
  手動Play/Pause、シークバー操作を DSL として公開した。
- V8 の5群を実配線で検証した: ショー進行フル、pause所有権12系列、到達可能18状態のモデル遷移と
  到達不能15状態の根拠、トラック末尾±1フレーム20回の連打、プレイリスト行選択による意図しないloadfileと
  Black固着の2回帰。状態/描画整合、Continue+Sync ON以外でのgap禁止、信号断pauseの二重発火禁止を
  `ValidateInvariants` とモデルテストで機械的に確認した。
- ミューテーション確認1: `RecordPauseOwnership` の否定を外して所有権を反転すると、
  `ManualPause_RemainsPaused_WhenReturningFromFreezeGap` が
  `Expected harness.IsPaused to be true ... but found False` で失敗した。変更は復元済み。
- ミューテーション確認2: `GapStateExitPolicy` の Single 判定を `==` から `!=` に反転すると、
  `BlackGap_WhenChangingToSingle_ClearsBlackAndRedrawsVideo` が
  `Expected GapState.Inactive ... but found GapState.BlackFrameActive` で失敗した。変更は復元済み。
- 復元後のV8対象は40/40件合格。Debug非E2E全件は1010/1010件合格、失敗0、スキップ0、
  ビルド警告0。ブランチは`test/hardening-v1`、pushは実施せず、未追跡`AGENTS.md`もステージしていない。

### テスト強化 V9 停止（2026-07-16）

- `tests/TimecodeSyncPlayer.Tests/E2E/SystemScenarioE2ETests.cs` を追加し、UIA パターン操作だけで
  プロジェクト往復、モード切替、再生中フルスクリーン2周、破損プロジェクト読込の4シナリオを実装した。
  キー/マウス合成と固定 `Sleep` は使用しておらず、前提条件不足も Skip にせず失敗として扱う。
- モード切替、フルスクリーン、破損プロジェクトの各シナリオは単独実行で合格した。
  プロジェクト往復は動画2本の追加後、2番目のトラックを選択して「上へ移動」した時点で
  実アプリの `DispatcherUnhandledException` を再現したため、共通ルールに従い V9 を停止した。
- 再現コマンド:
  `$env:WINDIR=$env:SystemRoot; dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --no-restore --filter "FullyQualifiedName~SystemScenarioE2ETests.ProjectRoundTrip"`
  は 1件中1件失敗。アプリには「予期しないエラーが発生しました。アプリケーションを終了します。」が表示された。
- 例外は `MainWindow.xaml.cs:362` の `PlaylistList.SelectedIndex = _vm.Playlist.SelectedIndex` に
  `-2` が渡された `ArgumentException`。`PlaylistViewModel` の MoveUp 中に
  `ObservableCollection` の移動で WPF 選択が一時的に `-1` となり、`SelectionChanged` が
  `_selectedIndex=-1` を設定した後、コマンドが `_selectedIndex--` を行って `-2` にする競合が原因。
- 本体 `src/` は変更していない。実バグ修正の承認待ちのため、V9 のミューテーション確認、
  非E2E/E2E全件ゲート、V10以降は未実行。ブランチは `test/hardening-v1`、push はしていない。

### テスト強化 V9 再停止（2026-07-16）

- 承認された並べ替えクラッシュを修正した。`PlaylistViewModel` の MoveUp/MoveDown は移動前の
  選択インデックスと最終インデックスをローカル変数で確定し、コレクション移動後に
  `SelectedIndex` を絶対代入するよう変更した。`MainWindow` の選択同期は `-1..Items.Count-1`
  だけを `PlaylistList.SelectedIndex` へ適用し、それ以外を無視する防御を追加した。既存ログは変更していない。
- RED: 実 `ObservableCollection.CollectionChanged` から `vm.SelectedIndex=-1` のUIエコー相当を
  差し込む回帰テスト2件は、修正前に MoveUp が期待1に対して `-2`、MoveDown が期待2に対して
  `0` となって失敗した。GREEN: 修正後の `PlaylistViewModelTests` は14/14件合格した。
- V9 `ProjectRoundTrip_RestoresPlaylistOrderOffsetModeAndPlayback` は並べ替えクラッシュを通過し、
  保存・クリア・読込まで進んだが、保存した先頭オフセット `00:00:10:00` が読込後に
  `00:00:00:00` へ変わる別の実バグを検出したため再停止した。
- 原因は、`ApplyLoadedProject` が保存済みオフセットを復元した直後に
  `ReadDurationsInBackground` を呼び、共通 `PlaylistDurationBackfillEffects` が
  `AutoOffsetOnAdd=true` を参照して `UpdateMediaDuration(..., recalculate: true)` を実行すること。
  ログでも読込直後に track[0] が10秒から0秒、track[1] が30秒から20秒へ再計算された。
- 新しい実バグの修正は未承認。V9 のミューテーション確認、非E2E/E2E全件ゲート、V10以降は
  未実行。ブランチは `test/hardening-v1`、push はしておらず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V9 完了（2026-07-16）

- 承認に基づき、プロジェクト読込経路のduration backfillを「duration補完のみ」に変更した。
  `PlaylistDurationBackfillCoordinator` は `recalculateTimeline` をUI適用処理へ明示伝搬し、
  プロジェクト読込は `false`、通常のファイル置換・追加経路は従来どおり `AutoOffsetOnAdd` の値を渡す。
  読込後も保存済み `TimelineOffset` を変更しない。誤った再計算を期待する既存テストは存在しなかった。
- TDD RED: 保存値10秒・30秒を持つ実 `PlaylistState` にduration backfillを行う新ユニットテストは、
  修正前には3引数の適用delegateと `recalculateTimeline` 引数が存在せず、CS1593/CS1739で失敗した。
  GREEN: 修正後の `PlaylistDurationBackfillCoordinatorTests` は5/5件合格し、durationが20秒へ
  補完されてもオフセット10秒・30秒が維持されることを確認した。
- V9 E2E 4シナリオは4/4件合格。プロジェクト往復は2本の追加・並べ替え・10秒オフセット編集・
  Continue/Freeze設定・保存・クリア・読込・全状態一致・再生・シークまで実アプリと実mpvで通過した。
  モード往復、再生中フルスクリーン2周、破損プロジェクト読込後の継続利用も合格した。
- ミューテーション確認1: プロジェクト読込の `recalculateTimeline: false` を `true` に戻すと、
  `ProjectRoundTrip_RestoresPlaylistOrderOffsetModeAndPlayback` が期待 `00:00:10:00` に対して
  `00:00:00:00` となり失敗した。変更は復元済み。
- ミューテーション確認2: `FullscreenCloseLabel` を `EXIT FULLSCREEN` から `FULLSCREEN` に壊すと、
  `FullscreenDuringPlayback_TwoCyclesPreservePlaybackAndSpoutState` が開状態ラベル待機で
  `TimeoutException` となり失敗した。変更は復元済み。
- 復元後のDebug非E2E全件は1013/1013件合格、失敗0、Skip0、ビルド警告0。
  Debug E2E全件は実機LTCを含む41/41件合格、失敗0、Skip0（6分7秒）。
  ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V10 停止（2026-07-16）

- 実機VB-CABLEを使う組み合わせシナリオ3件を `LtcHardwareLoopE2ETests` に追加した。
  Continue + BlackギャップからSingleへ切り替える映像復帰、Stopモードでギャップ中に信号断して
  ギャップ脱出後も信号断評価が生きていること、信号継続中のSingle/Continue反復切替を対象とした。
- 1件目を単独実行すると、実LTCが40秒を越えて `Gap: Black` に入り、Single切替後のログには
  `Gap state cleared for manual control syncEnabled=true mode="Single"` と、再描画用の
  `Seek`（再生位置14.467秒から19.967秒）が記録された。一方、UIの `CurrentTrackLabel` は
  3秒後も `Gap: Black` のままで、1/1件が `TimeoutException` により失敗した（25.6秒）。
- 原因は `MainWindow.ExitGapStateForManualControlIfNeeded` がギャップ状態とフレームバッファを解除して
  現在位置へ再シークする一方、`UpdateCurrentTrackLabel` を呼ばないこと。内部状態と映像は復帰するが、
  画面上のトラック表示だけが解除前のギャップ表示のまま残る実バグである。
- このプロダクション修正は承認範囲外のため `src/` は変更していない。V10の残り2シナリオ、
  ミューテーション確認、非E2E/E2E全件ゲート、V5以降は未実行。ブランチは
  `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V10 完了（2026-07-16）

- ユーザー承認に基づき、`MainWindow.ExitGapStateForManualControlIfNeeded` のギャップ解除処理末尾で
  `UpdateCurrentTrackLabel()` を呼ぶ最小修正を行った。ギャップ状態・映像復帰処理と画面表示が
  同じ状態へ更新され、既存の判定ロジックやログは変更していない。
- 実機VB-CABLEシナリオ3件は、Continue + BlackギャップからSingle切替時の映像・ラベル即時復帰、
  Stopモードでギャップ中に信号断して脱出後も停止評価が生きること、実LTC供給中に
  Single/Continueを各2回切り替えて毎回同期へ復帰することを検証した。3件連続で3/3件合格、
  Skip0、テスト本体28秒で、追加実行時間は5分以内に収まった。
- 共有fixtureで前テストの最終TCから次の信号開始TCへ戻る正常な再開を巻き戻りと誤認しないよう、
  新シナリオの初回取得にも既存の `WaitForProgressionAfterSignalRestart` を使用した。固定Sleepや
  新規Skipは追加していない。
- ミューテーション確認1: `ExitGapStateForManualControlIfNeeded` から
  `UpdateCurrentTrackLabel()` を削除すると、Single切替後も `Gap: Black` が残り、
  `CableLoop_ContinueBlackGap_WhenSwitchingToSingle_RestoresVideoStateImmediately` が
  ラベル待機の `TimeoutException` で失敗した。変更は復元済み。
- ミューテーション確認2: `LtcSignalLossPolicy.EvaluatePause` の `context.IsGapActive` ガードを
  `!context.IsGapActive` に反転すると、ギャップ脱出後の信号断で再生が停止せず、
  `CableLoop_StopMode_SignalLossDuringGap_IsEvaluatedAfterGapRecovery` が安定再生位置待機の
  `TimeoutException` で失敗した。変更は復元済み。
- 復元後のDebug非E2E全件は1013/1013件合格、失敗0、Skip0、ビルド警告0。
  Debug E2E全件は実機LTCを含む44/44件合格、失敗0、Skip0（6分48秒）。
  ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V5 停止（2026-07-16）

- 必須ケース `65,536 × 65,536 × 4` を `EnsurePixelBuffer` へ渡す回帰テストを追加した。
  安全な挙動として、折り返した小容量バッファを確保せず入力を拒否することを期待している。
- 単独実行は1/1件失敗。`width * height * 4` がuncheckedな `int` 演算で0へ折り返し、
  例外を投げずに長さ0の `PixelBuffer` を確保した。失敗出力は
  `Expected manager.PixelBuffer to be <null> ... but found {empty}.` だった。
- 根本原因は `PixelBufferManager` が必要バイト数を5箇所で `int` のまま乗算し、正の寸法と
  積の上限を検証していないこと。同じ計算はPixel/Frozen/GapFreezeの3つのEnsure経路と
  2つのコピー経路にあり、`FrameRenderer` のコピー長計算にも存在する。巨大寸法がネイティブ境界へ
  不正な小容量バッファとして進む可能性があり、V5が明示した実バグに該当する。
- 共通ルールに従い `src/` は変更していない。V5の残りケース、ミューテーション確認、非E2E全件ゲート、
  V2以降は未実行。ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` も
  ステージしていない。

### テスト強化 V5 完了（2026-07-16）

- ユーザー承認の二段防衛を実装した。第一防衛では `RenderFrameSizePolicy` が幅・高さ1未満、または
  long/checkedで算出した4bytes/pixelの必要量が `int.MaxValue` を超える寸法を描画不可と判定し、
  元寸法を `Log.Warning` へ記録する。`RenderFrameCoordinator` はEnsure/native呼出し前にreturnし、
  ライブ処理を例外で停止させない。
- 最終防衛として `FrameBufferSize` に共通の必要バイト数検証を追加し、Pixel/Frozen/GapFreezeの
  3つのEnsure、2つのコピー、`FrameRenderer` の3コピー入口、native parameter構築入口で使用した。
  第一防衛を迂回して不正寸法が到達した場合は一貫して `ArgumentOutOfRangeException` を投げる。
  `RenderBlack` / Frozen不可時の非正寸法は従来どおり16x16黒へ安全にフォールバックする。
- TDD RED: 第一防衛テストは `ShouldRender` 不在のCS1061/CS1739で失敗した。最終防衛追加前は
  PixelBufferManager 9件が無例外・0バイト確保・OverflowException等で失敗し、FrameRenderer 10件が
  WPF内部のArgumentException/OverflowExceptionとなり、native parameter 4件は無例外で失敗した。
  GREEN: 復元後のV5対象5クラスは88/88件合格した。
- 境界は0、負、1x1、32,768x1、32,768平方、65,536平方を検証した。1x1と32,768x1は正しい
  バイト数で受理し、0・負・両平方値は第一防衛で描画せず、最終防衛直接呼出しでは明確に拒否した。
  拡大・縮小・再拡大時の参照/ピン留めと0寸法黒フォールバックの既存テストも維持した。
- ミューテーション確認1: 必要量計算からlong cast/checkedを外してint計算へ戻すと、32,768平方と
  65,536平方が `ShouldRender=true` となり、元の再現テストも0バイト確保で、対象5件中3件が失敗した。
  変更は復元済み。
- ミューテーション確認2: `RenderFrameCoordinator` の `ShouldRender` 早期returnを削除すると、
  `Render_InvalidSizeStopsBeforeBufferAndNativeOperations` が期待した空の呼出し列に対して
  `ensure, build, render` を検出して失敗した。変更は復元済み。
- 復元後のDebug非E2E全件は1047/1047件合格、失敗0、Skip0、ビルド警告0。
  Debug E2E全件は実機LTCを含む44/44件合格、失敗0、Skip0（6分40秒）。
  ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V2 完了（2026-07-16）

- `SyncDecisionEngineTests`、`TimecodeSyncServiceTests`、`LtcFrameProcessorTests` に時間軸境界を追加した。
  本体コードは変更していない。
- 23:59:59:24から00:00:00:00への日跨ぎは単一Jumpとして同期を抑止し、次の00:00:00:01で
  Normalへ復帰して同期可能になる現行仕様を固定した。25fpsで1フレームずつ連続逆走する3フレームは
  毎回Reverseとして診断状態が追従し、全フレームで同期を抑止することを確認した。
- duration 0、1フレーム0.04秒、LTCとduration完全一致、duration+1msを検証した。
  許容差は二進表現で厳密な0.25秒の正負境界をNone、境界外1msをSeekとして固定し、逆走時も
  許容差内はNone、外は負方向Seekとなることを確認した。IsSeeking中の大幅TC差は完全にNoneとなる。
- デバウンスは `_lastSyncSeekAt` を境界時刻へ設定し、250msちょうど以降で解除されることを確認した。
  実装が `DateTime.UtcNow` を直接取得するため、決定論的には「境界未満」と「境界到達以降」のうち
  後者を固定し、既存の直後デバウンステストと組み合わせて両側を覆った。
- ミューテーション確認1: tolerance比較を `<=` から `<` に変えると、厳密な正負境界2件が
  NoneではなくSeekとなり失敗した。変更は復元済み。
- ミューテーション確認2: 逆走診断閾値を `-0.5` から `-1.5` フレームへ変えると、連続逆走3件が
  ReverseではなくDuplicateとなり失敗した。変更は復元済み。
- 復元後のV2対象3クラスは56/56件合格。Debug非E2E全件は1060/1060件合格、失敗0、Skip0、
  ビルド警告0。ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` も
  ステージしていない。

### テスト強化 V6 停止（2026-07-16）

- 実物の `LtcSignalLossPolicy` と `GapFreezeHandler` を合成し、信号断ポリシーがpauseを所有した状態で
  「信号復旧→ギャップ進入」と「ギャップ進入→信号復旧」の両順序を実行し、ギャップ脱出後の
  最終pause状態が一致する回帰テストを追加した。
- 単独実行は1/1件失敗。復旧→ギャップの順は最終的に再生へ戻ったが、ギャップ→復旧の順は
  ギャップ脱出後もpauseが残り、`Expected gapThenRecoveryPaused to be false ... but found True.` となった。
- 根本原因は、信号断pause中にギャップへ入ると `GapFreezeHandler.RecordPauseOwnership(true)` により
  ギャップはpauseを所有せず、その後 `LtcSignalLossPolicy.ObserveValidFrame` がギャップ中の復旧を
  `ResumeAndSync` なしとして処理しながら `_pausedByPolicy` をfalseへ消すこと。両ポリシーが所有権を
  手放すため、ギャップ脱出時に再生を再開する所有者がいなくなる。
- 実配線でも有効な再現である。`MainWindow` は有効LTCフレーム受信時に、現在のGap状態を含むcontextで
  `ObserveValidFrame` と信号断アクション適用を先に行い、その後 `ApplyTimecodeSync` でGap脱出を処理する。
  したがってGap中に復旧フレームがOnTrackへ戻す順序がこの失敗経路に一致する。
- 挙動判定ロジックの修正は承認範囲外のため `src/` は変更していない。V6の残り境界、
  ミューテーション確認、非E2E全件ゲート、V3以降は未実行。ブランチは `test/hardening-v1`、
  pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V6 完了（2026-07-17）

- ユーザー承認の不変条件「pause所有権は、解除アクション `ResumeAndSync` が実際に適用された時に
  初めて消費される」に合わせ、`LtcSignalLossPolicy.ObserveValidFrame` を最小修正した。
  信号断ポリシー所有のpauseがあり、Sync OFF・監視停止・Gapアクティブのいずれかで再開を
  適用できない間は、`_pausedByPolicy`・`_isLost`・復旧カウントを保持する。
- 復旧カウントはGap中も保持する方式を選択した。Gap脱出時点で信号が既に連続正常フレーム条件を
  満たしている場合、次の正常フレームで即 `ResumeAndSync` とすることで、ライブ中に不要な再待機を
  入れない。先に手動Playが入った場合は既存の再生状態遷移観測が所有権を破棄し、次フレームも
  `None` のままとなることを合成テストで確認した。
- V6境界として、Gap中の信号断は介入せず脱出直後のtickでPause、信号断pause中の
  Continue→SingleによるGap解除は信号断所有権を解除しない、復旧とGap進入の両順序、手動Play優先、
  `GapFreezeHandler` の2.999秒/3.001秒タイムアウト境界を追加した。フルスクリーン二重実行は
  V9で追加済みの実アプリ2連続開閉シナリオがコンボ選択後の再Invokeを含み、ロジック単体より強い
  経路で二重ウィンドウを作らず開閉できることを継続確認するため、重複テストは追加していない。
- ミューテーション確認1: 復旧時の `if (_pausedByPolicy && !canApplyPolicyOwnedResume)` ガードを
  削除すると、Gap中に `ResumeAndSync` が返り、
  `SignalRecoveryAndGapEntry_InEitherOrder_ResumeAfterGapExit` が
  `Expected blockedRecovery to be None ... but found ResumeAndSync.` で失敗した。変更は復元済み。
- ミューテーション確認2: `EvaluatePause` の `context.IsGapActive` ガードを削除すると、
  `Evaluate_LossStartsDuringGap_PausesOnFirstTickAfterGapExit` がGap中の期待 `None` に対して
  `Pause` を検出して失敗した。変更は復元済み。
- 復元後のV6対象5クラスは79/79件合格。V8のpause所有権12系列とモデル遷移を含む対象は
  31/31件合格。VB-CABLE実機の
  `CableLoop_StopMode_SignalLossDuringGap_IsEvaluatedAfterGapRecovery` は1/1件合格、Skip0。
  Debug非E2E全件は1066/1066件合格、失敗0、Skip0、ビルド警告0。ブランチは
  `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V3 完了（2026-07-17）

- `PlaylistState` の空状態に対するSelect/Move/Remove/Recalculate/Update/Clear/検索を一括検証し、
  すべて安全にfalse・null・-1または空状態を維持することを固定した。オフセット編集の空状態も
  `TrackNotFound` となりコレクションを変更しない。
- 1000トラックを1秒間隔に配置し、先頭・500番目・末尾・末尾直後のクエリ結果を検証した。
  実行時間ではなく返却track、media位置、gapのprevious trackだけを評価し、アロケーション爆発や
  例外なく決定的な結果を返すことを確認した。
- 負オフセットはモデル上の有効値として負の区間をクエリ可能、`TimeSpan.MaxValue` 近傍も
  オーバーフローせずクエリ可能であることを固定した。文字列編集経路は負値・100時間・NaN・
  Infinityを`ParseFailed`で拒否する。クエリ位置のNaN/±Infinity/`double.MaxValue` は例外を
  漏らさず決定的にGapを返す。
- duration未取得（`MediaOut=null`かつ`MediaDuration=0`）と既知durationの混在では、長さ0を
  OnTrackとせず厳密な半開区間でGap/既知track/末尾Gapを返すことを確認した。backfillはnullを
  スキップし、既知durationだけを補完して保存済みオフセットを維持する。
- 既存の重複テストはduration更新前に保持した古いrecordを再代入して2本目をduration 0へ戻しており、
  実際には区間が重複していなかった。更新後の `state.Tracks[1]` を基にオフセットだけ変更し、
  0～60秒と30～90秒が真に重なる状態で低playlist index優先を固定した。本体コードは変更していない。
- ミューテーション確認1: タイムライン走査を末尾からの逆順へ変更すると、重複位置40秒で2本目を
  返し、低indexの1本目を期待するテストが参照不一致で失敗した。変更は復元済み。
- ミューテーション確認2: OnTrack終端判定を `< tlOut` から `<= tlOut` に変更すると、duration 0の
  未取得trackが開始点0秒でOnTrackとなり、期待Gapに対する不一致で失敗した。変更は復元済み。
- 復元後のV3対象5クラスは61/61件合格。Debug非E2E全件は1081/1081件合格、失敗0、Skip0、
  ビルド警告0。ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` も
  ステージしていない。

### テスト強化 V4 停止（2026-07-17）

- 0チャンネル、3/8チャンネル、8kHz/192kHz、64bit float、frame境界を1byte超える
  `bytesRecorded` を追加し、現行変換挙動を確認した。3/8チャンネルは先頭チャンネルを正しく
  モノラル化し、サンプルレートは値変換へ影響せず、0チャンネルは空、未対応64bit floatは
  既存8bitと同様に0へフォールスルー、余剰1byteは切り捨てとなる。これら24件は合格した。
- 必須の非有限値ケースとして、NaN・+Infinity・-Infinityを含むfloat PCMを実
  `LtcAudioSampleProcessor.Process` へ入力し、Peak/RMSがNaNを返さないことを期待する回帰テストを
  追加した。対象25件の実行は24件合格、1件失敗で、RMSがNaNとなった。
- 失敗出力は `Expected float.IsNaN(result.Rms) to be false, but found True.`。根本原因は
  `LtcAudioSampleProcessor.MeasureLevel` が非有限sampleを無条件に `sample * sample` して
  `sumSquares` へ加算するため、1つのNaNでRMS計算全体へNaNが伝播すること。
- これはV4保証内容「統計値が破綻せずNaN伝播しない」に反する実バグであり、本体変更の承認範囲外
  なので `src/` は変更していない。長時間相当カウンタの適用可否確認、ミューテーション確認、
  非E2E全件ゲート、V4完了、V7は未実行。ブランチは `test/hardening-v1`、pushは実施せず、
  未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V4 完了（2026-07-17）

- ユーザー承認どおり `LtcAudioSampleProcessor.MeasureLevel` の統計計算だけを修正した。
  `float.IsFinite` がfalseのsampleはpeak/RMSの最大値・二乗和・分母から除外し、有限sample数が0なら
  `(peak=0, rms=0)` を返す。`PcmSampleConverter` の出力と `_decoder.Write(samples, samples.Length)`
  は変更せず、LTC decoderへの供給パスを維持した。
- 全sampleがNaN/+Infinity/-InfinityのケースはSampleCount 3のままpeak/RMSとも0、一部だけ有限の
  ケースはSampleCount 5のまま有限値0.5/-1.0だけでpeak 1.0、RMS `sqrt(1.25/2)` を返すことを
  実 `LtcDecoder` と合成して検証した。decoder側の例外・停止・追加のNaN起因問題は観測されなかった。
- 長時間相当カウンタは適用対象を確認したが、`PcmSampleConverter` と
  `LtcAudioSampleProcessor` に累積frame/sampleカウンタは存在せず、`SampleCount` は各呼出しの
  配列長である。`LtcDecoder._sinceTrans` はprivateでテスト用注入口がなくV4対象外のため、
  int上限相当の巨大配列確保による非現実的な試験は対象外とした。
- ミューテーション確認1: `!float.IsFinite(sample)` を `float.IsFinite(sample)` に反転すると、
  全非有限ケースのpeakが期待0に対してPositiveInfinityとなり失敗した。変更は復元済み。
- ミューテーション確認2: RMSの分母を有限sample数から全sample数へ戻すと、混在ケースの期待
  0.7905694に対して0.5となり失敗した。変更は復元済み。
- 復元後のV4対象3クラスは28/28件合格。Debug非E2E全件は1090/1090件合格、失敗0、Skip0、
  ビルド警告0。ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` も
  ステージしていない。

### テスト強化 V7 停止（2026-07-17）

- `SpoutOutput` の必須ライフサイクルとして、初期化・有効化後にnative `SendImage` がfalseを返し、
  出力が利用不可かつ安全のため無効へ遷移し、native復旧後の `TryInitialize` で利用可能へ戻る一連の
  回帰テストを追加した。再初期化はユーザーの有効状態を勝手に戻さず、再度有効化した後の送信成功
  までを期待している。
- 単独実行は0/1件合格、1件失敗。失敗出力は
  `Expected output.IsAvailable to be false, but found True.` だった。
- 根本原因は `SpoutOutput.SendFrame` が `SendImage=false` をログへ記録するだけで、`_initialized`・
  `IsEnabled`・native objectを無効化しないこと。例外catchも同様にログだけである。そのため
  `IsAvailable` はtrueのまま残り、次の `TryInitialize()` は `_initialized` の早期returnでtrueを返して
  Create/Open/SetNameを再実行せず、device lostから実際には復旧できない。
- これはV7保証内容「送信中失敗→無効化→再初期化」に反する実バグであり、本体変更の承認範囲外
  なので `src/` は変更していない。native例外側の同経路、mpv初期化各段階の後始末、
  MediaDurationReader異常出力、ミューテーション、非E2E全件ゲートは未実行。V7と全goalは未完了。
  ブランチは `test/hardening-v1`、pushは実施せず、未追跡 `AGENTS.md` もステージしていない。

### テスト強化 V7 Spout復旧修正・mpv後始末で再停止（2026-07-17）

- ユーザー承認どおり、`SpoutOutput.SendFrame` のnative `false` 応答または例外時に一度だけ実行する
  無効化遷移を追加した。最初に `_initialized=false` と `IsEnabled=false` を確定して後続送信を遮断し、
  Warningを1回出して、既存Disposeと同じRelease→Destroy順でnative objectを解放する。
  通常の初期化・Disposeのログと呼出し順序は変更していない。
- false応答と例外の両方で `IsAvailable=false`、`IsEnabled=false`、Release→Destroyとなり、その後の
  `TryInitialize` がCreate/Open/SetNameを再実行して復旧できることを確認した。失敗後の2回目送信は
  nativeへ到達せず、続くDisposeもRelease/Destroyを重複実行しない。SpoutOutputTestsは21/21件合格。
- 続くmpv必須ケースとして、Create成功後にInitializeが負値を返した場合、同じhandleを
  `TerminateDestroy` して結果にはzero handleと `InitializeFailed` を返す回帰テストを追加した。
  単独実行は0/1件合格、1件失敗で、出力は
  `Expected result.Mpv to be equal to 0, but found 321.` だった。
- 根本原因は `MpvSessionInitializer.Initialize` がInitialize失敗時に
  `MpvSessionInitializationResult(false, mpv, InitializeFailed)` を返すだけで
  `_mpvApi.TerminateDestroy(mpv)` を呼ばないこと。handleはMainWindowへ代入され終了時まで残るため、
  V7要件の初期化失敗直後の後始末を満たさない。
- このmpv本体修正は今回のSpout承認範囲外なので `MpvSessionInitializer` は変更していない。
  RenderContext失敗後始末、MediaDurationReader異常出力、V7ミューテーション、非E2E全件ゲートは
  未実行。V7と全goalは未完了。ブランチは `test/hardening-v1`、pushは実施せず、未追跡
  `AGENTS.md` もステージしていない。

### テスト強化 V7 完了（2026-07-17）

- ユーザー承認どおり、`MpvSessionInitializer` で `mpv_initialize` が負値を返した場合に
  `TerminateDestroy(mpv)` を直ちに呼び、失敗結果のhandleを `IntPtr.Zero` とした。Create失敗は
  handleが生成されていないため破棄を呼ばず、成功時も破棄を呼ばない。既存ログは変更していない。
- mpvのCreate失敗・Initialize負値・成功の3経路について、結果種別、zero/live handle、
  Create→Initialize→TerminateDestroyの順序を検証した。WindowLoaded側もInitialize失敗結果の
  zero handleを受け取り、専用エラーを表示して後続初期化を行わない契約へ合わせた。
- RenderContextはrc=0の成功と、rc=-1/`int.MinValue` の失敗について、成功判定・診断用handle・
  return code保持を固定した。WindowLoadedは作成失敗で専用エラー停止し、既存
  `MainWindowResourceDisposer` がRenderContext→mpvの順に解放する経路を維持している。
- `MediaDurationReader` は計画の適用条件を確認したが、ffprobe出力parserが分離されておらず、
  外部processをmockしない条件では空/非数値/負値/多行を任意注入できない。計画記載どおりこの4種は
  対象外とし、既存の実ffprobe正常動画・不存在ファイル・空ファイルの3経路を継続検証した。
- ミューテーション確認1: Spout送信失敗遷移から `_initialized=false` を削除すると、
  `SendFrame_NativeFalseInvalidatesOutputAndTryInitializeCanRecover` が期待falseに対する
  `IsAvailable=true` で失敗した。変更は復元済み。
- ミューテーション確認2: mpv Initialize失敗分岐から `TerminateDestroy` を削除すると、再現テストが
  最終呼出し `terminate-destroy:321` の期待に対して `initialize` を検出して失敗した。変更は復元済み。
- 復元後のV7対象6クラスは36/36件合格。Debug非E2E全件は1092/1092件合格、失敗0、Skip0、
  ビルド警告0。Debug E2E全件はVB-CABLE実機を含む44/44件合格、失敗0、Skip0（7分15秒）。

### テスト強化goal 最終サマリ（2026-07-17）

- 指定順 `V1 → V8 → V9 → V10 → V5 → V2 → V6 → V3 → V4 → V7` をすべて完了した。
- V1はProject/AppSettingsのアトミック保存と破損復旧、V8はヘッドレス同期シナリオとpause所有権、
  V9は実アプリのプロジェクト往復・並べ替え・フルスクリーン、V10はVB-CABLE実信号のGap/信号断/
  モード切替を強化した。
- V5はレンダー寸法オーバーフローの二段防衛、V2は日跨ぎ・逆走・境界・デバウンス、V6はGapと
  信号断のpause所有権競合、V3は空/1000件/重複/極端offset、V4は不正WaveFormatと非有限PCM統計、
  V7はSpout/mpv外部失敗後の無効化・後始末・復旧を固定した。
- 各タスクで最低2件のミューテーションを実施し、意図した代表テストが赤になることを確認後、
  すべて復元した。途中で検出した実バグは停止条件に従って報告し、ユーザー承認後のみ最小修正した。
- 最終ゲートはDebug非E2E 1092/1092件、Debug E2E 44/44件（実機込み）、失敗0、Skip0、警告0。
  ブランチは `test/hardening-v1`、pushは実施していない。未追跡 `AGENTS.md` は一度もステージ・変更
  していない。

### 最終レビュー修正・goal最終完了（2026-07-17）

- `GapFreezeHandler` のpause所有権をギャップエピソード開始時だけ記録するラッチにした。
  Black/Freeze切替による同一ギャップ内の再進入では所有権を保持し、Reset/ResetAllまたは実際の
  ギャップ脱出時にだけ解除する。ハーネスへ、切替後のトラック復帰で再生再開するケースと、進入前の
  手動pauseが切替後も維持されるケースを追加した。
- `SyncScenarioHarness` はギャップディスパッチ後に `UpdateCurrentTrackLabel` 相当の `update-label` を
  記録し、実アプリの処理順と一致させた。
- 不正なレンダー寸法のログをフレーム毎にWarning連発しないようDebugへ下げた。第一防衛の
  オーバーフロー拒否とEnsure系のchecked/例外契約は維持しつつ、PixelBufferManagerとFrameRendererの
  コピー入口は不正寸法を安全にearly-returnする従来契約へ戻した。
- `AtomicFileWriter.PathLocks` はプロセス中に保持されるが、デスクトップアプリが扱う設定パスと
  ユーザー選択プロジェクトパスの集合は実用上有界である旨をコードコメントに記録した。
- `PlaylistViewModel.AddFilesAsync` はbackfill開始時の `AutoOffsetOnAdd` をキャプチャし、非同期読込中に
  UI設定が変わっても一連の追加処理へ同じ値を適用する仕様とした。整合性向上として正式採用する。
- ミューテーション確認1: pause所有権ラッチの再入ガードを除去すると、GapBehavior切替後の復帰テストが
  pause継続を検出して失敗した。ミューテーション確認2: コピー入口を例外経路へ戻すと、0/負値/
  32768平方/65536平方の4境界が失敗した。いずれも復元済み。
- 復元後のDebug非E2E全件は1094/1094件合格、失敗0、Skip0、警告0。Debug E2E全件は
  VB-CABLE実機を含む45/45件合格、失敗0、Skip0、警告0（7分16秒）。これをもって
  `V1 → V8 → V9 → V10 → V5 → V2 → V6 → V3 → V4 → V7` のテスト強化goalを最終完了とする。
  ブランチは `test/hardening-v1`、pushは実施していない。未追跡 `AGENTS.md` はステージ・変更していない。

### 音量コントロール実装 停止（2026-07-17）

- `feature/volume-control` を `main` から作成。開始時のDebug非E2Eは1095/1095件合格、Skip0、警告0。
- AppSettingsの `IsMuted` / `Volume`（既定false/100、0〜100クランプ）、音声状態・ラベル・
  mpv `mute` / `volume` 書き込みを担う `AudioControlCoordinator`、PlayerViewModel表示状態、
  MainWindowのMUTEボタン/音量スライダーをTDDで実装した。音声設定は `mpv_initialize` 成功後に適用し、
  LoadFileへ音声オプションは追加していない。
- ユニット対象63/63件、SyncScenarioHarnessの音声状態遷移横断8/8件が合格。通常E2Eの
  `MuteAndVolume_PersistAndRestoreAcrossApplicationRestart` は1/1件合格し、ラベル・スライダー・
  settings.json・再起動復元を確認した。
- 実機E2E `CableLoop_ContinueMuteSurvivesGapAndTrackSwitch` は、ミュートON、実LTC追従、Black gap進入、
  ラベルとsettings.jsonのミュート保持までは成功した。一方、Gap中の前/次トラック操作後も
  `CurrentTrackLabel` が `Gap: Black` のままで、トラック切替完了の安定した観測に3回連続で失敗した。
  Continue同期tickが即座にGap表示を再適用することと、最終トラック後のno-tracks gapで選択状態を
  UIラベルから判別できないことが原因。製品の音量状態破壊は観測されていない。
- 「同一失敗3回」および「E2Eが1件でも失敗」の停止条件に従い停止した。全件ゲート、実機E2E全件、
  ミューテーション確認、docs/settings.md、CHANGELOG、最終コミットは未実施。作業差分は未コミットで保持し、
  pushしていない。未追跡 `AGENTS.md` はステージ・変更していない。

### 音量コントロール実機E2E方式変更後 停止（2026-07-17）

- 承認された決定論的方式に従い、ProjectSerializerでトラック1（0〜5秒）、Gap（5〜8秒）、
  トラック2（8〜13秒）のプロジェクトを生成し、`--load-project` で起動して実LTCを3秒12フレームから
  連続送信する `CableLoop_ContinueMuteSurvivesDeterministicGapAndTrackSwitch` を追加した。
- 単独実行は1/1件失敗。最初のトラックラベル待機が3秒でタイムアウトした。アプリログでは
  両トラックのメディアパスがプロジェクトディレクトリ外として拒否され、さらにファイルパスが空と判定されて
  `Continue mode query result: status="NoTracks"` となったことを確認した。LTC自体はFixed25で正常に復号され、
  CABLE Outputも正常に開始している。原因は、生成したプロジェクトを専用一時ディレクトリへ置いた一方で、
  メディアを共通E2E fixtureの別ディレクトリから絶対パス参照したため、ProjectSerializerの安全なパス制約に
  違反したことにある。
- E2Eが1件でも失敗した場合の停止条件に従い、再実行・期待値緩和・修正は行わず停止した。次回は
  プロジェクトと同じ一時ディレクトリ配下へテスト動画を配置し、相対パスとして保存する必要がある。
  全件ゲート、ミューテーション確認、docs/settings.md、CHANGELOG、最終コミットは未実施。
  作業差分は未コミット、pushは未実施。未追跡 `AGENTS.md` はステージ・変更していない。

### 音量コントロール実装 完了（2026-07-17）

- ユーザー判断により、上記実機E2E失敗は製品バグではなくテストセットアップ不備として再開した。
  テスト動画をプロジェクトと同じ一時ディレクトリへコピーし、ProjectSerializerが相対パスとして保存する
  構成へ修正した。Continueモードのトラック遷移は、Singleモード用の `1/2` ラベルではなく、今回の
  実行開始位置以降のアプリログに記録される固有トラック名で決定論的に観測するようにした。
- 実機E2Eはトラック1（0〜5秒）→Black Gap（5〜8秒）→トラック2（8〜13秒）を実LTCで横断し、
  各段階でMUTEボタンが `MUTE ON` のまま、settings.jsonの `isMuted=true` が維持されることを確認した。
- `IsMuted` / `Volume`（既定false/100、0〜100クランプ、非有限値は100へ補正）をAppSettingsへ追加し、
  mpv初期化成功後に `mute` / `volume` を適用するAudioControlCoordinator、MUTEトグル、0〜100音量
  スライダーを実装した。ミュート中の音量変更は保持され、解除しても選択値を維持する。
- SyncScenarioHarnessはpause/osdを含む全mpvプロパティ書き込みを単一レコーダーへ通し、Single / Continue、
  自動・手動トラック切替、Freeze / Black Gap進入脱出、GapBehavior切替、Stopモード信号断・復旧、
  プロジェクト読込、StopPlayback、LoadFile、シーク中にmute/volumeの状態と書き込みが不変であることを確認した。
- `docs/settings.md` に `isMuted` / `volume` を追記し、CHANGELOG 0.2.0 Addedへ永続ミュート・音量操作を追記した。
- ミューテーション確認: ユニット層で音量上限を100から99へ変えると境界2件が失敗、結合層で
  StopPlaybackへ誤った `mute=no` を加えるとSingle / Continueの2件が失敗、E2E層でBtnMuteの
  AutomationIdを壊すと実アプリ操作テストが失敗した。すべての変異は復元済み。
- 復元後のDebug非E2E全件は1120/1120件合格、失敗0、Skip0、警告0。Debug E2E全件は
  VB-CABLE実機を含む46/46件合格、失敗0、Skip0、警告0（7分59秒）。ブランチは
  `feature/volume-control`、pushは実施していない。未追跡 `AGENTS.md` はステージ・変更していない。

### 第三者QA指摘対応・ベースラインE2E停止（2026-07-17）

- 指定ブランチ `fix/qa-v0.2` を `main`（`8839f7b`）から作成。push は未実施。
- `docs/qa-fixes-v0.2-plan.md`、`docs/test-coverage-roadmap.md`、QAレポート
  `C:\Users\codea\Documents\qa-report-v0.2.0.md` を全文確認した。
- 初回の非E2E実行では、実行セッションの `WINDIR` が空のため WPF FontCache のURI構築に失敗し、
  `ListBoxItemHitTesterTests` 3件が失敗した。`WINDIR=$env:SystemRoot` をテストプロセス内で補うと
  対象3/3件が合格し、非E2E全件も1120/1120件合格、失敗0、Skip 0、ビルド警告0となった。
- Q1着手前のE2Eベースラインは46件中45件合格、失敗1、Skip 0（6分24秒）。
  `TimecodeSyncPlayerE2ETests.AppLaunches_WindowIsVisible` が
  `FlaUI.Core.Exceptions.PropertyNotSupportedException: IsOffscreen [#30022] is not supported`
  （`TimecodeSyncPlayerE2ETests.cs:124`）で失敗した。
- 一括実行ルールの「E2E が1件でも落ちたら停止」に従い停止。Q1/Q2/Q3はいずれも未着手で、
  `src/`・`tests/`・QA対象docsの変更、ミューテーション確認、QA項目対照表は未実施。
- 未追跡 `AGENTS.md` はステージ・変更していない。

### 第三者QA指摘対応・Q1実機E2E停止（2026-07-17）

- ユーザー許可に基づき既存E2E障害を調査した。失敗時もタイトル取得と他45件は成功し、単独再実行も
  1/1件成功したため、RDP/UIAの一時的な `IsOffscreen` 非対応と判断した。対応時は従来どおり
  `IsOffscreen=false` を要求し、非対応時だけ正のBoundingRectangleへフォールバックするテスト専用
  ヘルパーをTDDで追加した（コミット `58f1f0f`）。復旧後は非E2E 1127/1127件、E2E 46/46件、
  失敗0、Skip 0、警告0。
- Q1では、保存済みプロジェクト復元専用のpausedロード、UI/CLI共通復元処理、`.tsp` を渡した
  `--open` のプロジェクト分類をTDDで実装した。通常動画の追加・`--open` は従来の `pause=no` を維持した。
- Q1対象は、再生操作・CLI計画・SyncScenarioハーネスの19/19件、UIラウンドトリップ1/1件、
  CLI `--load-project` / `.tsp` の `--open` 2/2件が合格。非E2E全件は1130/1130件合格、
  失敗0、Skip 0、警告0。
- 実機込みE2E全件は47件中46件合格、失敗1、Skip 0（6分35秒）。
  `VolumeControlE2ETests.CableLoop_ContinueMuteSurvivesDeterministicGapAndTrackSwitch` が、
  Sync ON後の `switching to track volume-track-1` ログ待機（`VolumeControlE2ETests.cs:108`）で失敗した。
- 根本原因は、Q1の復元で先頭トラックをpaused状態かつ `_loadedTrackId` 設定済みにした結果、Sync ON後の
  Continue同期が `ContinueCurrentTrack` 分岐へ入り、この分岐には `pause=no` / UI pause状態解除がないこと。
  したがって「Sync ON時は同期エンジンにより再生開始」というQ1仕様を満たさない製品実バグである。
- 一括実行ルールの「製品の実バグ発見」「E2Eが1件でも失敗」に従い停止。Q1差分は未コミットで保持し、
  Q2/Q3、ミューテーション確認、QA項目対照表は未着手。pushは未実施。未追跡 `AGENTS.md` は
  ステージ・変更していない。

### 第三者QA指摘対応 Q1/Q2/Q3 完了（2026-07-17）

- ブランチ `fix/qa-v0.2` でQ1→Q2→Q3を直列実行し、pushは実施していない。先行する
  `AppLaunches_WindowIsVisible` はRDP/UIAの一時的な `IsOffscreen` 非対応と確認し、表示意図を
  維持したBoundingRectangleフォールバックをテスト側だけに追加済み（`58f1f0f`）。
- Q1（QA-001、`fb38e46`）: UI/CLIのプロジェクト復元を先頭トラックのfirst frame表示＋pauseへ統一し、
  `.tsp` の `--open` もプロジェクトとして扱う。復元pauseを専用所有権フラグで手動pauseと分離し、
  Single/ContinueのOnTrack同期時だけ一度解除する。手動Play/Pauseは即時に所有権を破棄し、既存の
  pause所有権12系列とモデル遷移テストは無変更で合格した。
- Q2（QA-007/003/004/005、`9631003`）: `syncMode` / `gapBehavior` の変更保存と起動UI復元、
  `ltcDeviceName` の名前保存・復元（旧 `ltcDeviceIndex` は無視）、欠落デバイスの先頭フォールバック＋
  ログ、成功したプロジェクト読込/保存だけの `lastOpenedProjectPath` 更新を実装した。同パスによる
  自動オープンは行わない。`docs/settings.md` に既定値とenum数値対応を記録した。
- Q3（QA-002/006、`4476185`）: CHANGELOG 0.2.0へ、再生中のUI Automation全ツリー取得が
  遅延・失敗し得る制約（個別要素アクセスは可能な場合がある）と、LTC停止後に最終表示値を保持する
  仕様をKnown limitationsとして追記した。README/README.enにはトラブルシュート節がないため、
  条件付きのREADME追記は対象外とした。
- 最終回帰: Debug非E2E 1137/1137件合格、失敗0、skip 0。Debug E2EはVB-CABLE実機系列を含む
  49/49件合格、失敗0、skip 0（6分39秒）。Q1確定時点も1136/1136＋47/47で全緑を確認した。
- ミューテーション確認1: 復元pause消費後もフラグを残すと、Single/Continue双方の
  `RestoredProject_SyncOnOnTrack_ReleasesRestorePauseOnce` がresume 2回を検出して失敗した。
  ミューテーション確認2: Gap変更保存をFreeze固定にすると、設定変更→正常終了→再起動E2Eが
  タイムアウトして失敗した。双方を復元後、対象3件が再度合格した。
- 関連コミット: `58f1f0f`（UIAフレーク頑健化）、`fb38e46`（Q1）、`9631003`（Q2）、
  `4476185`（Q3）。未追跡 `AGENTS.md` はステージ・変更していない。

## QA-002 U1 診断（2026-07-17）

- 診断条件: Debug / 実 `libmpv` SW render（`vo=libmpv`）/ 1280x720・30fps・20秒動画。
  UIA は別プロセスの FlaUI UIA3 から計測し、広域列挙は
  `Window.FindAllDescendants()`、個別検索は `BtnPlay` の `FindFirstDescendant` とした。
  各10回、1回1.5秒を規定時間とした。Codexプロセス環境だけ `windir` が欠落してWPFが
  起動不能だったため、検証コマンド内だけ `windir=$SystemRoot`（`C:\WINDOWS`）を設定した。
  マシン／ユーザー環境変数は変更していない。
- UIA広域列挙: 停止中は10/10成功、失敗率0%、102要素、平均38.76ms、P50 38.11ms、
  P95/最大44.70ms。通常再生中は0/10成功、失敗率100%（全件1.5秒超）。
- UIA個別検索: 停止中は10/10成功、平均10.10ms、P95/最大10.94ms。通常再生中は
  2/10成功・8/10タイムアウト（失敗率80%）で、成功した2件は平均11.12ms、最大12.84ms。
  既存E2Eは `--vo null` でSW renderを通らないため、実render中の応答性を証明していなかった。
- Dispatcher遅延: 停止安定区間は20標本で平均0.06ms、P50 0.05ms、P95 0.08ms、
  最大0.20ms。通常再生安定区間は2秒あたり20～21標本で平均20.70～21.76ms、
  P50 21.34～21.95ms、P95 22.26～23.56ms、最大24.26msだった
  （`DispatcherPriority.Input`、100ms間隔）。
- render実頻度と占有: ソース30fpsに対して2.0～2.1秒あたり60～63回（約30.0回/秒）。
  `OnRenderUpdate` は平均32.62～32.97ms/回、安定区間最大35.04msで、UIスレッド時間の
  97.86～98.89%を占有した。内訳は `mpv_render_context_render` が平均約32.3ms、
  WriteableBitmap更新は平均0.38～0.60msにすぎず、後者は1回の約1～2%だった。
- 1/2対照実験: WriteableBitmap更新だけを30fpsから約15fpsへ落とし、後段のSpout発行経路は
  スキップしない一時変更で計測した。bitmap更新30～32回／skip 30～32回（2.0～2.1秒）に
  なった一方、renderは60～63回、平均32.66～32.88ms、UI占有率98.02～98.63%のまま。
  UIA広域列挙も0/10成功（失敗率100%）、個別検索も2/10成功（失敗率80%）で回復しなかった。
- **U1結論**: UIスレッド飽和仮説は成立するが、占有源はWriteableBitmapではない。
  UIスレッド上で各フレームの `mpv_render_context_render` が次フレーム相当の約32msを消費し、
  直後に次のBackground renderを再投入するため、UIAプロバイダの広域列挙は全件タイムアウトし、
  個別検索も断続的にタイムアウトする。表示更新半減とAddDirtyRect最適化では原因を除去できない。
- **U2進行判定**: UIスレッド飽和そのものは直接確認できたため停止条件には該当せず、U2へ進む。
  U2では表示だけのスロットリング案を採用せず、`mpv_render_context_render` とSpout全フレーム発行を
  UIスレッドから分離し、WriteableBitmap公開だけをUI Dispatcherへ戻す構成を検討・検証する。

## QA-002 U2/U3 停止（2026-07-17）

- U1の証拠に基づき、`mpv_render_context_render` を単一の専用レンダースレッドへ分離した。
  最初の `Task.Run` 案は実行スレッドが固定されずAccessViolationを起こしたため採用せず、
  レンダーコンテキストの作成・更新・描画・解放を同じ専用スレッドで直列化する構成へ変更した。
- QA-002回帰E2Eは修正前に全ツリー列挙が1.5秒を超えてRED、初期修正後は
  102要素を62.27msで列挙してGREENとなり、5回連続起動でも5/5件合格した。
  新規ユニット6/6件、Debug非E2E 1135/1135件、実機LTCを含むDebug E2E 50/50件
  （Skip 0、2分52秒）も一度は合格した。
- ミューテーション確認では `RenderFrameWorker` の描画可否判定を反転すると対象3/3件が失敗し、
  復元後は3/3件合格した。
- コミット前の独立レビューで、通常描画のawait中にUIスレッドのBlack/Freeze描画が同じ
  PixelBufferとSpoutOutputへ入れる競合を検出した。通常・Black・Freezeのフレーム処理を同じ
  非同期ゲートで直列化し、Spout送信を既存のテスト済み公開パイプラインへ戻した。
  ゲートの同時実行防止・投入順と、専用実行器のDispose待機を追加し、RED（型未実装）から
  関連10/10件GREENまで確認した。
- 競合修正後のQA-002回帰を5回連続実行したところ、1回目は合格したが2回目の広域列挙が
  再び1.5秒を超えて失敗した。失敗時ログでも約30fps、平均render 31.17ms、平均bitmap 0.81ms、
  最大bitmap 16.26msで、mpv描画のUIスレッド占有再発は示されず、残るUIAハング原因は未特定。
- 「E2Eが1件でも落ちたら停止」に従い、期待値緩和、追加再実行、ミューテーション、非E2E/E2E
  最終ゲート、U2コードコミットは実施せず停止した。CHANGELOGのUI Automation制約は削除せず保持した。
  作業差分は未コミットで保持し、U1記録コミットは `b8171e7`。pushは実施していない。
  未追跡 `AGENTS.md` はステージ・変更していない。

## QA-002 U2/U3 再開診断・再停止（2026-07-17）

- 上記停止後に残る1.5秒タイムアウトを再診断した。通常のフレームUI更新を1/4にしても4/5、
  WriteableBitmap更新を完全停止しても4/5、await後にBackground優先度へ戻しても9/10、
  Bitmap・シークバー・時刻・タイムライン更新を全停止しても9/10、mpv render callbackの
  Dispatcher投入を全停止しても9/10で、いずれも広域UIA列挙が1件タイムアウトした。
  各一時変更は復元済みで、映像/UI更新経路の残留占有では説明できない。
- 再生後pause状態でもネイティブ `FindAllDescendants` は17/20となり、停止状態にも同じ失敗が出た。
  段階記録では `UIA3Automation`生成と`FromHandle`は完了し、毎回ネイティブ全子孫列挙内で停止した。
  MicrosoftのUIAスレッド要件に沿う専用MTA Thread、明示 `CoInitializeEx`、幅優先全列挙、
  FlaUI provider transaction timeoutも対照したが、専用Threadはハーネスを悪化させ、幅優先は
  約102回のprovider往復自体が1.5秒を超えるため不採用として関連一時ファイルを削除した。
- 最終対照ではU1と同じTask.Run＋ネイティブ全子孫列挙に戻し、メディア未読込・再生未開始の
  純停止ウィンドウを測ったが0/10で全件1.5秒超となった。その一方、各起動時の個別 `BtnPlay`
  検索は成功しており、テスト版アプリ/testhostの残留プロセスも0件だった。現在のデスクトップ
  UIAセッションでは製品再生状態と無関係に広域列挙経路が恒常的に劣化している。
- ユーザーが以前から起動しているインストール版アプリ2プロセスは確認したが、作業範囲外のため
  終了していない。ログオフ・RDPセッション再作成などの外部状態変更も行っていない。
  停止条件に従い、最終E2Eゲート、CHANGELOG制約削除、U2コードコミットは未実施のまま再停止した。
  作業差分は未コミットで保持し、pushは実施していない。未追跡 `AGENTS.md` は変更・ステージしていない。

## QA-002 U2/U3 外部UIA状態によりblocked（2026-07-17）

- 3回目の継続ターンで外部状態を再監査したが、ユーザーが以前から起動しているインストール版
  TimecodeSyncPlayer 2プロセスは継続していた。テスト版アプリ/testhostの残留はなかった。
- 製品・テスト差分を変更せず最終形QA-002回帰を単独実行したところ、個別BtnPlay検索と再生開始は
  成功した一方、広域列挙は再び `stage=find-all-descendants` のまま1.5秒を超えて失敗した。
- 同一の外部UIA広域列挙ブロッカーが、初回停止、再開診断、今回の3連続ゴールターンで再現した。
  ユーザーアプリの強制終了、ログオフ、RDP/デスクトップセッション再作成には新たな権限と
  外部状態変更が必要なため、これ以上の製品変更・再試行では意味のある進捗ができないと判断した。
- U2実装候補とテスト差分は未コミットで保持し、最終非E2E/E2Eゲート、CHANGELOG制約削除、
  U2コードコミットは未実施。pushは実施しておらず、未追跡 `AGENTS.md` は変更・ステージしていない。

## QA-002 外部プロセス終了後の純停止再測定（2026-07-17）

- ユーザーがインストール版 TimecodeSyncPlayer 2プロセス（PID 43820、50568）を終了した後、
  TimecodeSyncPlayerプロセス0件を確認して再測定した。RDP/デスクトップセッションは再作成していない。
- U1の停止基準と同じく、メディア未読込・再生未開始の純停止ウィンドウに対し、別testhostの
  Task.RunからFlaUI UIA3のネイティブ `FindAllDescendants` を実行した。各回1.5秒、独立起動10回で
  0/10件成功（失敗率100%）となり、全件が `stage=find-all-descendants` でタイムアウトした。
  各テストのタイムアウト確定は約6.97～7.43秒、外側の独立 `dotnet test` 実行は
  約7.92～8.44秒だった。
- 測定終了後もTimecodeSyncPlayerプロセス0件を確認し、QA-002回帰テストは再生中の最終形へ復元した。
  外部プロセス終了だけでは広域UIA列挙の異常は解消しなかったため、ユーザー指示どおり再生中測定、
  最終非E2E/E2Eゲート、CHANGELOG制約削除、U2コードコミットへ進まず停止する。
  次の再開条件はRDP/デスクトップセッションの再作成後、同じ純停止基準が回復することである。
  pushは実施せず、未追跡 `AGENTS.md` は変更・ステージしていない。

## QA-002 U1 Active consoleでの再測定（2026-07-17）

- 前回の純停止0/10はRDP切断中でデスクトップ描画が停止していた計測環境要因と判明した。
  Active console復旧後、デスクトップ直下9要素を56msで列挙でき、TimecodeSyncPlayerプロセス0件、
  CABLEエンドポイント全可視を確認してからU1を再測定した。
- 現行E2Eの `Task.Run` 内で別 `UIA3Automation` を生成する経路もCOM境界により純停止0/10となる
  ハーネス不良だった。同じUIAアパートメントで `MainWindow.FindAllDescendants()` を同期実行する
  経路へ戻すと、純停止は10/10成功、失敗率0%、84要素固定、平均44.00ms、P50 36.84ms、
  最大73.11msとなった。
- 未コミットU2差分を保持したままdetached worktreeで修正前 `3a48273` を構築し、同じ列挙経路で
  再生中REDを確認した。102要素の列挙は10,519.85ms、10,915.61ms、11,126.70msで、
  規定1.5秒を大幅に超過した。
- 修正前のInput優先度Dispatcherプローブは100ms周期208標本で平均13.41ms、P50 3.17ms、
  P95 11.08ms、最大525.28msだった。1280x720・30fps動画の安定区間では2秒あたり
  60～61フレーム、`mpv_render_context_render` 平均32.27～32.39ms、bitmap平均0.39～0.43msで、
  renderだけでUIスレッド時間の約96.8～97.2%を占めた。
- 表示更新だけを2回に1回へ落とす対照実験でも、102要素列挙は10,796.33ms、renderは
  平均32.49～32.52ms、bitmap平均0.19～0.21msで復帰しなかった。Spoutを含む後段と
  mpv render頻度は維持した。
- **再測定結論**: U1の根因を再確認した。詰まりはWPF bitmap更新ではなく、UIスレッド上の
  `mpv_render_context_render` が約97%を占有することにある。表示半減では解消しないため、
  専用レンダースレッドへ分離するU2候補を継続する。診断worktreeの一時変更は製品差分へ含めない。

## QA-002 U2/U3 完了（2026-07-17）

- U1の根因に基づき、mpv render contextの作成・callback登録・更新・描画・解放を単一の
  `RenderThreadExecutor`へ移した。`mpv_render_context_render`の待機をUIスレッドから外し、
  完了フレームのWriteableBitmap反映とSpout送信だけをUIへ戻した。Spoutは表示スロットリングせず、
  成功した各通常フレームを既存の公開パイプラインから1回送信する。
- 通常・Black・Freezeの共有PixelBuffer／Spout処理は`RenderFramePipelineGate`で直列化した。
  トラック世代、Gap表示判断、Freeze capture状態をawait完了後とゲート取得時に再確認し、
  ファイル切替・Sync OFF・Gap退出後に古いフレームやcacheが後着しないようにした。
  scheduler resetは進行中dispatchとpending通知を保持し、callback例外はログ境界で処理する。
- U3回帰は、E2E runnerが所有する同じUIA3Automation／Windowを同じテストスレッドから同期的に
  `FindAllDescendants()`する構成とした。修正前exeでは102要素に10,519.85msかかってRED、
  U2後は独立起動10/10成功、平均44.54ms、最大49.10msとなった。全修正後の最終単独実行も
  102要素、43.03msで1.5秒基準を満たした。
- 新規のworker／専用実行器／pipeline gate／世代・状態ガード／例外境界と既存公開pipelineを含む
  関連テストは22/22件合格した。描画可否判定を反転するミューテーションではworker対象3/3件が
  期待どおり失敗し、復元後3/3件合格した。scheduler pending保持もRED→GREENで確認した。
- 独立コードレビューで、共有buffer競合、async reset再入、古いGap描画・Freeze cache後着、
  callback例外未処理を検出して修正した。最終再レビューはCritical 0件、Important 0件で、
  関連テスト22/22件とSpout全フレーム経路を確認してコミット可能判定となった。
- 最終回帰はDebug非E2E 1155/1155件合格、失敗0、Skip 0。Debug E2EはCABLE実機LTC系列と
  QA-002回帰を含む50/50件合格、失敗0、Skip 0（2分51秒）。CHANGELOGの該当UI Automation
  Known limitationを削除し、`docs/ARCHITECTURE.md`へ専用threadと後着防止規則を記録した。
- pushは実施していない。未追跡 `AGENTS.md` は変更・ステージしていない。

## QA-002 並行性レビュー追補（2026-07-17）

- Dispose中のactive render待機がfaulted Taskを再スローしてteardownを中断する経路を修正した。
  待機例外は`Log.Warning`へ記録し、render context、mpv、Spout、LTC、ピン留めbufferの破棄を
  継続する。faulted／completed／null Taskの3ケースをRED→GREENで確認した。
- render待機中にGapへ進入した場合は、pipeline gate内の通常Publish直前に
  `GapRenderFramePolicy`を再評価する。Gapが有効ならWriteableBitmapとSpoutへの通常フレーム公開を
  抑止し、Freeze最終フレームに必要なcapture-only処理だけを行う。Gap判定反転ミューテーションでは
  対象3/3件が失敗し、復元後3/3件合格した。
- MainWindowから参照されない`RenderFrameCoordinator`と専用テスト4件を削除した。
  最終回帰はDebug非E2E 1157/1157件合格、失敗0、Skip 0。Debug E2EはCABLE実機LTC系列を含む
  50/50件合格、失敗0、Skip 0（2分52秒）。
- 独立追補レビューはCritical 0件、Important 0件でコミット可能判定となり、Dispose後始末、
  Freeze capture、通常状態のSpout全フレーム維持を確認した。
- **長時間ソークテスト（数時間再生＋トラック切替＋ギャップ遷移の連続）は未実施であり、
  次のショー投入前またはv0.3タグ前に実施すべきである。**
- pushは実施していない。未追跡 `AGENTS.md` は変更・ステージしていない。

### QA-002 長時間ソークテスト実施（2026-07-17〜18）

- レンダースレッド分離後の6時間ソークを実施し**合格**した。構成: 2トラック+1分ギャップの
  タイムラインを Continue + Sync ON で実LTC追従、25分送出+30秒無音のサイクルで
  ギャップ遷移・トラック切替・信号断/自動復帰を反復。5分間隔で69サンプル計測。
- 結果: クラッシュ0、メモリ 226〜290MB を往復し単調増加なし（リーク兆候なし）、
  UIA応答 平均23.9ms/最大122ms（修正前10,520ms）、ログの新規エラー0。
- 予定外の実地検証: 試験中にユーザーのRDP切断でWASAPIデバイスが無効化（0x88890004）され、
  「デバイス死→信号断扱いでpause→デバイス復帰→LTC再開→自動再同期」の全段が設計どおり動作した。
- 試験終盤、LTC送出がギャップ帯内で満了する状態が発生し、ギャップBlack+信号断+表示保持が
  重なって「操作不能に見える」ことを確認（動作は全て仕様どおり。Sync OFF/Single切替で復帰可）。
  **v0.3 UX候補**: 信号断中のタイムコード表示の淡色化、ギャップ中の次トラック開始時刻表示など、
  オペレーターが状態を読める表示の検討。
- 前項の「ソーク未実施」注記は本実施をもって解消。
