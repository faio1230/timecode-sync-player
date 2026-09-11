# GPU Output Probe

本体から独立した .NET 8 / WPF / x64 / Debug の GPU 出力実証アプリです。設計の基準は `docs/GPU-OUTPUT-PROBE-SPEC.md`。動画デコード、プロジェクト設定、本体ログには触れません。

## ビルドと CPU のみの確認

```powershell
dotnet build scripts/GpuOutputProbe/GpuOutputProbe.csproj --configuration Debug
scripts/GpuOutputProbe/bin/Debug/net8.0-windows/GpuOutputProbe.exe --self-test
scripts/GpuOutputProbe/bin/Debug/net8.0-windows/GpuOutputProbe.exe --validate-shaders
```

`--self-test` はスケジューラー・最新画像プール・GPU 使用権を模した管理状態・記録の検証だけを行います。`--validate-shaders` は D3DCompiler で HLSL をコンパイルしますが、D3D デバイスを作成しません。これらは GPU 実動作の確認にはなりません。

## 実機実行（親エージェントが直列実施）

```powershell
scripts/GpuOutputProbe/bin/Debug/net8.0-windows/GpuOutputProbe.exe --list-displays
scripts/GpuOutputProbe/bin/Debug/net8.0-windows/GpuOutputProbe.exe --mode common --output both --width 1920 --height 1080 --fps 60 --seconds 8 --warmup 1 --monitor-index 0 --windowed --mutex-wait-ms 0 --sender GpuProbe-UniqueName --log-dir TestResults/GpuProbe-UniqueDirectory
```

指定ログディレクトリは新規である必要があります。既存パスへの上書きは拒否します。通常設定を保存しません。指定秒数が過ぎると GPU・ネイティブ処理完了を待って解放・記録し、終了します。途中で終了操作をすると、継続・通常終了・強制終了の選択を表示します。強制終了はこのプロセスだけを即時終了するため、記録の完了は保証されません。

Spout の受信は公式 WinSpoutDXreceiver を使用してください。アプリには受信アプリの起動・終了機能がありません。WPR、UAC、GPU リセットの操作もしません。スクリプトによる実行・解析は隣の `GpuOutputProbeHarness` を参照してください。

## 実装した経路

- GPU が HLSL で色帯・外枠・十字・動くマーカー・24 bit の画像番号を作ります。画像番号は下部の白黒セルに下位 bit から左→右へ表現します。表示画像の CPU 読み戻しはありません。
- `common`: GPU ワーカー1本が合成、全画面への描画、Spout 用保持画像へのコピー、Spout 送信を順に行います。
- `split`: 合成と全画面は GPU ワーカー、Spout は専用ワーカー。同じアダプター LUID の別 D3D デバイスを使用し、3枚の共有テクスチャを一度開いて保持します。
- 両構成とも Spout は専用の保持画像1枚へ GPU コピーします。画像番号が同じ再送ではコピーを省略します。コピーの EVENT query 完了後に共有画像の使用権を返し、Spout の送信待ちから分離します。
- 最新画像をキューに蓄積しません。プールの未使用領域で次を合成し、完成後に最新を置換します。未処理の旧画像はスキップします。
- shared keyed mutex は key 0 を使用し、CPU の画像使用権と組み合わせます。WAIT_TIMEOUT をスキップ、WAIT_ABANDONED を異常として区別します。Vortice の void ラッパーでは正の HRESULT が失われるため、この2メソッドは COM slot 8/9 を直接呼びます。
- 全画面は flip-discard / latency waitable /最大 latency 1 / `Present(1)` を使用します。ティアリングは許可しません。表示前に GPU の元画像使用完了を確認し、元画像の mutex と使用権を返します。
- 全画面でも操作ウィンドウはアクセスできるよう最前面に残るため、映像を一部覆う可能性があります。`manifest.operatorOverlayPossible` と実表示寸法を記録します。物理的に遮蔽なしの4K表示を証明する試験ではありません。

## 寿命と異常

GPU EVENT query を End/Flush/GetData で確認します。100 ms 以上未完了の場合、新しい仕事を停止して異常を記録し、完了またはデバイス無効の確定まで所有スレッドで待ちます。期限超過だけで使用中資源を解放しません。UI は待機中も応答し、強制終了を選択できます。

Spout への `SendTexture` は `<実登録名>_SpoutAccessMutex` を外側で取得し、送信後 GPU 完了まで保持します。SDK 自身の再帰取得と組み合わせるため、API が true を返しただけの結果と区別できます。送信 API の返却、GPU 完了、外側 mutex の解放を別イベントにします。

### 外側 mutex の短時間待ち実証

`--mutex-wait-ms` は整数 0〜8、既定0です。設定値はOSへの要求時間であり、実時間の上限保証ではありません。実際の `WaitOne` 指定時間は「設定値」と「次の予定時刻までの残りを整数ミリ秒に切り捨てた値」の小さい方で、一度だけ待ちます。例えば8ms設定でも残り7.999msなら7ms、残り1ms未満なら0msになります。再試行・スピン・追いつき送信は行いません。次の予定時刻はスケジューラー自身の値を渡すため、59.94 Hz等でも丸めた周期の加算で近似しません。

取得前にキャンセルまたは期限超過なら待ちません。取得後も同じ期限とキャンセルを確認し、送信前に無効になった仕事は mutex を返してスキップします。この場合、GPU に仕事を出していないので EVENT query も追加しません。OS の待ちが指定ミリ秒を超えても、次の予定時刻より前なら送信できます。指定待ち時間は実時間で必ず守られる保証ではなく、実際の時間を記録して評価します。

8msまでの拡張の評価は、1080p／8msのスモーク後、同じ新バイナリ・split・送信位相4ms・公式受信機の条件で、4Kを4→8→8→4msの順に直列比較する予定です。4msの旧条件も同じ期限・所有権の仕組みで実行します。8msより大きい値は拒否します。

### 送信開始位相の実証

