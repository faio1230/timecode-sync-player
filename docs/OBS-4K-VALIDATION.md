# OBS受信・4K/60fpsの分離検証（2026-09-08〜09）

無効化の調査を保留した後の、公式SDK受信・フルスクリーンを使った更新頻度の測定は [4K動画の公開頻度とBitmap待ちの調査](4K-CADENCE-2026-09-09.md) を参照。安定60fpsは未達で、比較した性能変更2案は未採用。

この記録に続く実装・検証は [Spout調査とUIコピー削減](SPOUT-IMPLEMENTATION-2026-09-09.md) に記載した。標準OBSの実DLLは公式v1.8配布物へ照合でき、UI側の全面コピー1回を除去した。GPU未完了の根本原因、timeout後の安全な回収、標準OBS混在は引き続き未解決である。以下の原始結果と旧版の判定は保持する。

**9月9日時点：GetData未完了100msによる送信無効化を新たに自然再現し、失敗前checkpointと処理段階を記録できた。旧175405の原因との同一性は未確定で、標準OBSの画像混在も残る。受信コピーの確認と実動画の段階別診断を実装・検証したが、無効化・画像整合・4K60性能・同期精度をすべて解決した状態ではない。** 最新結果は下記の段階1〜3に記載する。

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

### 段階1：未完了timeoutの自然再現と失敗前記録（2026-09-08 23:36〜）

**新しい自然発生の無効化を再現し、今回はGetDataが未完了のまま100msへ到達したことを記録できた。GPU処理が未完了だった理由と、旧 `175405-packaged-probe-final` と同じ原因かどうかは未確定。** `13a8725` の完了済み応答を先に判定する修正を保持して開始し、段階1の診断・実機ハーネス・独立レビューをサブエージェントで分担した。実機の起動は親担当が直列に実行した。

今回の証跡は `TestResults/obs-clean/staged-investigation-20260908-233600/`（以下「段階1証跡」）。失敗後のネイティブ解放が長時間停止しても具体例外を失わないよう、プローブに失敗時だけ同期保存する `sender.jsonl.failure.json` を追加した。これは `SpoutOutput` がWarningを発行した後、同クラスの失敗時cleanupへ入る前のcheckpointであり、`SpoutFrameTransfer` 内のmutex解放より前という意味ではない。正常送信ごとのファイルI/Oは追加していない。失敗した送信の外側 `SendMs` にはcheckpointのI/Oも含まれる。

GPU異常時の診断に `GetDeviceRemovedReason` のHRESULTを追加し、HRESULTを取得できなかった場合も元の転送例外を保持する。失敗時の値 `00000000` は、その照会でデバイス削除理由を報告していないことを示すにとどまる。EVENT完了、GPU処理の正常な所要時間、driver内部の原因を証明する値ではない。実際のSpoutDX.dllの逆アセンブルとWindows SDKのABIを照合し、送信に使うcontext、CreateQuery／End／GetData／FlushのCOMスロットとBOOL・query記述のサイズに今回確認した範囲の不一致はなかった（`native-transfer-abi-review.txt`）。ABI照合を原因解消の証拠とは扱わない。

自然失敗の原始データは `TestResults/obs-clean/runs/20260908-234220-stage1-20260908-234216-01/`。標準OBS＋独立受信8ms＋PNG12枚予定、4K60・30秒の5回反復を開始したが、第1回の92回目の送信（FrameIndex=91）で無効化し、後続4回を実行せず停止した。失敗までの送信は92回、予定欠落0、計測約1.816秒、送信exit1・Dispose完了、独立受信exit0だった。30秒の成功試験には数えない。

