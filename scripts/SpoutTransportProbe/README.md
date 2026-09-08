# SpoutTransportProbe

製品の `SpoutOutput` を直接呼び出す、Windows x64 用の送信計測器です。WPF アプリケーション、mpv、LTC 音声入力を起動せず、現チェックアウトの製品コードを `ProjectReference` で利用します。実験用 native helper、reflection、診断用コピーには依存しません。

3840×2160 の固定バッファを3枚用意し、各バッファの B/G/R はそれぞれ同じ値の 64、128、192、A はすべて255にします。準備後の画素を変更せず、バッファをピン留めしたまま、60 Hz の予定スロットに合わせて30秒間交互に送信します。予定時刻を過ぎたスロットは数えて飛ばし、追いつくための連続送信は行いません。送信元の破棄が完了してからピン留めを解除します。

## 実行

.NET 8 SDK と製品が利用する x64 `SpoutDX.dll` が必要です。DLL の用意は `../../native/README.md` を参照してください。リポジトリのルートで実行します。

```powershell
dotnet build scripts/SpoutTransportProbe/SpoutTransportProbe.csproj -c Debug
& .\scripts\SpoutTransportProbe\bin\Debug\net8.0-windows\SpoutTransportProbe.exe "$env:TEMP\spout-probe-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss')).jsonl"
$LASTEXITCODE
```

出力ファイルのパスは必須です。既存ファイルへの上書きは拒否します。失敗時用の `<output.jsonl>.failure.json` が既に存在する場合も起動を拒否します。出力先ディレクトリは必要に応じて作成します。製品のビルド設定から `SpoutDX.dll` が実行ファイルの隣にコピーされていることを確認してください。

終了コード0は、初期化、30秒の計測、送信元の破棄、結果ファイルの書き出しまで完了したことを示します。引数・出力先の準備エラーは2、実行・終了処理・書き出しの失敗は1です。強制終了などで `summary` 行が残らなかった計測は未完了として扱ってください。

## 記録と判定範囲

JSONL は `start`、スロットごとの `send`、最後の `summary` から成ります。送信呼び出し時間、予定時刻からの遅れ、予定スロット欠落数、プロセスCPU時間、GC回数を記録します。CPU使用率は1コア相当を100%とし、待機処理のスピンも含みます。計測ループ内ではファイル・コンソールへの書き込みやログ出力先の設定を行いません。

製品の Warning 以上のログはスレッドセーフなメモリ内キューに保持し、計測終了後に `summary.diagnosticLogs` へ時刻・レベル・メッセージ・例外詳細を出力します。初期化、送信、破棄で製品側が捕捉した例外も確認できます。通常の Information/Debug ログは記録せず、正常送信やslow Warningではsinkでの文字列化やディスク・コンソール書き込みは行いません。

送信が失敗した場合だけ、SpoutOutputがネイティブ終了処理を始める前に出す例外またはSendImage=falseのログを検出し、最初の1件を `<output.jsonl>.failure.json` へ同期書き込み・flushします。UTC、QPC、所有PID、元ログの時刻・テンプレート・プロパティ・例外を保存します。終了処理が停止し、watchdogによってプローブが終了してsummaryを残せなかった場合にも、この失敗記録を調べられます。ネイティブ呼び出しが失敗ログを出す前に停止した場合の記録は保証しません。書き込みはCreateNewで既存データを保持し、I/O失敗は元の送信失敗を置き換えずメモリへ保持します。正常にsummaryを書けた場合は `failureCheckpointAttempted`、`failureCheckpointWritten`、`failureCheckpointError` に成否が残ります。強制終了時はcheckpoint自体の欠損・不完全JSONも未完了として扱ってください。

この失敗時だけの文字列化・ファイルI/O時間は、外側の送信計測 `SendMs` に含まれます。送信失敗の所要時間を旧版と比較するときは、その差をGPUやmutex待ちと扱わず、I/O前に確定したTransferの段階別時間・`TotalBeforeCleanupMs` を参照してください。正常送信の計測条件は変えません。

各診断ログは既存の `message`・`exception` に加え、`messageTemplate` と型を保った `properties` を持ちます。正常遅延および `SendImage=false` の構造化診断は `properties.Transfer` の `Sender`、`Stage`、`StartQpc`、`EndQpc`、各 `Ms` 値から解析できます。例外の `properties.Transfer` は診断文字列として保持します。summary向けのJSON変換は計測後に行い、上記の失敗checkpointだけは終了処理前に変換します。

