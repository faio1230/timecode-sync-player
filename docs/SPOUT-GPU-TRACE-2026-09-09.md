# 再起動後のGPU記録採取（2026-09-09）

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

今回の採取ではSpout無効化が再現していない。以前の単発失敗、実動画でのGetData未完了100ms、timeout後の安全な回収、標準OBSの画像混在は引き続き未解決である。