| 記録対象 | 自然失敗で確認した値 |
| --- | --- |
| 無効化の直接経路 | `stage=GetData`、`hr=00000001`（S_FALSE）、`completed=0`、`gpuElapsedMs=100`、polls=572,137 |
| 送信側の段階時間 | mutex待ち0.002ms、SendImage 1.962ms、End 0.003ms、Flush 0.015ms、poll 99.463ms、cleanup前全体101.453ms |
| 外側の送信時間 | 298.8154ms。失敗checkpointとその後のネイティブ解放を含み、GPU待機時間と同一ではない |
| 失敗checkpoint | 保存成功。送信開始からQPC差122.4177msで記録。後段cleanupに入る前の具体例外を保持 |
| 同時の独立受信 | `copyAndMapMs=138.3070`。送信開始約1.4923ms前から同136.8147ms後まで重なる |
| 独立受信画素 | 混在0 / 164標本、errors=0、droppedLogEvents=0 |

この失敗は、以前修正した `S_OK`・完了値1を期限だけで拒否する経路とは異なる。送受信双方の遅延区間が重なっているが、GPU実行、driver内部の待機、CPUスケジューリングのどれが遅れたかは未特定。OBS統計は約1秒間隔、PNGの記録はファイル時刻であり、特定のスクリーンショット要求やgraphics呼び出しが原因だったとは判断できない。採取できたPNGは1枚で全RGB画素が黒だったが、書き出しは失敗checkpointとOBS送信元resetの後であり、無効化前に黒が出ていた証拠ではない。`stage1-failure-independent-audit.json` と `stage1-failure-png-audit.json` に原始JSONL・PNG・OBSログとの照合と限界を保存した。

異常終了によって `run_pure.py` は最終 `result.json` の作成前に例外を返したため、外側バッチのreceiverResultは空である。独立受信exit0と完全なsummaryは原始stderr／receiver.jsonlで確認した。バッチが追加記録した1,800送信・12PNG未達は早期停止の結果であり、別の無効化原因として数えない。短い失敗runでは実ロードモジュール一覧を採取できず、SHA256は配置済みファイルの同一性確認に限定される。使用製品DLLのSHA256は `EADFB5CD977292351B973CD329938106D27E7119EB25A40147F72217515468BA`。

先に所有する補助プロセスだけでmutex競合を注入し、checkpoint保存・元例外・cleanup完了を確認した（`controlled-mutex-checkpoint/verification.json`）。この意図的失敗は自然再現に含めない。製品とプローブのDebugビルドは警告0・エラー0、診断追加後の非E2Eテストは **1,405件合格、失敗0、skip0**（`tests/stage1-tests.trx`）。別途、プローブのsinkを反射経由で呼ぶ補助検証6例も成功した。正常slowではファイルを作らない、SendImage=falseの先行診断を残す、疑似cleanup停止前に例外を保存する、重複・既存sidecarを上書きしない、書き込み失敗を送信例外へ波及させないことを確認した（`checkpoint-tests-results/results.json`）。この6例はネイティブを起動しておらず、1,405件の内数でもない。補助検証プロジェクトのビルドにはWindowsBase参照競合の警告1件があり、製品ビルドの警告0とは区別する。

自然失敗を受け、条件を分けて次の固定バッファ送信を直列に実施した。

| 条件 | 完了反復 | 送信回数 | 予定欠落 | 最大送信ms | 無効化 | 独立受信の混在／標本 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 送信のみ、`matrix/20260908-234346-stage1-sender-only-*` | 3 | 5,400 | 0 | 13.9509 | 0 | 未測定 |
| 独立受信のみ、`matrix/20260908-234551-stage1-receiver-only-*` | 3 | 5,400 | 0 | 13.4683 | 0 | 0 / 9,440 |
| 標準OBS＋独立受信、PNGなし、`conditions-20260908-235015-obs-cold-receiver-standard-shots-0/` | 3 | 5,390 | 10 | 130.1985 | 0 | 0 / 9,580 |

送信のみ・独立受信のみの6回は送受信exit0、計測・Dispose完了、Warning/Errorなし。送信のみの成功を画像整合やOBS条件の合格とはしない。OBS＋独立受信・PNGなしの正式3回も送受信exit0、計測・Dispose完了、Errorなしだったが、第1回にslow Warning3と予定欠落10があった。最長送信ではSendImage内部126.8329ms、後続poll2.5993msで `S_OK`・完了値1となり継続した。他の2つのslowも完了応答が得られた。PNGなしでも長い遅延は生じており、スクリーンショット処理だけを原因とする根拠はない。無効化0を性能合格と同一視せず、PNGを採っていないためOBS画像整合も未判定とする。

