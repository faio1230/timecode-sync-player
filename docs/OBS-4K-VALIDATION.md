# OBS受信・4K/60fpsの分離検証（2026-09-08）

送信側の画素混在を再現し、実際の送信名に対応するmutex内でGPU転送完了まで待つ修正を実装した。再実行用プローブで単発の送信無効化も検出しており、その原因は未確定。OBS受信側には別の混在反例があり、現時点で「OBSへ常に4K・60枚/秒を正確に出せる」とは判定していない。

## 基準と環境

- 基準commit `01c6b1a`、作業branch `refactor/session-lifecycle`。既存のFreeze・シーク修正を含む。
- 履歴を渡さない担当者による独立調査と、現在のソース・DLL・新規実測を根拠にした。会話履歴そのものを消去したという意味ではない。
- 開始時に残留プレーヤー・プローブ・OBSプロセスなし、物理RAM約37GiB空き。ユーザーのUnity等は停止していない。メモリ逼迫を示す証拠はなかった。
- Windows 11 / Core i9-12900 / RAM64GB / RTX3070 8GB。OBS29.0.0と既存Spout受信pluginを専用portableコピーで使用。OBSのキャンバス・出力は3840×2160、60/1fps。
- 通常のOBS設定・pluginは変更せず、配信・録画も開始していない。コピー内の起動を妨げたNDI pluginのみ無効化した。
- 製品のデコード既定 `hwdec=no` は変更していない。GPUデコード／GPU描画への移行はこの修正に含まない。

測定の原始データは作業ツリー内 `TestResults/obs-clean/runs/`、環境・DLL SHA256は `.superpowers/sdd/2026-09-08-obs-4k-clean-validation/environment-baseline.json` に保存している。これらの大きな実機証跡はGit管理外。

## 送信側の最小再現と修正

mpv・WPF・LTCを起動せず、内容を変更しない4K BGRAバッファ3枚を60Hzで30秒送る。灰色64/128/192、alpha255。独立受信プローブが共有GPU画像を読み戻し、9×9点のBGR243バイトを検査した。

| 条件 | 受信サンプル内の混在／全サンプル |
| --- | ---: |
| 変更前 | 802 / 2,957 |
| 外側mutexのみ | 719 / 3,016 |
| 外側mutex＋GPU完了待ちのC++実験 | 0 / 2,987 |
| 最終製品、OBS検証用受信処理併走、1回目 | 0 / 2,979 |
| 最終製品、同条件2回目 | 0 / 2,741 |

変更前の混在は、送信した2種類の灰色が1枚の受信画像内に存在する反例。mutexを解放する前にGPU完了を確認する必要があった。表の製品2回（173143・173229、assembly SHA256 D082D6B9AA7E9EC32B6B83D3F129A09EC4BFAF153D5B4A6A465A7BE199E6A54F）は各1,800送信／30秒、予定スロット欠落0、送信無効化0、正常終了。これは全画素・全フレーム到達の保証ではない。

実装は `CheckSender → GetSenderName → 実名mutex → SendImage → EVENT End/Flush/GetData → mutex解放`。同名送信元の接尾辞、サイズ変更、部分初期化失敗、終了後の再呼び出しを扱う。mutex待ち・GPU完了pollはそれぞれ100msを期限とし、失敗は送信無効化へ合流する。ネイティブ呼び出し自体の停止時間は保証できない。

初回実装 `9ee20fd` は実機でGPU完了待ちがタイムアウトしたため、成功とは扱わなかった。同じquery/contextでnative pollと比較し、待機がSleepへ移行する `SpinWait` を `Thread.Yield` に変更した `a13eb4c` で再検証した。driver内部の原因は断定していない。新しいnative DLLやNuGet依存は製品に追加していない。終了後guardの修正は `ca2a9da`。

再実行用の製品直結プローブは [SpoutTransportProbe](../scripts/SpoutTransportProbe/README.md)。送信APIの完遂と受信検証を分けて記録する。
### 再実行用プローブで検出した未解決の送信無効化