`--send-phase-ms` は double、既定0です。有限値で `0 <= phase < 1000 / fps` を満たす必要があります。0以外は `--mode split` かつSpoutを含む出力だけで使用できます。位相比較では、mutex要求4ms固定で送信位相0／1／2／4msを切り替える条件を使用します。

送信ワーカーのスケジューラー起点だけを `commonOriginQpc + Math.Round(phaseMs * QpcFrequency / 1000)` に変更します。丸めは .NET の `Math.Round` 既定（中間値を偶数へ丸める）です。GPUワーカーの起点、両ワーカーの終了時刻 `commonOriginQpc + Seconds * QpcFrequency`、最新画像取得、コピー、Spout送信、GPU資源所有権は維持します。mutex取得の期限には、位相をずらしたスケジューラー自身の真の次回予定時刻を渡します。CPUの次回待機が終了時刻を越える場合は終了時刻で起こしますが、既に実行中のGPU／ネイティブ処理の完了待ちは継続します。

位相は強制的な画像遅延ではなく、送信ワーカーが最新画像を選ぶ予定時刻の差です。遅れた回は従来どおり飛ばし、過去画像を待ち行列に積みません。設定値は `manifest.options.sendPhaseMs` に記録します。

### 全画面の準備待ち実証

`--present-wait-ms` は整数0〜1、既定0です。0以外は全画面出力を含む場合だけ許可します。split・送信位相4ms・mutex要求8msを固定し、同じ新バイナリで全画面の準備待ち要求0msと1msを比較します。バッファ2枚、最大フレームlatency1、`Present(1)`、GPU完了確認は変更しません。

全画面ワーカーは最新画像の使用権とsourceのkeyed mutexを取得してから、swapchainのlatencyイベントを一度だけ待ちます。要求時間は設定値と「次の予定時刻／共通終了時刻の早い方までの残りを整数msに切り捨てた値」の小さい方です。待ち成功後、描画前や描画完了後に期限超過・キャンセルとなった場合、Presentを見送れます。発行済みGPU処理は完了確認してから元画像の使用権を返します。

待ち成功による表示の許可はDisplayTarget側に保持し、実際のPresent試行でだけ消費します。その回を見送っても成功signalを失わず、次回は新しい最新画像を取り直して、native waitを繰り返さずに保持済み許可を使います。保持するのは画像ではなく表示の許可です。終了時は許可を破棄します。OSの待ち時間は要求値を超える場合があり、実時間の上限保証ではありません。

### 同一プロセス内の要求切替

`--present-wait-plan fixed|abba|baab`（既定`fixed`）で全画面の待ち要求を区間切替できます。`fixed` は従来の `--present-wait-ms` を全期間使用します。`abba` は0→1→1→0ms、`baab` は1→0→0→1msで、指定Seconds全体を4等分します。非fixedでは全画面出力、`--present-wait-ms 0`、`Seconds/4 > 2*Warmup` が必要です。各区間の両端Warmup秒を解析から除きます。例えば48秒・Warmup2秒なら、12秒ずつの4区間で各8秒を解析します。

区間境界は `originQpc + (long)(i * (Seconds / 4) * QpcFrequency)`（i=0〜4、正値の整数変換で切捨て）です。適用区間は観測した現在時刻ではなく、その周期のscheduledQpcが属する `[startQpc,endQpc)` から選びます。遅れた周期は実際に選んだ区間だけを適用し、飛ばした区間を追いかけません。待ち要求だけを切り替え、worker・デバイス・swapchain・Spout送信機・表示許可・共通起点・周期スケジュールは作り直しません。

UIは切替planと要求の並びを表示します。非fixedの `--present-wait-ms 0` はplan指定の前提値であり、実行中の全区間が0msという意味ではありません。`summary.json` の既存全期間集計は異なる要求を含むため、効果判定にはハーネスで区間別の解析窓を指定した結果を使用してください。

### 次の合成まで表示準備通知を待つ実証

`--display-pacing tick|ready`（既定`tick`）を追加しています。`tick` は従来の合成直後の確認経路です。`ready` は `--mode split --output both --present-wait-ms 0 --present-wait-plan fixed` の組み合わせだけを許可します。比較条件はSpout位相4ms・mutex要求8msを維持します。

`ready` は合成tick直後から次回合成予定／共通終了時刻の早い方まで、停止handle（index0）とswapchain latency handle（index1）を `WaitForMultipleObjects` で一度だけ待ちます。停止通知を優先し、復帰後にも停止・期限を確認します。待機中は画像leaseもsourceのkeyed mutexも持ちません。成功して期限内なら最新画像を取り、その後は既存の描画・GPU完了確認・Present経路を使用します。Ready権を保持済みならnative waitは不要です。

各合成tickで通知待ちは最大1回、表示試行も最大1回です。source取得失敗時にも再試行しません。timeoutは残時間の整数ms切捨てです。早くtimeout復帰して端数msが残っても同tickで再待機せず、既存のidle待機へ戻ります。このため周期末尾の端数msに来た通知をその周期で処理できない可能性は、今回の測定限界です。期限後に成功通知を得た場合はready権だけを保持し、次回合成を優先してから最新画像を選び直します。

デバイス、コンテキスト、swapchain、3枚のsource、スレッド数、GPU完了後に画像を返す寿命管理は共通です。新しいtimerは追加しません。表示処理そのものが長引いた場合の合成への影響は残ります。wait中の停止handleはSafeHandle参照を保持し、swapchain handleも所有GPUワーカーの終了後まで閉じません。

通常終了は送信ワーカーの開いた共有資源を解放してから、合成側の共有資源を解放します。UI の映像 HWND はエンジン終了後に破棄します。描画／送信中に別スレッドから Dispose しません。初回性能実証では自動 GPU 復旧を行いません。

### 同一tick内のコピー再試行実証