正式なOBS＋独立受信3回の原始結果は `TestResults/obs-clean/runs/20260908-235019-c-20260908-235015-01-*`、`235051-c-20260908-235015-02-*`、`235123-c-20260908-235015-03-*` の各 `run-001/` にある。段階1証跡のconditionsバッチ結果がこの3ディレクトリを参照している。これより前の `conditions-20260908-234746-obs-cold-receiver-standard-shots-0/` は、受信プローブが出力ファイルを開けず `cannot open output` を返し、初回後に停止した。送信側は1,790送信・予定欠落10・最大131.1958ms・slow Warning3、無効化0・exit0だったが、独立受信の条件が成立せず、正式3回には数えない。深い出力階層を避けるためscratchハーネスのResultsRootを `runs/` 直下へ移し、labelを短縮してから取り直した。失敗した原始出力は変更していない。

GPU待機の内部を追うWPR記録も試みたが、`GPU.Verbose.Memory` の開始はprofile system performanceポリシーを有効にできず `0xc5585011` で失敗し、`wpr -status` は記録なしだった（`wpr-start.log`、`wpr-status.log`）。権限・ポリシーは変更しておらず、GPU schedulerのETW証跡は取得していない。

自然失敗時の専用OBSは所有PID34516・HWND47186178、出力ファイル失敗時は所有PID8712・HWND181537716、正式PNGなし試験時は所有PID41876・HWND18549552を確認してWM_CLOSEを送り、3回ともexit0を確認した。各実行の `obs-shutdown.json` と `processes-after.json` を保持し、最後の対象プロセス一覧は空だった。通常OBSや元動画／プロジェクト、ユーザーの他アプリは変更していない。未完了timeout後のmutex・GPU資源解放の安全性と、未完了が長引く原因は引き続き未解決。timeoutの単純延長、未完了画像での送信継続、`hwdec=no` の変更は行っていない。

### 段階2：独立受信の保護範囲を揃えた比較（2026-09-08 23:59〜）

段階1の自然失敗を記録した後、受信側の共有画像アクセスを切り分けるため、[SpoutReceiverProbe](../scripts/SpoutReceiverProbe/README.md) を追加した。これは比較用の独立受信プローブであり、標準OBS pluginや製品の描画経路は変更していない。過去のprivate-copy OBS診断sourceは本番OBSへ導入していない。

既存の独立受信プローブに使われたvendor sourceの `spoutDX::ReceiveTexture()` は、共有画像をprivate画像へ `CopyResource` し、`Flush` の後に `AllowTextureAccess` する。この区間にはコピー完了を確認する待機がない。旧EXEと対応objectの `ReceiveTexture`／`GetSenderTexture` の機械語も再配置箇所を除いて照合した（段階1証跡の `receiver-object-exe-match.json`）。旧受信プローブで「標本混在0」だったことは観測結果として保持するが、それだけで受信器の保護範囲が安全だったとは扱わない。この照合を標準OBS plugin全体の原因確定に拡張しない。

新プローブは同じDebug x64バイナリの `baseline`／`locked` を比較する。baselineはvendorの内部mutexだけ、lockedは指定した実送信名のmutexを外側でも取得し、`ReceiveTexture`、10行のstagingコピー、同期Map、送信名・共有ハンドル・寸法・formatの再確認、Unmapまで保持する。初版の `frame.accepted=true` は正常Mapとmetadata確認後の観測を示すが、後述の初回コピー未確認という制限がある。metadata不一致、接続後の送信元消失、mutex timeout／abandoned、native失敗は正常な受信結果へ混ぜない。

両modeとも接続中の各pollで10行から81点を採取するため、フレーム選別とReleaseビルドを使う旧EXEとは負荷が異なる。mode間ではバイナリ、8ms poll、送信する4K固定画像3色、60Hz、30秒を揃えた。新receiverのSHA256は `299BBD7819A239FF43AAA97DB12FC1B07CA434C484E6093F058BF928E10A8A5A`。vendor sourceを静的リンクし、source・headerのSHA256とライセンス表示をビルド出力へ保存した。製品のSpoutDX.dll置換や新しいデコードライブラリの導入はしていない。

