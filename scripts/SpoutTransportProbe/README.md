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

出力ファイルのパスは必須です。既存ファイルへの上書きは拒否します。出力先ディレクトリは必要に応じて作成します。製品のビルド設定から `SpoutDX.dll` が実行ファイルの隣にコピーされていることを確認してください。

終了コード0は、初期化、30秒の計測、送信元の破棄、結果ファイルの書き出しまで完了したことを示します。引数・出力先の準備エラーは2、実行・終了処理・書き出しの失敗は1です。強制終了などで `summary` 行が残らなかった計測は未完了として扱ってください。

## 記録と判定範囲

JSONL は `start`、スロットごとの `send`、最後の `summary` から成ります。送信呼び出し時間、予定時刻からの遅れ、予定スロット欠落数、プロセスCPU時間、GC回数を記録します。CPU使用率は1コア相当を100%とし、待機処理のスピンも含みます。計測ループ内ではファイル・コンソールへの書き込みやログ出力先の設定を行いません。

製品の Warning 以上のログはスレッドセーフなメモリ内キューに保持し、計測終了後に `summary.diagnosticLogs` へ時刻・レベル・メッセージ・例外詳細を出力します。初期化、送信、破棄で製品側が捕捉した例外も確認できます。通常の Information/Debug ログは記録せず、計測中にログの文字列化やディスク・コンソール書き込みは行いません。

`attemptedFrames` は送信APIの呼び出し回数です。終了コード0でも予定スロット欠落はあり得るため、`missedScheduledSlots` と `sendOverBudget` を確認してください。`SendFrame` は戻り値を返さず、受信結果も取得しないため、`transportMisses` は `null` です。送信側の定量結果だけでは、受信の成功、全画素の一致、全フレームの到達を保証できません。3色の繰り返しだけでは、受信側で任意のフレーム欠落を一意に判定することもできません。

要求する送信名は `TimecodeSyncPlayer` ですが、同名の送信元が複数あるとSpout側で接尾辞が付く場合があります。製品アプリなど他の送信元を停止するか、受信側でこのプローブに対応する実際の送信名を確認してください。標準出力や要求名だけを見てOBSがこのプローブを受信したと判定しないでください。

OBSによる受信、録画、画素・フレームの解析は別途必要です。OBSの private-copy 対策は本番導入済みとして扱いません。このプローブはOBSのプラグインや受信処理を変更しません。