`--copy-retry off|signal`（既定`off`）を追加しています。`signal` は `--mode split` かつSpoutを含む出力だけで使用できます。`off` は従来の1回取得経路のまま変更しません。

送信workerが `copy.keyedMutexBusy`（初回）で失敗した後、画像lease・pool使用権・sourceのkeyed mutexを一切保持しない状態で、GPUスレッドの表示側keyed mutex返却（`display.mutex.release` 直後のsignal）を最大 min(4ms, 次回送信予定までの残り整数ms) 待ちます。復帰後は停止・次回予定時刻を再確認し、期限内なら同じ出力枠で最新画像を再び選び直して1回だけ再取得します。2回目の失敗は `copy.keyedMutexBusy.retry` で、これ以上の再試行はしません。待ち開始前の予算0・停止は `copy.retry.deadline` / `copy.retry.cancelled` で見送ります。signalか予算満了のどちらで抜けても再試行は同じ1回です。

これはGPU返却の通知で起こされる機構であり、位相4ms固定でなくても成立させるためのものです。固定位相のずらしによる回避ではありません。

再試行の選択は同じworker・scheduledQpcで2組目の `send.select.start/end` になるため、両イベントの `value` に試行番号を記録します。初回は0（従来ログと同一）、再試行は1です。end の detail（`latest` / `retained` / `none`）は変えません。ログ上の順序は、初回の対 → `copy.keyedMutexBusy` skip → `copy.retry` → 再試行の対（`copy.retry` の deadlineQpc より前に開始）です。

### 共有フェンスによる読者同期の実証

`--source-sync keyed|fence`（既定`keyed`）を追加しています。設計は `docs/GPU-FENCE-VSYNC-PROBE-SPEC.md`。`keyed` は従来の keyed mutex 経路・記録のままです。`fence` は `--mode split` かつSpoutを含む出力だけで許可し、`--copy-retry signal` とは併用できません（再試行すべき keyed mutex が無いため）。

`fence` では source texture を `Shared | SharedNTHandle` で作り、keyed mutex を付けません。共有は `IDXGIResource1.CreateSharedHandle`（Read）の NT ハンドルで、送信デバイスは `ID3D11Device1.OpenSharedResource1` で開きます。合成デバイスで `ID3D11Device5.CreateFence(0, Shared)` を作り、`ID3D11Fence.CreateSharedHandle`（GENERIC_ALL）の NT ハンドルを送信デバイスが `ID3D11Device5.OpenSharedFence` で開きます。NT ハンドルは legacy ハンドルと異なり作成側が所有するため、送信ワーカーが texture とフェンスを開いた（または開けずに終了した）後に、合成側が自分のハンドルを `CloseHandle` します。開いた参照は送信ワーカーが終了時に texture → フェンス → デバイスの順で解放し、その後に合成側が source・フェンス・デバイスを解放します（join 順序は従来どおり）。デバイスが `ID3D11Device1`／`ID3D11Device5`／`ID3D11DeviceContext4` を提供しない場合は起動時に拒否し、manifest に `startupFailed=true` と `startupFailure`（理由）を記録します。

**フェンス値は画像IDそのもの**です（`ImageStamp.Id` を流用し、別フィールドは持ちません）。合成は描画命令の後に `ID3D11DeviceContext4.Signal(fence, imageId)` を出し、従来どおり EVENT query の CPU 完了確認 → `compose.publish` → pool 公開の順序を維持します。値の逆行は `FenceSequence` が GPU に出す前に拒否します。表示（同一デバイス）は keyed mutex の取得をせず、`display.fence.wait`（value=フェンス値）を1件だけ記録します。Spout コピー（別デバイス）は `CopyResource` の前に送信側 context4 で `Wait(fence, imageId)` を出し、`copy.fence.wait`（value=フェンス値、detail `kind:slot`）を記録します。CPU はブロックしません。`fence` では `display.mutex.*`／`copy.mutex.*`、`copy.keyedMutexBusy`、`present.keyedMutexBusy`、`compose.keyedMutexBusy` は発生しません。逆方向（読者→書き手）の危険は従来の CPU lease／pool 契約で防ぎます。`Signal`／`Wait` の失敗・デバイス消失は既存の Fault 経路です。

### 表示通知駆動の実証

`--display-pacing vsync` を `tick|ready` に追加しています。全画面出力を含む場合だけ許可し、`common`／`split` の両方で使えます。`--present-wait-ms 0` と `--present-wait-plan fixed` が必要です。合成 60Hz と Spout の周期・位相は変えません。

GPU ワーカーの合成 tick は従来の期限管理のままで、合成直後に表示を試みません。空き時間の待ち方だけを次のように変えます。「最後に表示した画像IDより新しい最新画像がある」ときだけ停止 handle（index0）と swapchain latency handle（index1）を `WaitForMultipleObjects` で次の合成期限まで（残り整数ms、1ms未満なら待たずに従来の yield）待ちます。新しい画像がなければ従来どおり停止 handle だけを待ち、立ったままの通知で busy loop しません。通知で復帰したら停止・合成期限を再確認し、期限内なら最新 lease を取り、ID が最後に表示した ID より大きければ描画 → GPU 完了確認 → source 返却 → `Present(1)`。同じ ID なら `display.vsync.noNewerImage` で見送り、通知（ready権）は消費しません。期限後・停止後に来た通知は `display.vsync.deadline`／`display.vsync.cancelled` を記録して権だけ保持し、合成を優先した後、次の枠で native 待ちなしに表示します。通知1回につき表示は最大1回、合成枠1つにつき native 待ちと表示試行はそれぞれ最大1回です。待機中に lease・keyed mutex・フェンス待ちは持ちません。

起動時は latency waitable object が signaled のため、最初の画像の後の最初の待ちは即時に復帰して表示します。以後は Present が通知を消費し、表示先の周期で再び signaled になります。決定ロジックは `VsyncDisplayGate` に分離し、`--self-test` でフェイク時計・フェイク待ちにより検証します。swapchain 設定（flip-discard、buffer 2、latency 1）は変えません。