受信器のmutex待ち期限は100msだが、同期Mapとnative解放そのものの時間上限は保証できない。GPU完了を確認できない異常時は正常unlockを抑制して記録し、所有プロセスを外側のwatchdogで終了する。これは診断の失敗処理であり、未完了GPU処理のキャンセルや異常終了中の共有資源安全性を保証する実装ではない。`--external-watchdog` は呼び出し側が監視を用意したことの宣言であり、引数だけでwatchdogが作られるわけではない。

scratchの `stage2_receivers.py` が自分で起動したworkerをjobへ入れてからプローブの起動を許可し、所有プロセスだけを監視・終了する。送信元の正常破棄を受信異常と誤認する競合を避けるため、送信起動29秒後に受信stopを要求し、送信自体は30秒完走させる。末尾約1秒は独立受信の検査範囲外であり、全30秒の受信到達保証とはしない。OBS併用時は送信起動後0.5秒の待機から毎秒PNGを最大12枚採取し、`SaveSourceScreenshot` の要求前後QPCを `obs-observations.jsonl` に保存する。早期のnative失敗でPNGが不足した場合は検査範囲不足として記録し、別の送信失敗原因に数えない。

新receiverのDebugビルドとネイティブを起動しないマーカーself-testは成功した。modeごとの原始JSONL・QPC順序・送受信summary・metadata・画素標本の独立監査を段階1証跡の `audit_stage2.py` で行った。OBSなし比較のバッチは `stage2-20260908-235914/`、原始runは `TestResults/obs-clean/runs/s2-20260908-235914-{baseline|locked}-{01|02|03}/`。独立監査結果を `stage2-no-obs-independent-audit.json` に保存した。

| 条件 | 反復 | 送信回数 | 予定欠落 | 最大送信ms | slow Warning | 無効化 | 独立受信の混在／accepted標本 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 新baseline、OBSなし | 3 | 5,378 | 22 | 154.6259 | 3 | 0 | 0 / 8,795 |
| 新locked、OBSなし | 3 | 5,379 | 21 | 144.7237 | 3 | 0 | 0 / 8,846 |

6回とも送受信exit0、送信計測・Dispose完了、受信errors／logdropは0。rawの送受信summary会計、全標本のaccepted、QPC段階順序と単調性、各run内の寸法・共有ハンドル固定を確認した。lockedのmutex待ち最大6.4472ms、copyAndMap最大1.4516msだった。両modeとも画素混在0であり、この6回だけではlockedによる混在減少の効果を実測で証明できない。各runの画像3色は繰り返すため、metadata固定や画素標本の一致から更新停滞なし・全60フレーム到達を推定しない。予定欠落とslow Warningがあり、4K60性能合格とも判定しない。

独立レビューで、`ReceiveTexture` の成功や初期 `IsFrameNew` の値だけでは、private画像への最初のCopyResourceが一度でも実行された保証がないことを確認した。初版の全原始標本を保持したうえで、最初の成功送信のSendEndQpcよりReceiveStartQpcが前の標本をstartupとして別集計した。任意のウォームアップ秒数で都合の悪い標本を除外していない。OBSなしbaseline／lockedは各run1標本、各mode計3標本がstartupで、混在はどちらも0。送信初回成功後はbaseline 0 / 8,792、locked 0 / 8,843だった（`stage2-no-obs-independent-audit-startup.json`）。この時間境界も受信側の実コピーを直接確認するものではなく、初版のacceptedに残る制限を解決済みとはしない。

