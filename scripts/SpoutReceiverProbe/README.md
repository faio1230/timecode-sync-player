# Spout受信mutex比較プローブ

独立受信器の `ReceiveTexture()` が共有画像からprivate画像へのGPUコピー完了前にmutexを解放する経路を調べる診断用です。OBS pluginや製品機能ではありません。既存のignored receiver source・EXEは変更しません。

`baseline` と `locked` は同じ新バイナリで比較します。両方とも接続中の各pollで10行をstagingへコピーし、同期Map後に81点と1080pマーカーを読むため、旧プローブのフレーム選別・Release最適化とは測定負荷が異なります。旧EXEの結果とは区別してください。自動adapter切替も無効に固定します。

`locked` は指定した実送信名の `<name>_SpoutAccessMutex` を取得し、`ReceiveTexture()`、10行コピー、同期Map、Unmapまで保持します。Map成功は同じcontext上の先行shared→private→stagingコピー完了を確認する根拠です。ログは正常unlock後に出力します。metadata・送信名の前後不一致、接続後の消失、mutex timeout/abandoned、native失敗は観測成功として扱いません。接続前の指定送信名不在は `observation.accepted=false` で記録して期限まで待ちます。

SDKは内部mutex取得失敗や新規フレームなしでもReceiveTexture成功を返すため、成功値や初期値trueのIsFrameNewだけをコピーの証明には使いません。診断ビルドは引数なし `ReceiveTexture()` のshared→private CopyResource直後へ、同一受信スレッドのカウンターだけを増やすhookを1か所挿入します。Map成功に加えてコピー累計が1以上になってから `frame.accepted=true` とします。それ以前はraw画素とQPCを `observation.accepted=false` に残します。各frameの `copySubmitCount` は累計、`copySubmitsThisPoll` は今回のコピー回数です。差分0の標本は既にコピーされたprivate画像の再観測であり、新規送信フレームではありません。hookはI/O・atomic・追加待機を行わず、baseline/lockedの両方へ同じように適用します。

コピー成立は送信元の初回画像更新完了を保証しません。送信元が共有画像を登録してから最初に画像を更新するまでの標本は、送信側の最初の成功SendEndQpcと照合してstartupとして別集計してください。原始標本は削除せず、後続の混在反例とは区別します。

**外部のプロセスwatchdogが必須です。** mutex取得期限は100msですが、既存D3D11のblocking Mapとnative解放の所要時間に上限はありません。新しい無期限pollは追加していません。GPU完了を確認できない失敗時は正常unlockせず、エラーを記録して終了します。main thread終了に伴うabandoned mutexを使う診断上の失敗処理であり、異常GPU処理のキャンセルやプロセス終了までの安全性を保証するものではありません。所有プロセスをjobに入れた外部watchdogで終了を管理してください。

## ビルド

Windows PowerShell、MSVC、Debug x64を使用します。Spout vendor sourceを明示します。sourceは静的リンクされ、SpoutDX.dllはロードしません。

```powershell
& .\scripts\SpoutReceiverProbe\build.ps1 -VendorPath .\.superpowers\sdd\2026-09-08-spout-decode-compare\receiver\vendor
```

出力: `scripts/SpoutReceiverProbe/bin/Debug/x64/SpoutReceiverProbe.exe`。vendorは元のcopyright・BSD形式の条件を保持したまま使用し、全vendor source/headerのcopyright・license blockを `THIRD-PARTY-NOTICES.txt` としてバイナリに添付します。vendor source/headerとprobe sourceのSHA256を `build-inputs.json` に記録します。別vendor版を使った結果は別条件です。

元vendorファイルは変更せず、出力ディレクトリの `SpoutDX.probe.cpp` だけにhookを入れます。対象関数の定義・境界とコピー呼び出しの一致数がそれぞれ厳密に1でなければビルドを拒否します。生成ソースも元source/headerとともにハッシュへ記録します。hook導入前のバイナリと結果は別版として保持してください。

## 実行

以下はwatchdogが所有プロセスとして起動する際の引数例です。`--external-watchdog` は呼出側がwatchdogを設置したことの宣言で、プローブがwatchdogを作成する指定ではありません。

```text
SpoutReceiverProbe.exe --sender TimecodeSyncPlayer --output NEW_PATH.jsonl --mode locked --external-watchdog --duration 50 --poll-ms 8 --stop-file OWNED_STOP_PATH
```

`--mode baseline` では外側mutexを追加しません。raw JSONLの `frame.accepted`、`mutexMs`、`receiveMs`、`copyAndMapMs` と各QPCを送信記録に合わせます。`summary.errors=0` でも画像整合や60fpsを保証しません。終了0はサンプルありの正常終了、2はサンプルなし、1は失敗です。失敗時は `error.accepted=false` と失敗stageを記録し、正常summaryは出しません。`--self-test` はGPUを作成しないマーカー解析検証です。

正常終了時は `cleanup-stage-start` をflushしてからnative解放を行い、receiverのdestructorまで戻った後に `cleanup-stage-end` を記録します。nativeStartQpc／nativeEndQpcと開始checkpointのflush時間を分け、終了ログのflush時間も `cleanup-log-flush` に残します。このI/Oは終了処理時だけです。正常summaryはdestructor完了後に出します。例外経路は別で、catchへ到達する前のローカルCOM資源解放もあるため、エラーログの存在や不在だけで安全解放・終了完了を保証しません。