### 走査時刻の記録（DXGI フレーム統計）

設計は `docs/GPU-SCANOUT-STATS-PROBE-SPEC.md`。オプション追加なし、表示方式・同期方式・周期は変えません。Present 成功直後に `IDXGISwapChain::GetLastPresentCount` を呼び、その値を `present.return` の `value` に入れ、`presentCount → (画像ID, generatedQpc, present.start qpc)` を GPU worker 内の表（直近16件、`ScanoutTracker`）に保持します。GPU ループの各反復（合成 tick の末尾、vsync の通知待ち復帰後・表示後）で `IDXGISwapChain::GetFrameStatistics` を1回呼びます。lease・keyed mutex・フェンス待ちを持ったままは呼びません。呼出しは swapchain 所有の GPU worker だけです。

統計の `PresentCount` が前回観測より進んでいれば `present.scanout` を1件記録します（同じ PresentCount は二重記録しません）。PresentCount が未観測の Present を飛び越えた場合は最新の1件だけを記録し、飛ばした Present は未観測のまま残ります。表に無い PresentCount（起動前・表の容量超過・S_OK 以外で終わった Present）は画像0で `detail` に `:unmapped` を付けます。`DXGI_ERROR_FRAME_STATISTICS_DISJOINT` は `present.stats.disjoint`（detail `disjoint`）を1回だけ記録して続行し、異常扱いしません。それ以外の失敗は既存の Fault 経路です。終了時に `summary.json` へ `scanoutPending`（記録したが観測されなかった Present 件数）と `scanoutDisjoint`（bool）を追加します。

制限: windowed では DWM が合成するため、統計は DWM がそのフレームを flip した vblank を表し、物理的な発光時刻ではありません。統計は Present から1 vblank 以上遅れて更新されるため、`present.scanout` の `qpc`（観測時刻）は走査時刻ではなく、走査時刻は `deadlineQpc`（`SyncQPCTime`）で読みます。`PresentRefreshCount` と `SyncRefreshCount` が異なる場合、`SyncQPCTime` はその Present の vblank そのものではありません。

### vblank 位相予測による表示の実証

`--display-pacing vblank` を `tick|ready|vsync` に追加しています。設計は `docs/GPU-VBLANK-PACING-PROBE-SPEC.md`。全画面出力を含む場合だけ許可し、`--present-wait-ms 0` と `--present-wait-plan fixed` が必要です。`--present-margin-ms`（double、既定 3.0、0.5〜8.0）は vblank でだけ既定以外を指定できます。合成 60Hz と Spout の周期・位相、swapchain 設定、pool 契約は変えません。

表示は `present.scanout` の観測（`SyncQPCTime`、`SyncRefreshCount`）を位相基準にします。refresh 周期は直近8件の連続差（refresh を飛んだ観測はその平均）の中央値で、初期値は表示先の `refreshHz` です。次の vblank は `lastSyncQpc + k × period`（`now + margin` を超える最小の k）、目標時刻は `予測 vblank − margin`。GPU ワーカーの空き時間に `min(目標, 次の合成期限)` まで停止 handle（index0）と waitable timer（index1）を `WaitForMultipleObjects` で待ちます。timer は `CreateWaitableTimerExW` の `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` を要求し、失敗時は通常タイマーにフォールバックして manifest の `highResolutionTimer` に記録します。合成期限が先なら合成を優先し、合成後に再判断します。ただし目標を過ぎて起床したとき、その予測 vblank が lead（1 ms、4K 実測の描画＋Present 時間 p99 ≈ 0.4 ms から定めた定数）以上先に残っていて、まだ表示していない新しい画像があれば、合成期限を過ぎていても先に Present し、合成はその直後に行います（合成の遅れは `compose.start` の遅れとして記録され、1 ms 未満を想定）。先に合成すると Present が合成直後の新しい画像を選び、目標時点の画像が一度も表示されず次の vblank が保持される（4K 実機で交互の飛び・重複）ためです。vblank まで lead 未満なら間に合わないので Present せず、予測を捨てて次の vblank へ再予測します。

目標時刻に達したら latency waitable を timeout 0 で確認し、未シグナルなら `display.vblank.notReady` を記録して見送り、その予測を捨てて次の vblank へ再予測します（同じ margin 内で再試行しません）。シグナル済みなら最新 lease を取り、ID が最後に表示した ID より大きければ描画 → GPU 完了確認 → source 返却 → `Present(1)`、同じ ID なら `display.vblank.noNewerImage`。予測付き試行の選択・描画・Present の期限は合成期限ではなく予測 vblank（`min(予測 vblank, 共通終了)`）で、目標が合成期限の直前でも `present.deadline` で見送りません（合成がわずかに遅れることは許容し、遅れは記録されます。bootstrap の期限は合成期限のまま）。予測 vblank 1回につき表示は最大1回で、重複判定は予測 vblank の時刻で行います（最後に表示した予測 vblank から半周期より後の予測だけ表示可能。統計の `SyncRefreshCount` の番号は予測ラベルと1ずれることがあるため refresh 番号では判定しません）。合成枠1つにつき表示試行は最大1回です。目標を過ぎて vblank も過ぎていたら次の vblank へ再予測し、過去分を貯めません。統計を得る前（起動直後）は `display.vblank.bootstrap` を記録して tick と同様に合成直後へ Present し、統計を起動します。待機中に lease・keyed mutex・フェンス待ちを持たず、停止を優先します。決定ロジックは `VblankDisplayGate`、timer は `VblankWaitTimer` に分離し、`--self-test` でフェイク時計・フェイク統計・フェイク待ちにより検証します（目標経過後の Present 優先と lead 内の再予測を含む）。

### 合成位相の vblank 整列の実証