次の標準OBS併用バッチ `stage2-20260909-000249/` は初版のbaselineを3回実行し、3回目の自然失敗で止まった。原始データは `runs/s2-20260909-000249-baseline-{01|02|03}/`。このバッチのlockedは未実行である。第1／2回は1,778／1,790送信、予定欠落22／10、最大144.8207／130.3191msで正常終了。第3回は1,747送信目（FrameIndex=1746）でGetDataが `S_FALSE`・完了値0のまま100msへ到達し、無効化して送信exit1となった。poll99.927ms、cleanup前102.045ms、外側SendMs295.3272ms、deviceRemovedReason=0で、失敗checkpoint保存とDispose完了を確認した。

第3回の独立受信は失敗送信開始の383.9651ms前に正常summaryを出し、最終的にexit0を確認した。29秒の受信stop要求は同474.0660ms前だった。ただし初版はsummary後のdestructor完了QPCを記録していないため、summary時刻をnative解放完了時刻とは同一視しない。最終PNG要求の終了も17,761.0745ms前であり、今回の失敗は受信停止を要求してPNG採取も終えた末尾のOBS併用送信で起きた。過去に提出したGPU処理やOBSを原因から除外できる証拠ではなく、GPU未完了の理由は引き続き未確定。

OBS baseline3回の独立受信は混在0 / 8,876標本、errors／logdrop0。startupは3標本で混在0、初回送信成功後も混在0 / 8,873だった。OBS PNGは36枚とも全RGB画素が期待する単色で、今回の36枚では既知のOBS混在を再現しなかった。`stage2-obs-baseline-independent-audit.json` と `stage2-obs-baseline-independent-audit-startup.json` に全画素集計、要求前後QPC、原始ファイルSHA256を保存した。既知の混在反例や旧175405の原因未確定は取り消さない。専用OBSは所有PID40628・HWND18615088へWM_CLOSEを送りexit0、終了後の対象プロセス一覧は空だった。

初版lockedのOBS併用を別バッチ `stage2-20260909-000647/` で2回予定したが、初回の90送信目（FrameIndex=89）で再びGetData未完了100msとなり停止した。原始runは `runs/s2-20260909-000647-locked-01/`。送信は予定欠落0、外側最大305.8587ms、mutex待ち0.002ms、poll99.199ms、cleanup前101.614ms、S_FALSE・完了値0・deviceRemovedReason=0で、checkpoint保存成功・Dispose完了・exit1だった。受信mutexを追加しただけではこの送信無効化を解消していない。

この受信器は `Exact sender or metadata changed during receive; observation rejected` を記録してexit1となり、仕様どおり正常summaryを出さなかった。拒否イベントのQPCは失敗送信開始の195.9934ms後で、失敗checkpointの123.5559ms後よりさらに72.4375ms遅い。イベント時は `mutexOwned=false`、`gpuCompletionKnown=true`、`normalUnlockSuppressed=false` で、Map完了と正常unlock後のmetadata拒否だった。送信無効化後のcleanupによる送信元消失に伴う結果と整合するが、このログだけで因果関係を確定せず、受信拒否を送信timeoutの先行原因とは扱わない。拒否した画像の画素はaccepted標本に含まれない。

それ以前のaccepted標本は154、混在0（startup1／初回送信成功後153、いずれも混在0）。採取済みPNG2枚は全画素が期待単色で、最後の要求終了は失敗開始の123.5358ms前だった。早期失敗で12枚未達であることを別原因に数えない。`stage2-obs-locked-independent-audit-startup.json` に両側の原始結果と全画素監査を保存した。所有OBSはPID46684・HWND60820294へWM_CLOSEを送りexit0で終了した。

初回コピーの判定を補うため、vendorの引数なしReceiveTextureにあるshared→private CopyResource直後へ、受信スレッド内でカウンターだけを増やす診断hookを加えた。元vendorファイルは保持し、生成する `SpoutDX.probe.cpp` だけに適用する。baseline／lockedの両modeで同じhookを使い、コピー累計1以上かつ正常Map・metadata確認後だけacceptedとする。それ以前の画素は `observation.accepted=false` として原始記録に残す。コピー済みprivate画像の再観測と新しいコピーを `copySubmitsThisPoll` で分け、コピー成立を送信元の初回更新完了や一意な動画フレームと同一視しない。