再ビルド後のassembly SHA256は `D72FF1FF7272C5D76CC959981445B9788A986964FD2AD4836BC9358383230C77`。一時診断ログの追加・除去に伴うソースの改行コード変更後のビルドであり、Git上の製品内容diffは0。上表の旧バイナリと同一ハッシュだとは扱わない。実プレーヤーの正式2runと以下のプローブはこのD72版。

`20260908-175405-packaged-probe-final` は180回目の送信が200.818msかかり、Spoutが無効になってexit1で終了した。初回プローブには具体例外を保存するsinkがなく、原因は未確定。この失敗を回帰テストや再試験の成功で取り消さない。

Warning以上をメモリに保持して結果末尾へ書き出す診断を追加し、標準OBS受信plugin併走で再試験した。

| Run | 送信回数／30秒 | 予定欠落 | 独立受信の混在 | OBSの混在PNG |
| --- | ---: | ---: | ---: | ---: |
| 175610-packaged-probe-diagnostic | 1,800 | 0 | 0 / 3,053 | 0 / 12 |
| 175720-packaged-probe-repeat | 1,800 | 0 | 0 / 3,034 | 1 / 12 |

再試験2回の診断ログにWarning/Errorなし、正常終了。ただし単発の無効化は再現頻度・原因とも未確定で、堅牢性の未解決項目。100ms期限を拡大して問題を隠す変更はしていない。

### 送信無効化の引き継ぎ調査（2026-09-08 22:32〜22:40）

**無効化は未解決。** `refactor/session-lifecycle` の `91034a3` から開始し、未追跡の `AGENTS.md` を保持した。開始時の製品DLLは上記D72版と一致し、SpoutDX.dllは配置元・製品出力・プローブ出力でいずれもSHA256 `BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5`。再ビルド前のプローブ一式も保存した。

元の失敗runの原始データを照合すると、180回目の送信と重なる区間で、独立受信側の `copyAndMapMs` も **137.1726ms** だった。これは両側の実時間上の遅延が重なった証拠であり、送信側がmutex待ち・GPU完了待ち・ネイティブ呼び出しのどこで失敗したか、GPU処理とスケジューラのどちらが遅れたかは特定できない。送信計測には無効化後の解放も含まれ、200.818msから原因を復元できない。

製品の例外診断へ、処理段階、実送信名、画像サイズ、poll回数、最終HRESULT・完了値、GPU待機経過時間、Prepare／mutex準備と待機／SendImage／End／Flushの時間、無効化後の解放を除く全体時間を追加した。計測値はローカル変数で保持し、失敗時だけ `Exception.Data["SpoutTransfer"]` へ文字列化する。`SpoutOutput` のWarning経由で、プローブの既存 `summary.diagnosticLogs` にも残る。未完了の段階の時間は `incomplete`。`SendImage=false` のログから根拠のない `device lost?` を除いた。

この調査時点の実装では、`GetData` が `S_OK`・完了値1を返した場合でも、直後の期限判定が100ms以上なら無効化する。この経路と未完了のtimeoutを区別する診断テストを追加した。これは注入した時計と疑似GPU応答による経路確認であり、実機失敗の原因とは判定しない。[GetDataの戻り値仕様](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata)に照らして最終応答を記録するが、この時点の診断追加では期限・完了判定順序・mutex保護・失敗時の解放動作を変更していない。`hwdec=no` も維持した。

専用portable OBS29.0.0の標準 `spout_capture`、4K60、独立受信プローブ8ms poll、PNG最大12枚、製品直結送信30秒を固定し、すべて直列実行した。標準OBS入力がactive/showing、private-copy診断入力がinactive/非表示であることを確認した。配信・録画は開始していない。診断付き製品DLLのSHA256は `A6DC6EAA8F73902FD6759254A3CE3CD4FF75594FD0B35B233E59FB503A41B2EA`。プローブで実際にロードされた製品・SpoutDXとOBS側DLLのパス・ハッシュも保存した。