`--compose-align off|vblank`（既定 `off`）と `--compose-lead-ms`（double、既定 1.5、0.5〜8.0）を追加しています。設計は `docs/GPU-COMPOSE-ALIGN-PROBE-SPEC.md`。`vblank` は `--display-pacing vblank` かつ全画面出力を含む場合だけ許可し、lead の既定以外は `vblank` でだけ指定できます。`off` は従来と同じ経路で、スケジューラーにオフセットを渡しません。

`vblank` では GPU worker と Spout worker の `TickSchedule` が同じ `ScheduleOffset`（Interlocked で読み書き）を起点に加えます。周期は変えず位相だけを動かします。GPU worker は各 `present.scanout` 観測後（統計を得る前の bootstrap 中は補正しません）に、次の合成予定時刻 D と望ましい時刻 W（現在時刻より後の最初の `予測 vblank − margin − lead`）の位相誤差 e = wrap(D − W, ±period/2) を求め、オフセットを `−clamp(e, ±slew)` だけ動かします。slew は 0.5 ms 固定（`ComposeAlignGate.SlewMs`、manifest の `alignSlewMs`）、観測1回につき補正は最大1回です。補正量は整数 µs に量子化してから tick に戻すため、解析器はイベントからオフセットを正確に再構成できます。表示目標は従来どおり `予測 vblank − margin` で、表示経路・tick／ready／vsync／fence は変わりません。

オフセットは `DueQpc` の読み取り時にサンプルし、`Take` は同じサンプルを使います。そのため待機中に GPU worker が起点を動かしても、期限に達した tick が「未到来」になることはなく、変更は次の `DueQpc` 読み取り（＝まだ取得していない tick）から効きます。取得済みの scheduledQpc は変えません。予定時刻は `Take` をまたいで必ず単調増加で、起点を過去へ大きく動かして次の index が直前の予定時刻以前になる場合はその index を飛ばし（貯めない、`schedule.late` の件数に含む）、遅れた tick の既存規則も維持します。Spout worker は同じオフセットを読むため、合成に対する送信位相（4 ms）は保たれます。**設計上の注記: 整列中は合成・Spout の周期が主表示の実周期（例 59.94 Hz）に追従します**（slew の範囲で位相を追うため）。合成公開の平均間隔を実周期として解析器が記録します。

記録: `compose.align`（GPU worker、`qpc`=観測時刻、`value`=位相誤差 e µs（符号付き）、`deadlineQpc`=W、`detail`=適用した補正量 c µs（符号付き文字列）、scheduledQpc・画像は 0）を補正判断ごとに 1 件。manifest には `options.composeAlign`、`options.composeLeadMs`、`alignSlewMs`。決定ロジックは `ComposeAlignGate` に分離し、`--self-test` でフェイク時計により wrap／clamp、収束、bootstrap 中の非補正、オフセット付きスケジュールの単調性、Spout 位相の維持、オプション検証を確認します。

### ループの空き時間待ち（高分解能 timer）

GPU worker と Spout worker の `Loop` は、次の予定時刻までの空き時間を停止 handle（index0）と worker 自身の waitable timer（index1、`VblankWaitTimer` を worker ごとに1つ、その worker のスレッドで作成しループ終了後に閉じる）で `WaitForMultipleObjects` により待ちます。timer は予定時刻そのもの（100 ns 単位、整数 ms への切り捨てや「1 ms 早く起きて yield」はしない）に設定し、残りが 50 µs 以下のときだけ `Thread.Yield()` で回ります（`LoopIdleWait`、`--self-test` で境界を検証）。以前の `WaitOne(残り整数ms − 1)` は 2〜4 ms 遅れて起きることがあり、`compose.start` の遅れとして観測されていました。timer の高分解能可否は manifest の `loopTimerHighResolution`（`gpu`／`spout` の bool、split でない場合 `spout` は null）に記録し、vblank 目標待ちの `highResolutionTimer` の意味は変えません。スケジュール・tick／ready／vsync／vblank の判定・合成と Present の順序は変えません。

### 映像ソース契約（`--source contract-fake`）

`--source pattern|contract-fake`（既定 `pattern`）を追加しています。設計は `docs/GPU-SOURCE-CONTRACT-SPEC.md`。`pattern` は従来どおり合成 tick が pool の surface へ直接パターンを描きます（周期・同期・記録は変えません）。`contract-fake` は `IVideoSource`（`VideoSource.cs`）経由に切り替え、合成層は「再生位置に対する最適な GPU 画像」をソースから lease で受け取るだけになります。