この最終診断版receiverのSHA256は `9BFF8763F229EDE9FB6FED6310E3B44350773F6DAA588C9C77A5A14B75311FBE`。Debugビルドとself-test成功後、OBS併用baseline／lockedを各1回に限定して再検証した。前の299B版のEXE・object・PDB・manifest・ライセンス表示は `receiver-before-copy-hook/` に保持し、結果も版を分けた。新バッチは `stage2-20260909-000948/`、原始データは `runs/s2-20260909-000948-{baseline|locked}-01/`、独立監査は `stage2-copy-hook-independent-audit.json`。

| 最終診断版・OBS併用 | 送信／予定欠落 | 送信exit／無効化 | 最大送信ms | 独立受信の全accepted混在／標本 | startup／初回送信成功後 | OBSの全画素検査 |
| --- | --- | --- | ---: | --- | --- | --- |
| baseline、1回 | 1,780 / 20 | 0 / 0 | 150.5281 | 0 / 2,949 | 1 / 2,948、いずれも混在0 | 期待単色11枚、混在1枚 |
| locked、1回 | 1,749 / 0、早期終了 | 1 / 1 | 288.0019 | 0 / 2,954 | 1 / 2,953、いずれも混在0 | 期待単色12枚 |

両receiverはexit0、errors／logdrop0で、全accepted標本のcopySubmitCountが1以上、カウンター単調増加、各pollのコピー回数1を確認した。初回コピー未確認のままacceptedになった標本はなく、今回rawに画素付き未accepted観測もなかった。ただし両runとも最初の1標本は初回送信成功前のstartupである。共有ハンドル・寸法は各run内で固定、QPC段階順序とsummary会計も一致した。baselineの `obs-009.png` にはRGB128が4,685,824画素、RGB64が3,608,576画素あり、標準OBSの混在反例を再確認した。lockedのPNG12枚が単色でも標準OBS受信処理そのものは変えておらず、OBSの画像混在修正ができたとは判定しない。

最終lockedの送信失敗は1,749回目（FrameIndex=1748）、GetData `S_FALSE`・完了値0・100ms、poll99.849ms、mutex0.002ms、deviceRemovedReason=0で、失敗checkpoint保存とDispose完了を確認した。receiverにはstaging解放、ReleaseReceiver、CloseDirectX11、receiver destructorを含むnative cleanup前後のQPCを追加し、ログflushの時間と分けて記録した。このrunではnative cleanupに83.5841msかかり、**送信失敗開始の384.9707ms前にreceiver destructorまで完了していた**。正常summaryも384.9108ms前、最終PNG要求終了も17,781.5908ms前だった。失敗時点で受信側Mapが継続していたという説明には反するが、過去のGPU処理やdriver内部の影響まで除外する証拠ではない。receiverの正常exitやcleanup完了を、異常GPU処理のキャンセル安全性の保証へ広げない。

この最終2回で、新しい受信診断の初回コピー確認・native cleanup記録を実機で検証できた。一方、送信無効化と標準OBS画像混在の両方が残り、lockedによる解決は実証できていない。予定欠落・slow Warningもあり、4K60性能合格とは別判定である。所有OBSはPID43676・HWND181799860へWM_CLOSEを送りexit0、終了後の対象プロセス一覧は空だった。段階2の実機反復はここで止め、LTCや実動画の測定結果と混ぜない。

### 段階3：実4K動画の描画・コピー・公開時間の分離（2026-09-09 00:12〜00:24）

既存の `synthetic-4k60-changing.mp4`（3840×2160／60fps／H.264、30秒）を同じ製品で再生し、Spout OFF、ONで受信なし、ONで標準OBS併用の順に各32秒を直列測定した。LTC同期はOFF、デコード既定は `hwdec=no` のまま、音声・独立受信プローブ・PNG採取・録画・配信は追加していない。元動画のSHA256 `1F8D01C4B0801F0D57C90FA80940424F2FAB3D7AD537FBACA2931D6175557F2F` と参照だけの元project `ABF80D3D92CE08FBAC4C8DF8003192519722BE6452DF2C6BEFC8E61CF74D2865` は全runで不変だった。