| 条件／run | 送信回数 | 予定欠落 | 最大送信ms | 無効化 | 独立受信の混在／標本 | OBSの混在PNG |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| D72版 `223303-disable-baseline-01` | 1,789 | 11 | 150.6500 | 0 | 0 / 3,162 | 2 / 12 |
| D72版 `223352-disable-baseline-02` | 1,800 | 0 | 13.3540 | 0 | 0 / 3,211 | 1 / 12 |
| 診断版 `223511-disable-diagnostic-01`〜`223948-disable-diagnostic-10`（10回） | 18,000 | 0 | 13.5828 | 0 | 0 / 32,019 | 8 / 120 |

12回ともexit0・初期化／計測／Dispose完了、Warning/Errorなし。初回には16.667msを超えた送信が3回あり、正常終了を性能合格と同一視しない。独立受信の疎な画素標本に混在がなくても、OBSの全画素検査では計11/144枚に異なる送信画像の混在が残った。LTC／動画再生は今回起動しておらず、同期精度・4K動画の実画像更新・長時間メモリ増加は再判定していない。

Debugビルドは警告0・エラー0。非E2Eテストは **1,369件合格、失敗0、skip0**（期限時の最終応答を区別する3例を追加）。送信無効化の再現も原因の証拠も得られなかったため、動作修正は行っていない。次の失敗では `diagnosticLogs` の処理段階・HRESULT・完了値を根拠に、実際の未完了待機、完了済み応答の期限判定、ネイティブ失敗を分けて追う。

別途、OBS終了後の22:42に、所有する補助プロセスで送信名のmutexを意図的に保持する診断経路試験を1回実施した。製品プローブの初回送信が期待どおりexit1となり、`stage=WaitMutex`・`polls=0`・`hr=n/a` と具体的な `TimeoutException` が `summary.diagnosticLogs` に保存された。SendFrame全体は137.7579ms、無効化後の解放を除く診断時間は105.117ms、Disposeは完了。これは記録経路の確認であり、元の単発障害の再現に数えない。送信／GPU完了pollには到達せず、補助プロセスも正常終了した。

原始データは `TestResults/obs-clean/runs/20260908-*-disable-*`。環境・実ロードDLL・元失敗との時間照合・PNG全画素集計・TRX・再ビルド前のバイナリは `TestResults/obs-clean/disable-investigation-20260908-223249/` に保持した。検証用OBSは今回起動したPID・開始時刻・実行パスとHWNDを確認してWM_CLOSEで終了し、exit0を確認した。通常OBSの設定、元動画／プロジェクト、既存ログ、他アプリは保持した。

### サブエージェントによる追加調査と完了判定順序の修正（2026-09-08 22:49〜）

**元の `175405-packaged-probe-final` の原因は未確定。一方、停止を注入した実機試験で、GPUが完了済みでも期限判定によって送信を無効化する経路を確認し、この経路を最小修正した。** 調査・診断追加・直列ランナー・レビューを分担し、GPU／OBSを使う試験の起動と終了は親担当が一つずつ実行した。既存変更と原始証跡を保持し、GPU描画への変更やLTC／デコードの変更は行っていない。

今回の証跡の親ディレクトリは `TestResults/obs-clean/subagent-investigation-20260908-224946/`（以下「今回証跡」）。`binaries-before.json`、`os.json`、`gpu.json` と開始時プロセス一覧を保存した。製品SpoutDX.dllは引き続きSHA256 `BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5`。診断追加後・完了判定順序修正前の製品DLLは `028A2448FAA75F2740EEB9FE2134AB0EBB3B11F32A7B2B3A7A52F0BF3BDD66AD`、修正後は `2CF486B39023CA2BF5BF93A9E088609E2A53D146AD3FDB67C27CA921BE3028BC`。`measurement-binaries.json` と `fixed-binaries.json` で識別し、異なる版の反復結果を同じバイナリの検証回数へ混ぜない。