- 契約型: `SourceImageStamp(Generation, Sequence, PositionSeconds, DecodedQpc)`、`SourceStatus`（`Ready`／`NotReady`／`Ended`）、`ISourceImageLease`（`Texture`・寸法・`Format`（BGRA8／NV12／BC1／BC3／BC7）、`BeginGpuUse`／`CompleteGpuUse`、Dispose は1回）、`IVideoSource`（`SetGeneration`、`TryAcquire`、`Diagnostics`、`TryDispose`）。`Texture` はフェイク・テストでは null です。
- 純 C# の核 `SourceImageRing<TSlot>`（GPU なし、lock）: 3枚以上の環で規則1〜4・6を実装します。`SetGeneration(n)` 以後は世代 n の画像だけを返し、古い世代は lease が全て返った時点で解放します。`TryAcquire(gen, p)` は `p` 以下で最大位置の画像、無ければ `p` より先の最初の画像、無ければ `NotReady`（世代不一致も `NotReady`）。`Ended` はデコード側が `SignalEnd(end)` を出し、`p >= end` で `p` より先の画像が無いときだけです（末尾画像は end 手前まで `Ready`）。`Offer` は環が満杯なら最も古い未 lease の画像を置き換え（`replaced`）、全て lease 中なら**新しいデコード結果を捨てて数えます**（`dropped`。lease 中のテクスチャへは書きません）。`TryDispose` は lease が残る間 false を返し、強制解放しません。`Dispose` は lease が残っていれば例外です。
- `FakeVideoSource<TSlot>`: デコードスレッドの代わりに `Tick(nowQpc)` で駆動します。既定 30 fps（`origin + i/fps`、遅れた周期は飛ばして追いつかない）で1周期に最大1枚、`PositionSeconds` は予定時刻から、`DecodedQpc` は tick 時刻です。slot は環より1枚多く持ち（既定4枚、保持3枚）、満杯でも描画先が残るようにします。エンジンでは合成デバイス上の非共有 surface 4枚を所有し、`ShaderPipeline.Compose` で画像番号＝ソース `Sequence` のパターンを描いて EVENT query 完了後に `Offer` します。そのため `contract-fake` の画面上の 24 bit 番号はソース番号（30 fps で同じ番号が2合成に載る）で、`compose.publish` の imageId（合成番号、単調増加）とは別です。
- 合成 tick（GPU worker）: `fake.Tick(now)` → `pool.TryBeginWrite` → `TryAcquire(generation=1, position=経過秒)` → `Ready` なら keyed mutex 取得 → lease `BeginGpuUse` → `Display` シェーダーでキャンバスへ配置（`fit-height`、下記「固定キャンバスへの配置」） → フェンス signal → `gpu.Fence.Wait("compose")` → `CompleteGpuUse` → mutex 返却 → 公開 → lease `Dispose`。`NotReady`／`Ended` なら `compose.sourceNotReady`（value 1）で見送り、pool は前回の最新を保持します（保持は合成層の責務）。lease の返却は合成の GPU 完了確認の後で、pool 側に合成済みのコピーが残るのでソース側 texture への参照は残りません。ループ終了後、lease が無いことを `TryDispose` で確認してからソース surface を解放します（残っていれば Fault）。
- 記録: `source.acquire`（GPU worker、scheduledQpc は合成 tick、imageId＝ソース `Sequence`、generatedQpc＝`DecodedQpc`、detail＝`Ready`／`NotReady`／`Ended`、value＝位置 µs）を tick ごとに1件。`summary.json` に `sourceDiagnostics`（decoder、gpu、format、generationRejected、notReady、replaced、dropped、peakLeases、offered、ready、ended）、manifest に `source`（fps、slots、retainedImages、generation）。解析器は `source` 節（窓内の結果別件数、`compose.sourceNotReady` 件数、`sourceDiagnostics` の転記）を加え、旧ログは `available=false` のままです。
- `--self-test` に世代切替・位置前後・EOS・満杯置換／破棄・lease 規則・Dispose 順序・別スレッド `Offer`・フェイクの周期を int slot で検証する管理テストを追加しています（D3D オブジェクトは作りません）。

### 固定キャンバスへの配置（`CanvasPlacement.cs`、`--source-size`）

設計は `docs/CANVAS-PLACEMENT-SPEC.md`。Vortice／D3D に依存しない純粋な計算です。本体の `ProjectData`／`TrackData` は変更しません。

- 型: `CanvasSettings(Width, Height, DefaultFitId)`（既定 1920×1080・`fit-height`、0 以下の寸法は例外）、`ClipPlacement(FitId)`（null は既定を継承）、`PlacementRect(X, Y, Width, Height)`（実数）、`Placement(Destination, SourceCrop)`、`IFitCalculator { Id; Compute(srcW, srcH, canvasW, canvasH) }`、`FitRegistry`（ID で登録・解決、組込みは `fit-height`／`fit-width`）。
- **座標の規約**: `Destination` は常にキャンバス内（キャンバス座標）で、はみ出しはキャンバス外の `Destination` ではなく `SourceCrop`（素材ピクセル座標）を狭めて表します。描画側は viewport を `Destination` に、サンプリング範囲を `SourceCrop` にし、残りを不透明な黒でクリアします。倍率は縦横同一・中央揃え固定。合わせた辺はキャンバス寸法そのものに置き、`canvas/src*src` の丸め残差でサブピクセルの切り落とし・余白が生じないようにしています。
- `FitRegistry.Resolve(clip, canvas, out warning)`: `FitId` が null なら既定を無警告で継承、未知の ID は既定へフォールバックして `warning` に文字列を返します。既定 ID 自体が未登録なら例外です。
- `--source-size WxH`（既定＝キャンバス寸法、各 1〜8192）: `contract-fake` のソース画像の寸法です。キャンバスと異なる値は `--source contract-fake` でだけ許可します。合成 tick は黒でクリアしたキャンバスに、起動時に一度計算した `fit-height` の `Placement`（クリップ上書きなし）で lease 画像を置きます（viewport＝`Destination`、`Display` シェーダーが `uvOffset + uv * uvScale` で `SourceCrop` だけをサンプル。定数バッファ `b1`、`pattern` ソースと表示側の全画面 letterbox は全画像を使う恒等値）。例: `--source-size 1024x768` で左右に黒余白、`2560x1080` で左右を切り落とし。
- 記録: manifest に `placement`（`source`／`canvas` の寸法、`fitId`、`destination`、`sourceCrop`。`pattern` では null）と `options.sourceWidth`／`sourceHeight`。解析器は `placement` をそのまま `analysis.json` に転記し、検証はしません。旧ログは null です。
- `--self-test` に ID ごとの固定期待値（16:9 等倍、4:3 の余白／切り落とし、21:9 の切り落とし／余白、縦動画、1×1、キャンバスより大きい素材）、レジストリのフォールバック、無効寸法の例外、`--source-size` の検証を追加しています。

## SDK の前提

手元の SpoutDX.dll と同一 SHA256 の SDK 2.007.017 DLLを対象にします。

`BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5`

一致しない DLL は、C++ ABI を再確認するまで本実証では拒否します。対応するヘッダーの CPU 専用 MSVC x64 `/MT /D_ITERATOR_DEBUG_LEVEL=0 /DSPOUT_IMPORT_DLL` 確認では `sizeof(spoutDX)=1864`, `alignof=8` でした。従来実装の4096 byte確保を維持します。単にコンストラクター／デストラクターが動いたことをサイズ検証とは扱いません。確認資料は `TestResults/gpu-output-abi-20260909-185707` にあります。