製品に `render-stage` 診断を追加し、mpv render APIの全呼び出し、snapshotコピー、UI側コピー、bitmap公開、Spout呼び出し、Freeze用コピー、未公開snapshotの破棄をQPCで分離した。sessionId／generation／sequenceとnative attemptIdで対応付けるため、公開された画像だけを測って未公開のnative処理を見落とさない。`publish` はbitmap／Spout／Freeze処理と後段callbackを含む親区間であり、子区間や別スレッドの時間を単純加算しない。`native-render` はmpv API全体の所要時間で、デコード単独の時間ではない。既存の `Playback perf avgRenderMs` は診断イベント投入などwrapperの時間も含み得るため、新しい純API区間を主に使用する。

新しい診断は既存の `TIMECODE_ACCURACY_TRACE` に出力先を指定したときだけ有効になる。通常OFFでは段階別の追加時刻取得・イベント生成を行わず、有効時も既存の上限付きキューから背景書き込みする。Python精度解析器は新schemaを検証して記録件数には含めるが、同期精度や画像標本の代わりには数えない。

独立監査は段階1証跡の `audit_stage3.py` で行い、起動・EOFを避けて計測起点の5秒以上27秒未満の22秒を対象にした。traceはQPCで区切り、段階別時間は区間全体がこの範囲に収まるもの、従来の約2秒周期perfログは報告された計測窓全体が範囲に入るものだけを集計した。native呼び出し数は終了QPCが範囲に入る群も別に数え、snapshot／publishと全trace上で結び付けた。区間境界をまたぐ1件の有無を破棄と誤認しない。

最初の `runs/p3-before-trace-off-20260909-0012/` はscratchハーネスが起動直後のMainModule取得でNullReferenceとなり、measurementStartQpc=0、samples=0、強制終了、trace末尾不完全だった。製品の性能失敗に数えず、原始記録を保持した。所有handleに対するQueryFullProcessImageNameと既知WPF HWNDの再確認へハーネスを修正して取り直した。その後の正式5回はすべて測定完了、既知の所有WPF HWNDへのWM_CLOSEでexit0、forcedClose／watchdogなし、traceのend記録とdropped=0／errors=0、素材・DLLハッシュ不変を確認した。

段階別診断追加前のEADFB版は `p3-before-trace-off-retry-20260909-0014` と `p3-before-trace-on-20260909-0015` で、bitmap公開はそれぞれ1,320回／22秒（60.000fps）、1,312回／22秒（59.636fps）だった。段階別診断を追加した正式版の製品SHA256は `6AF83FAFA17287A7E4BE35C9B205A00702AE89B14F9A962F42F8F3BA911318A0`。以下の3回はこの同じ版である。

| 正式版の条件／raw run | 22秒内のnative呼び出し完了 | bitmap公開／秒 | 未公開snapshot | 実Spout状態 |
| --- | ---: | --- | ---: | --- |
| OFF、`p3-trace-off-20260909-0018` | 1,320 | 1,320／60.000fps | 0 | 完全perf窓10件すべてfalse |
| ON・受信なし、`p3-trace-on-20260909-0020` | 1,320 | 1,307／59.409fps | 13 | 完全perf窓10件すべてtrue |
| ON・標準OBS、`p3-obs-20260909-002335` | 1,320 | 1,310／59.545fps | 10 | 完全perf窓10件すべてtrue |

ONのUIボタン表示だけでは内部Spout無効化を見落とすため、perfログの実 `spoutEnabled`、SendFrame例外／false／無効化Warningも確認した。ON2回には該当失敗がなく、分析区間のspout stageもすべて `call-returned` だった。void APIから戻ったというeventだけを送信成功の保証には使っていない。各1回の測定なので、診断前後の小さなfps差を改善・劣化と断定せず、診断の観測負荷も条件差として残す。