転送中の例外には製品側が失敗時に整形する `transfer` 診断が含まれます。正常な転送も16.667ms（1/60秒）を超えると `SpoutFrameTransfer: slow send {@Transfer}` のWarningを出し、`SendImage` がfalseを返した場合は別のWarningで記録します。Warningの存在だけで送信失敗とは判定しません。処理段階、実送信名、画像サイズ、poll回数、最終HRESULTと完了値、GPU待機経過時間に加え、Prepare・CreateMutex・WaitMutex・SendImage・End・Flush・GetData・ReleaseMutexの段階別時間を確認できます。開始した段階は失敗時も実経過時間を記録し、未開始は例外文字列では `not-started`、構造化値では `null` です。

`PollMs` はGetDataとThread.Yieldを含むpoll全体、`TotalBeforeCleanupMs` はログ処理とSpoutOutput側の失敗後解放を除く時間です。プローブの `SendMs` にはそれらも含まれるため、所要時間だけでmutex timeoutやGPU timeoutと判定せず、例外・最終応答・各時間を照合してください。失敗checkpoint以外のログイベントの文字列化とファイル書き込みは、引き続き計測終了後です。診断の追加自体は無効化の原因特定や解決を意味しません。

GPU完了を確認できた応答は、観測までに100ms以上かかっていても成功として受理し、遅延をWarningへ記録します。未完了応答のpoll期限とmutex待ちの期限は各100msのままです。これは完了済み転送の不要な無効化を避ける処理であり、100ms以内のGPU処理・全体送信時間を保証するものではありません。

`attemptedFrames` は送信APIの呼び出し回数です。終了コード0でも予定スロット欠落はあり得るため、`missedScheduledSlots` と `sendOverBudget` を確認してください。`SendFrame` は戻り値を返さず、受信結果も取得しないため、`transportMisses` は `null` です。送信側の定量結果だけでは、受信の成功、全画素の一致、全フレームの到達を保証できません。3色の繰り返しだけでは、受信側で任意のフレーム欠落を一意に判定することもできません。

要求する送信名は `TimecodeSyncPlayer` ですが、同名の送信元が複数あるとSpout側で接尾辞が付く場合があります。製品アプリなど他の送信元を停止するか、受信側でこのプローブに対応する実際の送信名を確認してください。標準出力や要求名だけを見てOBSがこのプローブを受信したと判定しないでください。

OBSによる受信、録画、画素・フレームの解析は別途必要です。OBSの private-copy 対策は本番導入済みとして扱いません。このプローブはOBSのプラグインや受信処理を変更しません。

## GPU未完了時の実行記録

`Capture-SpoutGpuTrace.ps1` は1回の製品プローブとGPU／CPUスケジューラのWPR記録を対応付けます。まず通常のPowerShellで `-Preflight` を使うと、既存プロセス・DLLハッシュ・WPR profileの解釈・権限を記録するだけで、プローブや記録を開始しません。`result.json` の `canRecord` が実採取可能性、`traceSaved` がETL保存結果です。

```powershell
& .\scripts\SpoutTransportProbe\Capture-SpoutGpuTrace.ps1 -OutputDirectory .\TestResults\obs-clean\runs\NEW_PREFLIGHT -Preflight
# 実採取は管理者として起動したPowerShellから実行する。スクリプト自身は昇格しない。
& .\scripts\SpoutTransportProbe\Capture-SpoutGpuTrace.ps1 -OutputDirectory .\TestResults\obs-clean\runs\NEW_GPU_TRACE
```

既定の `SenderOnly` はOBS・プレーヤー・既知の受信／音声プローブがあると拒否します。標準OBS接続を観測する場合は `-Condition ExistingObs -ObservedObsPid <確認済みPID> -ExpectedObsExe <実行ファイル絶対パス>` を指定し、OBS側のsender選択・source表示・4K60設定を別途保存してください。このスクリプトはOBSの起動・設定変更・終了をしないため、引数だけで受信条件成立を証明できません。

新規出力ディレクトリだけを使用し、固有のWPR instance名で開始成功した記録だけを保存終了します。既存記録へのcancelや権限・ポリシー変更は行いません。送信プローブは所有Process handleで監視し、既定45秒で終了させます。WPRのETL統合時間はこの送信watchdogに含みません。保存失敗時は結果とinstance名を保持し、そのinstanceに限定して状態確認・回収してください。OS終了やPowerShell自体の強制終了まで含む自動回収を保証するものではありません。