診断は失敗時だけでなく、正常転送が16.667ms（1/60秒）を超えた場合も `SpoutFrameTransfer: slow send {@Transfer}` のWarningを記録する。Prepare／CreateMutex／WaitMutex／SendImage／End／Flush／GetData／ReleaseMutexの実経過時間を保持し、失敗した段階も開始済みなら時間を残す。未開始は例外文字列で `not-started`、構造化値で `null` とする。前節の `incomplete` は旧診断の仕様である。`PollMs` はGetDataとThread.Yieldを含むpoll全体、`TotalBeforeCleanupMs` はログ処理とSpoutOutput側の失敗後解放を除く時間。送信プローブの `SendMs` との区別を維持する。

`summary.diagnosticLogs` は既存のmessage／exceptionを残し、messageTemplateとpropertiesを追加した。正常な遅い転送とSendImage=falseの段階別値は構造化された `properties.Transfer` のSender、Stage、StartQpc、EndQpc、各Msなどから解析する。例外時の `Exception.Data["SpoutTransfer"]` は失敗時に製品側で文字列化され、対応するTransferも文字列である。sinkへの記録はメモリ内で行い、sinkに保持したログイベントの文字列化・JSON変換・書き出しは計測終了後。Warningの存在を無効化と同一視しない。

固定条件の切り分けは3840×2160、60Hz、30秒を使用した。以下は完了判定順序の修正前の診断版で、9回ともexit0、計測・Dispose完了、無効化0、Warning/Errorなし。

| 条件 | 反復 | 送信回数 | 予定欠落 | 最大送信ms | 独立受信の混在／標本 | OBS画像整合 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 送信のみ、`matrix/*sg-sender-only*` | 3 | 5,400 | 0 | 14.3523 | 未測定 | 未測定 |
| 独立受信のみ、`matrix/*sg-receiver-only*` | 3 | 5,400 | 0 | 15.0704 | 0 / 9,515 | 未測定 |
| 標準OBS＋独立受信、PNG保存なし、`runs/20260908-230030`〜`230132-sg-obs-no-shots-*` | 3 | 5,400 | 0 | 15.0634 | 0 / 9,516 | PNG未採取 |

OBS試験は専用portableコピーの標準 `spout_capture` をactive/showingとし、private-copy診断入力はinactive/非表示、録画・配信なし。4K60設定と入力状態は `obs-fixed-settings.json`／`obs-fixed-settings-final.json` に保存した。この9回で自然発生の無効化がなかったことは、元の単発障害の解決や受信条件との無関係を示さない。

次に、自分で起動した送信プローブの主スレッドだけへ150msの停止を最大16回注入した。GPUに疑似完了応答を返すテストではなく、通常のSpoutDX／D3D11経路の途中でCPU側の観測を遅らせる試験であり、元障害の自然再現とは区別する。

| 注入run | 注入回数 | 送信／予定欠落 | 最大送信ms | 無効化／exit | 観測 |
| --- | ---: | ---: | ---: | --- | --- |
| `controlled-deschedule-before-01` | 2 | 121 / 9 | 185.2136 | 1 / 1 | GetData最終応答hr=0・completed=1、gpuElapsedMs=153なのにtimeout |
| `controlled-deschedule-before-02` | 16 | 1,667 / 133 | 174.6323 | 0 / 0 | 遅延はSendImage段階で観測し、期限後の完了応答経路には命中せず |
| `controlled-deschedule-after-01` | 16 | 1,668 / 132 | 170.2584 | 0 / 0 | hr=0・completed=1、GpuElapsedMs=151、PollMs=151.5751でもCompleted／SendSucceeded=true |

before-01では具体的な `TimeoutException` と `stage=GetData; hr=00000000; completed=1; gpuElapsedMs=153` が保存された。GPU完了済み応答を期限超過だけで失敗へ変えることが、ここでは送信無効化の直接原因だった。before-02の成功は注入位置に依存しており、before-01の反例を取り消さない。

