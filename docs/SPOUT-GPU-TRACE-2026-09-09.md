# 再起動後のGPU記録採取（2026-09-09）

最新到達点：NonPagedMemoryの追加後にGPU schedulerイベントを収録でき、GetData未完了によるSpout無効化と同じETLを保存した。失敗の根本原因は未特定。以下は各試験時点の判断を時系列で保持する。

`refactor/session-lifecycle` の `617f038` から再開。既存の未追跡AGENTS.md、製品コード・DLL、通常OBS、元の素材・プロジェクトを保持した。証跡はGit管理外の `TestResults/obs-clean/restart-investigation-20260909-021138/`。

## 再起動前の停止記録

ユーザー申告はPC全体の停止と手動再起動で、UAC画面は表示される前だった。Windows Error Reportingには01:38:45からLiveKernelEvent `141`、続いて`1a8`／`1b8`／`193`がある。前回の昇格要求は01:42:10で、この要求より前に停止記録が存在する。前回の採取先にはlaunch-requestだけがあり、昇格Process取得・WPR開始・採取結果は確認できない。

0x141はdisplay engineが期限内に応答しなかったlive dump分類で、コードだけではアプリ・ドライバー・ハードウェアの根本原因を特定できない。193はdxgkrnlによるlive dump、1a8／1b8は黒画面の診断dumpであり、同じdumpの再報告も含まれる。報告件数を独立した故障数として数えない。WindowsのTDRと製品のGetData完了待ち100msは別で、今回の停止とSpout無効化の同一原因は未立証である。[0x141](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x141---video-engine-timeout-detected)、[0x193](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump)、[0x1A8](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x1a8--video-dxgkrnl-black-screen-livedump)、[0x1B8](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x1b8--video-miniport-black-screen-livedump)、[TDR](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery)

01:54にshutdown.exeによる再起動要求とEventLog停止があり、現在のWindows起動時刻は01:55:13。復旧確認時に対象試験プロセスはなく、WPRは記録中ではなかった。限定時間帯のイベント原文・XMLを `live-kernel-events.json`／`system-events.json` に保存した。

## 昇格と1回目の実採取

ユーザーの許可に従いWindows UACを要求し、管理者tokenでpreflight成功を確認した。その後、送信のみの固定4K BGRA画像プローブを30秒、1回だけ実行した。OBS、独立受信、LTC／音声、録画・配信は併走していない。採取は既存 `Capture-SpoutGpuTrace.ps1` と `SpoutGpu.wprp` を使用し、製品の待機期限・`hwdec=no`・送信処理を変更していない。

原始runは `elevated-resume/sender-only/`。

| 判定対象 | 結果 |
| --- | --- |
| WPR start／stop | 成功、固有instanceだけを保存終了 |
| ETL | `gpu.etl`、143,654,912 bytes |
| 送信 | 1,800回／30秒、予定欠落0 |
| 最大SendMs | 13.3044ms |
| Spout有効状態 | 全送信の前後で有効・利用可能 |
| 診断Warning／Error | 0 |
| 終了 | completed／disposed=true、exit0、watchdogなし |
| 受信画像・同期精度 | 未測定 |

送信PID23952、QPC周波数10,000,000、計測開始QPC12,011,423,650、終了12,311,423,664。`sender-audit.json` に全送信のSlot／FrameIndex・時間会計・状態・入力ハッシュ不変・ETLと原始JSONLのSHA256を保存した。製品DLLは前回と同じ `08CC7D05DC2DB762035BB6879C6F3077393E35C57981ED02B62024F232C5C2F9`。

`wpr-start.txt` は存在しない。開始コマンドの出力が空ならPowerShell pipelineからファイルが生成されないことと整合するが、元出力は復元していない。開始成功の判定は別のresultとETLに基づく。終了時の `wpr-stop.txt` は保存成功、resultはtraceSaved=true／wprStopExit=0。終了後の対象プロセスは0、WPRは停止、確認時間帯の新たなLiveKernelEvent／Display 4101記録は0だった。

## 既存ダンプの保全範囲

管理者権限で `C:\WINDOWS\LiveKernelReports\WATCHDOG-20260909-0138.dmp` の存在と23,529,706,530 bytesを確認した。今回のコピー上限256MiBを超えるため、コピー・全体ハッシュ採取は行わず元の位置を変更していない。OSによる今後の自動削除まで防いだものではない。他4候補パスは管理者権限でも見つからなかった。詳細は `elevated-resume/dump-preservation.json`。