profileはCPUスケジュール／stackとDxgKrnl・D3D11・DXGIを記録し、メモリバッファ設定は計256MiBです。循環記録のため、ETLの対象時刻・event loss・必要なproviderの存在をWPAで確認してから失敗QPCと照合してください。`traceSaved=true` だけでは記録の完全性やGPU原因特定を保証しません。観測負荷のある試験として、通常プローブの性能値とは区別します。[WPRのinstance指定](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options)、[メモリ記録の上書き](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/logging-mode)

今回の環境ではprofile解釈と事前確認まで検証し、管理者権限がないためETLの実採取・解析は未検証です。

## 条件を固定した直列反復

`Run-SpoutTransportMatrix.ps1` はビルド済みの製品プローブを30秒ずつ直列実行します。リポジトリルートからWindows PowerShellで実行してください。ビルド、OBS設定変更、音声入力の起動は行いません。

```powershell
# 送信プローブのみを20回起動する（約11分）。
& .\scripts\SpoutTransportProbe\Run-SpoutTransportMatrix.ps1 -Runs 20 -Label sender-only

# 独立受信プローブを各試験の送信開始前に起動する。
& .\scripts\SpoutTransportProbe\Run-SpoutTransportMatrix.ps1 -Runs 20 -Label independent-receiver `
    -ReceiverExe .\.superpowers\sdd\2026-09-08-spout-decode-compare\receiver\SpoutReceiverProbePixels.exe
$LASTEXITCODE
```

`-SenderExe` で別のビルド済み製品プローブを指定できます。受信付きでは指定した実行ファイルへ `--sender TimecodeSyncPlayer --output <新規receiver.jsonl> --duration 50 --poll-ms 8 --stop-file <新規receiver.stop>` を渡します。送信終了後にstopファイルを作成し、受信終了を最大10秒待ちます。送信watchdogは既定45秒です。試験間は既定2秒待ち、前の所有プロセスが終了してから次を起動します。新しいウィンドウは表示しません。

結果は `TestResults/obs-clean/runs/<日時>-<Label>-<GUID>/` に保存します。`-ResultsRoot` で親ディレクトリを指定できます。既存結果は上書きせず、各 `run-NNN` に送受信のJSONL、stdout/stderr、開始時manifest、終了時resultを残します。結果にはUTC開始終了時刻、所有PID、終了コード、watchdogの判定、元のsummary、Warning/Error件数、受信サンプル混在数を保存します。バッチのmanifestと結果に加え、送信exe・プローブDLL・製品DLL・SpoutDX.dll、指定時には受信exeのSHA256を記録し、反復間と最終終了時のバイナリ変更を拒否します。

送信の非zero終了、無効化、未完了、破棄失敗、summary欠損・不整合、Error/Fatal、受信の非zero終了・summary欠損・記録エラー、watchdog超過で最初の該当試験後に停止し、終了コード1を返します。全試験の正常終了は0です。正常な遅い送信にもWarningが出るため、Warningだけでは止めず原文と件数を保持します。予定スロット欠落も記録して続行します。受信混在は既定では記録して続行し、停止させたい場合は `-StopOnReceiverMismatch` を指定します。受信画素判定には `sampleBgrHex` を出力するPixels版が必要で、81点のBGRがすべて64・128・192のいずれか同じ値であることだけを確認します。正常終了、性能、画像整合、同期精度は別々に評価してください。

既知の送信プロセス（製品アプリ、SpoutTransportProbe、PureSpoutSender、指定送信exeと同名）および指定受信exeと同名の既存プロセスを検出した場合は開始を拒否します。追加の競合プロセス名は `-AdditionalConflictProcessNames` に指定できます。他の名前のSpout送信元や実際の送信名の接尾辞はこのチェックでは検出できません。強制終了の対象はこのランナー自身が起動したProcessオブジェクトだけです。

OBSの起動・終了・スクリーンショット保存は外側で管理してください。「sender-only」はこのランナーが受信プローブを起動しないという意味で、OBSなどの受信アプリが動いていない保証ではありません。既存OBSや他アプリを自動停止しません。送信のみ／独立受信／標準OBS／OBS画像保存ありの比較では、条件を外側で切り替え、バッチを一つずつ終了させてください。GPU・音声・OBSの別負荷試験と同時実行しないでください。