## 記録 schemaVersion 1

- `manifest.json`: options、QPC frequency/epoch、アダプター LUID、表示先、実表示寸法、実送信名、DLL hash、SDKサイズ等。`presentWaitSegments` はfixedでも1区間あり、各要素に index・waitMs・startSeconds・endSeconds・analysisStartSeconds・analysisEndSeconds・startQpc・endQpc を記録します。GPU 初期化失敗時は `startupFailed=true` の限定情報になります。
- `events.jsonl`: `{stage, worker, qpc, scheduledQpc, imageId, generatedQpc, detail, value, deadlineQpc}`。全時刻は絶対 QPC tick。0 は対象なし。`generatedQpc` はその画像の合成開始時刻で不変です。
- `summary.json`: outcome (`completed` / `cancelled` / `faulted`)、validPerformanceResult、記録欠落、画像プールの最大使用、公開頻度と間隔・画像経過時間の統計、`scanoutPending`（未観測の Present 件数）と `scanoutDisjoint`。

両pacing条件とも `summary.json` の `appCpuSeconds` は自プロセスの `Process.TotalProcessorTime` の差、`appCpuStartQpc`／`appCpuEndQpc` はその測定区間を表します。`appCpuScope` は `whole-run: engine startup through native cleanup; excludes log serialization` です。エンジン開始からGPU/native資源解放後までの全体近似で、GPU初期化や終了処理、UI、送信スレッドも含みます。測定origin／warmup窓だけのCPU負荷ではなく、ログのJSON保存は含みません。

主なイベントは `compose.start/complete/publish`, `copy.start/complete`, `display.draw.start/complete`, `present.start/return`, `send.start/return/gpuComplete/publish`, `skip`, `error`, `lifecycle` です。`compose.publish` は GPU 完了・mutex返却後で、最新画像を CPU の共有状態へ渡す直前の記録です。`present.return` は S_OK のみ、`send.publish` は外側 mutex を解放した後です。CPU が完了を観測した時刻であり、GPU ハードウェア内部タイムスタンプではありません。

位相実証では `compose.visible` と `send.select.start/end` を追加しています。`compose.visible` は `pool.Publish` の復帰直後にQPCを採取し、既存の `compose.publish` と同じ画像・予定時刻を記録します。選択イベントの時刻は `AcquireLatest` の直前／直後に両方採取してからキューに追加します。両方に取得した使用権の画像ID・生成時刻を付け、取得なしなら0にします。終了イベントの detail は、保持中の送信用画像と異なるIDを得た場合 `latest`、同じなら `retained`、取得なしは `none` です。これらは公開と選択の前後区間を表すもので、プール内部の厳密な線形化時刻ではありません。

短時間待ち実証では `send.acquire.start/end` を追加しています。同じ worker・scheduledQpc・imageId で対になり、開始イベントの value は有効な待ち時間（整数ms）、終了イベントの detail は `acquired` / `busy` / `abandoned` です。両イベントの deadlineQpc はその仕事の次の予定時刻です。待ち時間の計算と区間開始に同じQPC時刻を使うため、区間には小さな方針判定・予算計算も含まれます。これはCPUから観測した取得区間で、純粋なカーネル待ち時間ではありません。前後時刻を捕捉してから記録をキューへ追加します。送信 API についても呼び出し前後時刻を捕捉してからキューへ追加し、最後の期限確認とネイティブ呼び出しの間に記録処理を挟みません。期限超過は `send.deadlineExpired`、キャンセルは `send.cancelled`、取得できなかった場合は従来どおり `send.accessMutexBusy` という skip 理由です。

全画面の準備待ちは `present.ready.start/end` を同じworker・scheduledQpc・imageId・deadlineQpcで対にして記録します。開始detailは `native`（native waitを1回）または `retained`（保持済み許可を利用しnative waitなし）、valueは有効要求msでretained時は0です。終了detailは `ready` / `notReady` / `retained` / `cancelled` / `deadline` / `error`、valueは表示許可を保持していれば1、なければ0です。待ち成功直後に期限超過・キャンセルを観測した場合もvalue1を維持します。エラーは記録後に異常終了へ進みます。

readinessの要求時間計算と区間開始も共通QPCを使い、前後時刻採取の間にログをenqueueしません。開始前の期限超過・キャンセルは対を作らず、`present.deadline` / `present.cancelled` でskipします。未準備は従来の `present.notReady` です。`display.draw.start` と `present.start` はそれぞれ実行直前の期限ガードと同じQPCを記録し、ログ追加を実行後へ回します。

`present.wait.segment` はGPUループが初めて区間を選んだときと、選択indexが変わったときだけ記録します。workerはGPU、scheduledQpcは適用した周期、qpcは観測時刻、detailは区間indexの十進文字列、valueは要求ms、deadlineQpcは区間endQpc、画像ID・生成時刻は0です。隣り合う区間の要求値が同じでもindexが変われば記録します。区間を飛び越えた場合は選択した区間だけを記録します。fixedも初回に1回記録します。deadlineQpcが非zeroになるのはこの区間イベント、`send.acquire.*`、`present.ready.*` です。区間イベントのdeadlineは周期ごとの描画期限とは異なります。

`ready` だけで記録する `display.wait.start/end` はGPU worker・現在tickのscheduledQpc・次回合成／共通終了の早い方のdeadlineQpcを持ち、画像はまだ選択していないのでimageId/generatedQpcは0です。start.detailは `native` / `retained`、valueは有効timeoutms（retainedは0）。end.detailは `ready` / `timeout` / `cancelled` / `deadline` / `error` / `retained`、valueはready権保持なら1、なければ0です。成功通知後の期限超過・停止でもvalue1を維持します。開始前の期限超過／停止はpairを作らず `display.wait.deadline` / `display.wait.cancelled` でskipします。native errorは記録してから異常終了します。時刻採取の間にログをenqueueせず、整数msの予算計算はstartと同一QPCを使います。