この証拠を得てから、負のHRESULTを失敗として扱ったうえで、`S_OK`かつ完了値が非zeroなら100ms判定より先に成功とする順序へ変更した。mutex待ち100ms、GPU未完了時の100ms期限、Thread.Yieldは維持した。after-01は期限を過ぎて観測された完了済み画像の経路でも送信が継続し、30秒の計測とDisposeを完了した。ただし人工停止で予定欠落132があり、この試験を4K60性能合格とは判定しない。受信画像と同期精度もこの注入試験では測定していない。

未完了のGPU処理を新たに成功と扱ったり、共有画像を無保護のまま送信継続したりする変更は含まない。従来からの未完了timeout／異常終了経路におけるmutex解放とGPU資源解放の安全性は、今回の成功先行修正では解決していない。完了が確認できたケースの修正範囲と分け、未解決として残す。

再利用可能な [直列ランナー](../scripts/SpoutTransportProbe/Run-SpoutTransportMatrix.ps1) を追加した。一意の結果ディレクトリへ原始stdout／stderr／JSONL、開始終了、所有PID、終了コード、summaryとバイナリSHA256を保存する。既知の送信プロセスがあれば開始を拒否し、終了処理は自分で起動したProcessオブジェクトだけに限定する。無効化・異常終了・不完全summary・watchdog超過では初回で止め、正常slow Warningと予定欠落は記録して続行する。受信混在は既定で記録のみ、厳格停止はオプションとし、画像整合と送信継続の判定を分離した。

今回のホストからWindows PowerShell 5.1を `-File` で起動した際は、param既定式の `$PSScriptRoot` が空になり、実機プローブ起動前にJoin-Pathで停止した（`controlled-mutex-runner/runner-stderr.log`）。SenderExe／ResultsRootの既定パス解決をスクリプト本体へ移し、5.1で構文解析と既定／空白入り明示パスの引数評価を確認した。再試験時にはCodexのPowerShell 7用runtime Modulesパスを継承したことによる `Get-FileHash` 不在で停止したため、検証用5.1子プロセスの `PSMODULEPATH` だけを標準WindowsPowerShellのModulesへ設定した。親・ユーザー環境は変更していない。これは今回のホスト条件での起動確認であり、PowerShell 5.1一般の不具合とは断定しない。起動前失敗の原始出力も保持し、製品が実行されていない2回をSpout無効化や製品検証回数には数えない。

その設定で、自分の補助プロセスが送信名mutexを保持する失敗経路を確認した（`controlled-mutex-runner-winps/verification.json`）。3回予定のランナーは第1回だけを作成し、送信・ランナーとも期待するexit1で停止した。記録は `stage=WaitMutex; polls=0; hr=n/a` と具体的なtimeout、SendImage以降は `not-started`、Dispose完了。mutex保持プロセスもexit0で終了した。これはランナーの初回失敗停止と記録・終了経路の合格であり、正常送信の合格や元障害の自然再現ではない。

同じ子プロセス環境のPowerShell 5.1で、送信のみ30秒×1回の正常経路も確認した（`winps-success/verification.json`）。送信・ランナーともexit0、1,800送信、予定欠落・無効化・Warning/Error・16.667ms超過はすべて0、最大13.1985ms、計測・Dispose完了。外側の後処理がバッチ結果を `result.json` と誤指定した際のFileNotFoundは、保存済みの正しい `batch-result.json` と `run-001/result.json` をオフラインで確認し直した。製品試験を再実行して取り直した結果ではなく、後処理の訂正として記録した。この1回をOBS付き20回には含めない。

Debugビルド成功。完了判定順序修正後の非E2Eテストは **1,395件合格、失敗0、skip0**。`tests/completion-order-tests.trx` のtotal／executed／passedがすべて1,395、failed／notExecutedは0である。

修正後の固定版で、標準OBS＋独立受信＋PNG保存あり、30秒×20回を直列実行した。`runs/*sg-fixed-obs-shots-01`〜`20` の原始JSONLとPNG全RGB画素を別担当が照合し、`final-audit.json` に集計と元ファイルのSHA256を保存した。