| 正式版の段階 | OFF 平均／p95 ms | ON・受信なし 平均／p95 ms | ON・OBS 平均／p95 ms |
| --- | ---: | ---: | ---: |
| native-render（mpv API全体） | 13.420／14.758 | 11.876／13.537 | 11.896／13.327 |
| snapshot-copy | 3.187／4.559 | 4.738／6.423 | 4.714／6.220 |
| ready→UIコピー開始 | 0.147／0.297 | 3.637／11.125 | 3.264／9.481 |
| ui-copy | 4.960／6.351 | 5.499／8.005 | 4.955／6.990 |
| bitmap | 2.650／3.991 | 3.726／7.358 | 3.861／7.708 |
| spout | 無効のため対象外 | 4.437／7.579 | 4.535／7.391 |
| publish（子処理を含む） | 2.658／3.998 | 8.179／12.710 | 8.412／12.971 |

ready→UIコピー開始は、snapshot完成からUIがコピーを開始するまでの全待ちであり、mailbox・UI dispatcher・gateなどを含む。純粋なmailbox待ちとは呼ばない。段階時間の対象は端点まで範囲内の1,319前後〜1,320件で、上表のnative終了群1,320件とは選別基準が異なる。native終了群は全returnCode=0でsnapshot-readyに到達し、ONで未公開だった13／10件をsequenceで辿ると、すべて `discard=mailbox-replaced` と対応した（`stage3-discard-correlation.json`）。今回の公開頻度低下は、native呼び出しが足りないという説明ではなく、準備済みsnapshotが公開前に差し替えられた事象として記録できた。ただしnative呼び出し回数は一意なデコード済み動画フレーム数ではない。

この結果は、次にCPU上のsnapshot／UIコピーとUI側公開の負担を限定して調べる根拠になる。一方、デコードライブラリ自作やGPU描画への全面変更が必要だという証拠にはならない。Freeze用コピーは正式3回の分析区間ですべて `not-needed` で、今回の定常再生のボトルネックとして実測していない。今回はSpout問題を優先し、根拠のない性能変更や追加の大規模反復は行わず、次の修正候補を評価できる診断までとした。

ハーネスはsettingsへ1280×900を指定していたが、実際のWPF外形は正式5回とも1076×680だった。位置は診断前OFF／ONが(156,156)／(208,208)、正式OFF／ON／OBSが(130,130)／(156,156)／(260,260)である。記録したサイズは同じだが位置まで同一条件とは主張しない。また、process採取QPCは同期UIA問い合わせより前、UTC・CPU・Working Setはその後に取得しており、取得時刻が揃っていないことをレビューで確認した。**今回のCPU使用率・5〜27秒のメモリ増減の定量比較は採用しない。** raw samplesは保持し、最終監査では旧計算値を `invalidLegacyEstimates` と明示した。独立した描画trace QPCの頻度・段階時間はこの制約と分けて扱う。旧280MiB増加のリーク／保持問題も今回の値で再判定しない。

正式監査ファイルは段階1証跡の `audit-final-p3-*.json`（5回分）で、初期の監査ファイルは訂正前の派生結果として保持する。OBS付きの設定・統計・終了記録は `stage3-obs-20260909-002335/`。標準sourceはactive/showing、private-copy sourceはinactive、配信・録画なし。前後統計のactiveFpsは約60だが、renderSkippedFramesは8→20で、これは起動・終了を含む統計区間であり分析22秒の画像更新数ではない。OBS画像・一意な動画フレーム到達・LTC同期精度は今回測定していない。過去のLTC付き実4K試験約47〜59fpsとは条件が異なり、今回の約59〜60fpsでその未達を取り消さない。

正式OFF／ON／OBS時のプレーヤー所有PIDは16904／47376／45204、記録したWPF HWNDへの終了要求で全exit0。専用OBSも所有PID22224・HWND46861306へWM_CLOSEを送りexit0、終了後の対象プロセス一覧は空だった。段階3を含む非E2Eテストは **1,413件成功、失敗0、skip0**、既存Python精度解析テスト30件成功。最終の製品・送信プローブDebugビルドは警告0・エラー0で、`final-build-manifest.json` にハッシュと検証数を保存した。ビルド・正常終了・公開頻度の測定成功と、未解決の送信無効化・標準OBS混在・本番同期精度を分けて引き継ぐ。

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