両条件共通の `display.select.start/end` は `AcquireLatest` の前後を採取後に記録し、両方に返却された画像stampと表示期限を付けます。end.detailは `latest` または取得なしの `none`（ID0）です。`ready` では `display.wait.end` の後に選択し、既存 `present.ready.*` は保持済み許可の `retained` 経路のみを通ります。これにより待機と画像選択の順序を区別します。keyed mutexの非保持はこのイベントだけで証明せず、コードの所有権レビューと合わせて判断します。deadlineQpcはこれら `display.wait.*` と `display.select.*` にも記録します。

source画像のkeyed mutex実保持区間を、両workerで `copy.mutex.acquire/release`（Spout）と `display.mutex.acquire/release`（GPU）の対として記録します。同じworker・scheduledQpc・画像stampで対になり、DetailはSpoutが `kind:slot`（kindは `first` / `retry`）、GPUがslotです。時刻は取得直後・返却直後に採取し、取得失敗時やleaseなしでは対を作りません。既定の `--copy-retry off` でも両workerの対は全経路で記録されます。`copy.retry` は `signal` での初回失敗後にのみ記録し、valueは要求待機ms、deadlineQpcは次回送信予定、画像ID・生成時刻は失敗時の選択stampです。skip理由として、2回目の取得失敗は `copy.keyedMutexBusy.retry`、待ち開始前の予算0・期限超過は `copy.retry.deadline`、停止検出は `copy.retry.cancelled` を使います。

`--source-sync fence` では `copy.fence.wait`（Spout、detail `kind:slot`）と `display.fence.wait`（GPU、detail slot）を、同じ worker・scheduledQpc・画像stamp で `copy.start`／`display.draw.start` の前に1件ずつ記録し、value はフェンス値＝画像IDです。区間記録（対）は作らず、keyed mutex 系のイベント・skip は出しません。

`--display-pacing vsync` の `display.vsync.wait.start/end` は GPU worker・直前の合成 tick の scheduledQpc・`min(次合成予定, 共通終了時刻)` の deadlineQpc を持ち、画像ID・生成時刻は0です。start の detail は `native`、value は残り整数ms（1以上）。end の detail は `ready`／`timeout`／`cancelled`／`error`、value は ready権保持なら1です。通知後の期限超過・停止は `display.vsync.deadline`／`display.vsync.cancelled` の skip で示し、権は保持します。表示は既存の `display.select.*`、`display.draw.*`、`present.start/return` で、`present.ready.*` と `present.notReady` は発生しません。

`--display-pacing vblank` では `display.vblank.wait.start/end`（GPU worker、直前の合成 tick の scheduledQpc、画像0）を順に対にします。start の detail は待ち先の種類 `target`／`compose`、value は要求待ち時間（µs の整数）、deadlineQpc はその目標時刻。end の detail は `target`／`compose`／`cancelled`／`error`、value は起床遅れ（end.qpc − deadlineQpc、µs、target 以外は 0）、deadlineQpc は start と同じです。1 合成枠に複数の対が並ぶことがあります（compose 待ち、見送り後の再予測）。`display.vblank.predict` は表示試行ごとに1件で、imageId は候補の最新画像（generatedQpc は 0）、value は予測 refresh 番号（`SyncRefreshCount` 系列、参考情報で判定には使いません）、deadlineQpc は予測 vblank の QPC、detail は推定周期（µs）です。予測付き試行の `display.select.*` の deadlineQpc は `min(予測 vblank, 共通終了)`（bootstrap の枠は合成期限）です。`display.vblank.bootstrap`（value 1）は統計を得る前の合成直後 Present を示し、この枠の Present には predict がありません。skip 理由は `display.vblank.notReady`／`display.vblank.noNewerImage`。表示は既存の `display.select.*`、`display.draw.*`、`present.start/return`、`present.scanout` で、`present.ready.*` と `present.notReady` は発生しません。manifest には `options.presentMarginMs` と `highResolutionTimer`（bool）を記録します。

`--compose-align vblank` の `compose.align` は GPU worker、`qpc`=`present.scanout` 観測直後の判断時刻、`value`=位相誤差 e（µs、符号付き）、`deadlineQpc`=望ましい合成時刻 W、`detail`=適用した補正量 c（µs、符号付き、`-clamp(e, ±slew)`）、scheduledQpc・画像は 0 です。`present.scanout` 1 件につき最多 1 件で、最初の観測より前にはありません。以後の GPU・Spout の scheduledQpc は起点＋累積補正＋round(i×period) になります。

`present.return` の `value` はその Present の `GetLastPresentCount`（swapchain 作成からの Present 呼出し番号、S_OK のときだけ記録）です。`present.scanout` は GPU worker、`qpc`=統計の観測時刻、scheduledQpc は0、`imageId`／`generatedQpc`=対応する Present の画像（unmapped は0）、`value`=`SyncRefreshCount`、`deadlineQpc`=`SyncQPCTime`（vblank の QPC 値）、`detail`=`"{PresentCount}:{PresentRefreshCount}"`（表に無ければ `:unmapped` を追加）です。同じ PresentCount の `present.scanout` は1件だけで、対応する `present.return` より後に記録されます。`present.stats.disjoint`（detail `disjoint`、画像0）は1 run に最大1件です。

解析窓は `[warmup, seconds-warmup)`。32秒/5秒なら `[5,27)` の22秒です。出力が要求されたのに解析窓で成功イベントが無い場合、期間不足、異常、ログ上限100万件を超えた場合は有効結果にしません。受信側の存在確認は本アプリ単独では行わず、ハーネス／操作者が別途評価します。

API/GPU 公開は受信画像の実到達・一意画像数・物理走査を証明しません。また本実証の入力は GPU 生成画像のため、既存 mpv 試験との差をそのままアプリ全体の改善量とは扱いません。