| 判定対象 | 修正後20回の結果 | 判定範囲 |
| --- | --- | --- |
| 正常終了・無効化 | 20回とも送受信exit0、計測・Dispose完了、無効化0、Warning/Error0 | この固定条件の計10分で自然発生無効化を再現せず |
| 送信の予定・時間 | 36,000送信、予定欠落0、16.667ms超過0、最大14.1739ms | 固定バッファ送信の結果。4K動画の実画像更新性能とは別 |
| 独立受信の画素 | 混在0 / 63,763標本 | 9×9点のBGR検査。全画素・全フレーム到達の保証なし |
| OBSの画像 | 240枚中、期待する単色224枚、混在15枚、全黒1枚 | 全RGB画素検査で画像整合は未合格 |
| 同期精度 | 未測定 | LTC・動画再生を起動していない |

全黒は `20260908-231032-sg-fixed-obs-shots-04/obs-000.png` の8,294,400画素すべてRGB0。送信バッファの灰色64／128／192には該当せず、混在15枚と分けて保持した。黒の発生原因・継続時間はこの1枚から判定しない。独立受信で混在がなくても、標準OBS側には今回も混在反例が残る。

終了後の送信プローブ・製品・SpoutDXのファイルSHA256は `fixed-binaries.json` と一致した。記録済みの実ロードモジュールも同じ版であることを照合したが、20回すべてでロード先を個別採取したとは主張しない。専用OBSは所有PID23508とHWND46530818を確認して終了要求を送り、exit0を確認した（`obs-final-shutdown.json`）。通常OBSの設定、元動画／プロジェクト、既存ログとユーザーの他アプリは保持した。

今回もLTC同期精度、4K動画の実画像更新頻度、長時間メモリ増加は測定していない。元の175405の原因が未確定であること、本番4K60／常時1フレーム以内同期の未達は維持する。今回修正できたのは、停止注入で証拠を得た「完了済み応答を期限だけで拒否する経路」であり、すべての送信無効化の解決ではない。

## OBS受信側の反例と制限

送信側をC++実験のGPU完了待ちで保護しても、元のOBS Spout sourceのPNG12枚中1枚に混在が残った。`20260908-170026-obs-fence/obs-006.png` は灰色192が6,668,288画素、灰色64が1,626,112画素。同時の独立受信プローブは混在0/2,862だった。OBSの `activeFps≈60`、renderSkipped増分0でも、この反例は発生した。

最終製品と標準OBSでも反例を再確認した。`20260908-175720-packaged-probe-repeat/obs-002.png` は灰色192が5,531,648画素、灰色64が2,762,752画素。同時の独立プローブは混在0/3,034だった。

専用OBSコピーにだけ入れた診断用sourceで、mutex取得後に共有画像をprivate textureへコピーし、GPU完了後にprivate textureだけを描画した。C++送信実験の2回、PNG計30枚では全画素混在0。最終製品での2回もPNG計29枚に混在はなかったが、1回目の4枚は黒だった。取得競合で更新が滞る可能性があり、黒・保持・混在は別の指標として扱う。

この受信sourceはGPU完了待ちが無期限で、取得時のWait0による更新停滞も未解決。本番用pluginとして配布・導入する段階ではない。GPU copy後のtimeoutで単純にmutexを解放すると、読み出し途中の画像を送信元が上書きできてしまう。製品化には、受信側の資源所有権・待機・再接続・終了時処理の設計と検証が必要。

PNGはOBSが生成した画像であり、物理モニターの表示測定ではない。疎な画像検査から全60Hzフレームの一致や更新を保証しない。

## 実プレーヤーと回帰検証