既存のWinDbg／KD等は確認した範囲で見つからず、インストール・dump解析は未実施。ダンプの存在確認、ETL採取成功、原因特定は別の判定とする。

## ETL監査で判明した採取設定の不足

独立担当が既存tracerptとGet-WinEventで解析した。137 buffers、1,619,828 events、tracerpt報告のEventsLost=0で、CPU Thread／ReadyThread／StackWalk、Direct3D11（1,020件）、DXGI（2,813件）が存在した。一方、DxgKrnlの正式GUIDはsummaryとETLへの直接XPath照会の両方で0件だった。**この最初のETLからGPUスケジューラの原因を調べられる状態には達していない。** 原文と監査は `elevated-resume/sender-only/etl-audit/` にあり、元ETLの前後SHA256は一致した。

解析にはschema mismatch警告がある。記録時間はtracerptの報告値31秒、最初の列挙イベントはUTC `2026-09-08T17:15:13.8369224Z`。正確な終了UTC・ETWのraw QPC対応・BuffersLostは未取得である。EventsLost=0だけでメモリリングの全期間保持や意味解釈の完全性を保証しない。[ETW記録統計](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties)

ローカルの `logman query providers Microsoft-Windows-DxgKrnl` により、GPUSchedulerは`0x8000`、HardwareSchedulingLogは`0x4000000`と確認した。旧mask `0x277`には含まれていなかったため、`SpoutGpu.wprp`のmaskを`0x04008277`へ最小修正した。バッファ量・製品timeoutは変えていない。修正版はWPRのprofiledetailsで受理され、必要keywordの指定を確認した。profileのSHA256は `F060E6029683A0C32496218ED17F4B7B694B728E797B81FA9924C54E0994D156`。

修正版での再採取をWindows UACへ要求したが、Start-Processは「この操作はユーザーによって取り消されました」を返した。`scheduler-v2-launch-error.txt` に原文を保持し、再採取は開始していない。再申請を反復せず、修正版での採取・GPU schedulerイベント収録確認を次の再開点とする。元のETLを修正版で採った結果として扱わない。

## UAC再申請後の修正版採取

ユーザーから再申請の依頼があり、UAC承認後にmask `0x04008277`で送信のみ30秒を1回実行した。原始runは `TestResults/obs-clean/gpu-scheduler-20260909-022454-8921cfcc/sender-only/`。WPR開始・保存終了とsenderはexit0、completed／disposed=true、Spout無効化は0。送信1,798回、予定欠落2（slot191／192）、最大SendMs59.0705ms、slow-send Warning3件で、性能合格とはしない。全送信前後でSpout有効・利用可能、製品DLLと採取入力ハッシュは不変だった。

`sender-accounting-audit.json` は実送信数・予定欠落・連番・QPCの会計整合を確認した。最初の `sender-audit.json` は前回と同じ「1,800回・欠落0・Warning0」の厳しい条件を使用しfalseだったため、その結果も保持し、正常終了や会計整合と区別する。ETLは143,654,912 bytes、SHA256 `05BDE51ADFB93D4D4BF57A4685E170FB7D1E9F6442FB6A099D9C2C5B110C89B4`。試験後は対象プロセス0、WPR停止を確認した。

独立監査のtracerptは137 buffers／1,563,214 events／EventsLost=0／報告時間31秒で、schema mismatch警告が残った。DxgKrnl正式GUIDはsummaryと直接XPath照会の両方で0件。**keyword追加後もGPUスケジューラの実イベント収録は確認できず、採取設定の問題は未解決。** 原始結果をGPU原因解析可能な記録として扱わない。

さらにローカルWPR組込みGPU profileを起動せずexportし、`sender-only/etl-audit/builtin-gpu.wprp`へ保存した。組込みprofileのDxgKrnl keywordも旧設定と同じ`0x277`だった。したがって、前節のkeyword不足を基本GPU記録が得られなかった原因とみなすことはできない。0x8000等の指定がなかった事実と、未収録原因の証明を区別し、原因を特定したという解釈を訂正する。

組込み設定はDxgKrnlをGUIDで指定し、`NonPagedMemory=true`、`Stack=true`、`Strict=true`、開始時のCaptureStateを使用している。現profileとはこれらが異なる。Microsoftもkernel-mode providerの採取にNonPagedMemoryを指定する手順を示しているため、次はこのメモリ設定を含む差を確認する。ただし今回それらの変更・再採取は実施しておらず、未収録の確定原因とは扱わない。[Microsoftのkernel-mode採取手順](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/capture-and-view-tracelogging-data)