最終の通常長テストは、1080pマーカー動画3本と3840×2160/60fpsの検証動画で各1回正常終了した。VB-Cableから25fps LTCを入力し、標準OBS Spout sourceを使用。どちらも原プロジェクトのハッシュ不変、アプリtrace・受信プローブの末尾記録あり、記録drop/errorは0。非E2Eテストは **1,366件合格、失敗0、skip0**。

1080p試験（`20260908-174655-player-marker-verified`）では、OBSのBlack画像5/5が全RGB画素0、Freeze画像5/5が末尾239・299・599。通常／seek画像18枚も期待clipのマーカーを確認した。OBS出力PNGは4Kだが、入力動画自体は1080pなので4K再生性能の根拠には使わない。

**同期精度は未合格。** LTC入力に2か所の1フレーム欠落があり、厳格な精度解析は `complete=false`。独立受信プローブでの定常フレーム区間誤差は平均絶対35.43ms、p95 85.07ms、最大133.33ms。全遷移を含む最大は626.67msで、定常値へ混ぜていない。60fps clipへのシークでは「誤差80ms以内を500ms持続」を5秒の測定区間内に観測できず、29.97fps clipでは約4.02秒後だった。250ms基準なら全シークで約151〜256ms後に持続回復した。数値はデコード済みLTC受信から独立プローブの画像までであり、OBSの時系列画像／物理画面のms精度ではない。

4K試験（`20260908-174958-player-4k-verified`）は実3840×2160/60fpsの合成検証動画を3トラックに配置した。通常再生と前後・別トラックへのシークを含むが、gap横断phaseやフレーム番号マーカーはないため、4KでのFreeze末尾・厳密なシーク精度は未測定。LTC入力は989標本で、区間内部の欠落も1件残った。

OBSの合成周期は約60fps・renderSkipped増分0だったが、同じ定常入力区間でアプリ画像公開は46.96〜58.78fps、独立受信プローブの画素ハッシュ更新は46.71〜58.78fps。通常再生区間はアプリ56.23fps／独立受信56.09fps。ハッシュ変化は画面全体の一意なフレーム番号ではなく、静止・繰り返し内容の区別に限界がある。

4K時のアプリCPU平均は5.140論理コア相当、Working Set最大1006.90MiB。観測開始から終了まで約279.66MiB増加し、Private Bytesも約258.25MiB増えた。短い測定内の保持増加であり、長時間リークの有無や原因は未判定。起動前の空きRAM確認と、この再生中の増加は別の結果。
本番の「常に1フレーム以内」や「常に60枚/秒」は、この結果から約束できない。次はLTC欠落の発生箇所、同期許容幅と表示遅延、OBS受信側の安全な画像コピーを検証する。

初期runは全操作phase後に終了待ちが失敗し、trace末尾が不完全なため合格に含めない。Process.CloseMainWindow使用時の4K runでは既知WPF HWND=77728494、Process選択HWND=21171064（title空）と異なった。FlaUIのCloseボタンInvokeでも別runで終了に至らず、HWND相違だけで全失敗を説明できるとは断定していない。既知WPF HWNDの所有PIDを確認してWM_CLOSEを直接postするテスト補助処理へ変更した短時間診断では、Closing入口、native barrier完了、context解放、正常終了を確認し、trace footerのerrors/droppedは0だった。製品の終了処理は変更せず、一時診断ログも除去した。

## 次の検証に必要なもの

VB-Cableはこの環境で利用可能。追加の音声機材は現時点では不要。次の本番条件の確認では、本番PCで使うOBS／Spout pluginの版、GPU、実際の4K60素材を固定する。受信pluginの安全な画像コピーと、プレーヤーからOBSまでの画像更新頻度を別々に確認する。今回の診断pluginを本番OBSに入れない。

過去のデコード比較は [SPOUT-DECODE-COMPARISON.md](SPOUT-DECODE-COMPARISON.md)。今回のOBS付き測定とは条件が異なるため数値を混ぜない。

検証終了後、専用OBS・プレーヤー・受信プローブを終了した。終了後の物理RAM空きは約37.5GiB。通常のOBS設定とユーザーの他アプリは保持した。