## NonPagedMemoryの追加と失敗時ETLの取得

`3644823`を起点に、Dxg providerの`NonPagedMemory="true"`だけを追加した。keyword `0x04008277`、Level、provider名、stack、CaptureState、バッファ数・サイズは同時変更していない。Microsoftの仕様ではpaged memoryのsessionへkernel-mode providerは記録できず、WPRのNonPagedMemory既定値はfalseである。効果はsessionのバッファメモリ種別に及ぶため、Dxgイベント1件だけの割当設定ではない。Graphicsの要求約128MiBをnonpagedにする設定で、両collector合計の要求約256MiBは維持した。実割当・metadata等まで含む厳密な使用量上限とはしない。[ETW logging mode](https://learn.microsoft.com/en-us/windows/win32/etw/logging-mode-constants)、[WPR EventProvider](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/eventprovider)

原始証跡rootは `TestResults/obs-clean/gpu-nonpaged-20260909-023342-fc4021a6/`。変更前後のprofileを別ファイルへ保存し、変更後SHA256は `345DAC4DAC9A8DCAFC442EDF24E40E1FA687D37B2250E296A12D918D0E9E25F6`。非昇格preflightでprofile解釈成功、ユーザーの既存許可に従いUAC昇格し、同じ送信のみの固定4K60・予定30秒試験を1回実行した。既存のユーザーPowerShell PID22708は操作せず、OBS・音声等の別負荷試験は併走していない。

### 製品の失敗と記録の保存を分けた判定

今回の計測wallSecondsは14.4260727秒で終了し、748回目の呼出しでSpout無効化を検出した。この所要時間は失敗時の診断保存・後処理も含み、無効化の発生時刻を表さない。製品logのcount747はそれ以前の成功数で、プローブFrameIndex747と一致する。748回を成功送信数としない。

| 項目 | 結果 |
| --- | --- |
| プローブ | exit1、completed=false、disposed=true |
| 予定欠落 | 最後の試行slot755までに8件 |
| GetData | S_FALSE／completed=0、poll9回、gpuElapsed102ms |
| 処理区間 | mutex0.020ms、SendImage38.704ms、poll103.281ms |
| transfer終了判定まで | 142.354ms |
| deviceRemovedReason | S_OK。GPU完了の証明ではない |
| 外側SendFrame | 1,791.2305ms。診断保存・後処理を含む |
| 診断 | slow-send Warning3件＋失敗Warning1件 |
| WPR | 保存成功、stop exit0、watchdog・cleanup errorなし |

`sender-only/sender.jsonl.failure.json` はnative cleanup前の例外を保持する。checkpointはUTC `2026-09-08T17:34:48.1539449Z`、QPC23,748,575,044。プローブの失敗呼出し開始QPCは23,746,068,250、戻りは23,763,980,555。開始からcheckpointまで250.6794ms、checkpointから戻りまで1,540.5511msある。後半にはJSON化・ディスクへのflushとnative cleanupが含まれ、内訳の時間は測定していない。外側の1.79秒をGPU待機時間と断定しない。

142.354msはSpoutFrameTransferの内部進入から失敗判定までの時間で、外側SendFrame進入との差、判定後のDeviceRemovedReason・例外整形・loggingの時間は未分離。相対所要時間だけから絶対的なGetData開始終了時刻を決めない。失敗後の残りの予定を観測済みの欠落へ水増しせず、`failure-audit.json`で試行済みslot・成功数・失敗1件・時刻会計・入力SHAの整合を確認した。

WPRの保存は約114秒を要したが完了した。`sender-only/result.json`はtraceSaved=true／senderExit1を別々に記録し、ETLは171,966,464 bytes。採取後の試験プロセス0・WPR停止、既存PowerShellの存続を確認した。この試験の確認時間帯に新しいLiveKernelEvent／GPU関連System警告は見つからなかった。これは過去のPC停止との同一原因や因果関係を否定する証明ではない。

### GPU収録と時計の監査

属性追加後、初めてDxgKrnlの実イベントを確認した。ID20 UpdateContextStatusには`0x4000000000008000`、ID432 SchedulingLogには`0x4000000004000000`があり、追加したscheduler keywordに対応する。CPU・D3D11・DXGIも記録された。これは今回の設定で収録できた証拠であり、Spout無効化の原因をNonPagedMemoryやGPU採取負荷だと決めるものではない。

tracerptは164 buffers／1,970,430 events／EventsLost=0／報告時間17秒。schema mismatch警告は残り、全payloadの意味解釈を保証しない。別担当がOpenTraceだけで取得したheaderもEventsLost=0／BuffersLost=0、QPC周波数10,000,000、記録範囲UTC17:34:34.8041313〜17:34:51.8891356を報告した。メモリリングの全期間保持はloss値だけでは証明できない。

headerのBootTimeからQPCを単純換算するとcheckpointの壁時計から203.5595msずれる反例があり、この換算は使用しない。代わりに先頭1buffer・3イベントだけをraw timestampで読み、同じGUID／ID／version／opcode／PID／TID／48bytes payloadのイベントをGet-WinEventのUTC表示と一意に対応付けた。対応はQPC23,615,076,894 ↔ UTC17:34:34.8041313。この原点でcheckpointを換算すると17:34:48.1539463となり、checkpoint自身のUTCとの差は+1.4µsだった。単一原点の線形対応を、長時間の壁時計補正等まで保証するものとはしない。

先頭読みはBufferCallbackで1buffer後に中断し、ProcessTraceのERROR_CANCELLEDとCloseTrace成功を確認した。実装・SDK構造体照合・clock原点・限界は `sender-only/header-audit/`、providerと失敗窓の原文は `sender-only/etl-audit/` に保持する。

今回、失敗とGPUイベントが同じETLに揃うところまで進んだ。ただし過去の失敗との同一原因、timeout後の安全な回収、標準OBSの画像混在は引き続き未解決である。この採取時点では製品コード・DLL・100ms期限・hwdec既定は変更していない。

### 送信スレッド識別の追加

失敗窓のGPU／CPUイベントを限定して調べたが、header PIDが送信プロセスと一致するだけではGetDataを呼んだスレッドを特定できなかった。GPU submit側のTID27312と初期化側の候補TID29996を、そのままSendFrameの実行スレッドとは扱わない。候補29996の最初の時間集計にはPowerShellのDateTime文字列再変換による誤りがあり、原始結果を保持して棄却した。修正版`cpu-candidate29996-state-summary-v2.json`は窓内の時間会計を検証したが、候補の集計から製品のCPU待ちを確定することはできない。

次回の照合に備え、送信プローブへ`start.nativeThreadId`と失敗checkpointの`nativeThreadId`を追加した。前者は計測前に取得した同期SendFrame呼出し元、後者はログEmit時点のWindows OSスレッドIDである。各送信ループには取得処理を追加していない。製品の送信・期限・デコード設定は変更していない。

Debugビルドは警告0・エラー0。独立担当がプローブのMainとGPUを起動せずreflectionで6項目を検証し、別スレッドからの失敗ログのID、PID／QPC、元の例外、重複checkpoint抑止、slow Warningではcheckpointファイルを作らないこと、保存失敗の非伝播を確認した。Warningのメモリ保持・summary出力は維持している。証跡は同じrootの`probe-thread-review/`。startフィールドの実送信との照合は、次の実採取で別途判定する。

ビルド後の製品ソース差分はないが、製品DLLのSHA256は`08CC7D05…C5C2F9`から`E910DF35C0A20F56B1E91CB85EADE5DB99EAD3C7D9EFBA1FA01CA74B46CF907B`へ変わった。生成されるinformational versionのGit revisionが`2b649403`から`3644823`へ変わったことを確認しており、以前のDLLとbyte同一とはしない。`product-build-versions.json`と`post-diagnostics-build.json`に記録した。新プローブDLLのSHA256は`9E6DDF78705F483BF8088E2968EC693CF27AABE18F0CC3AF3CBDB9496CB7876F`。

### ID追加後の実採取要求（保留）

03:01:20 JSTに、同じprofileで送信のみ4K60・予定30秒を1回採取するWindows UACを要求した。要求と入力snapshotは`TestResults/obs-clean/gpu-threadid-20260909-030120-86a0c9bc/`に保存している。03:07 JSTの確認時点ではconsentプロセスが存在し、昇格Processの取得・sender起動・WPR開始は確認できず、承認待ちである。この要求を実施済みの試験として数えない。次は承認後の実記録を保存し、start／失敗checkpointのOS TIDをCPUイベントと照合する。
