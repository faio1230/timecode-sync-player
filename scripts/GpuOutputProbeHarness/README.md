# GPU出力実証の実行・解析

設計は [GPU-OUTPUT-PROBE-SPEC.md](../../docs/GPU-OUTPUT-PROBE-SPEC.md)。このフォルダーは検証アプリとは独立した補助スクリプトです。ビルド・アプリ・受信機の起動は自動で行われません。実機試験は親エージェントが明示した条件を1回ずつ実行します。

## 1回の実行

WindowsのPowerShellから、ビルド済みDebugアプリと公式 `WinSpoutDXreceiver.exe` の絶対パスを指定します。既定は1080p、60Hz、12秒、解析窓 `[2,10)` 秒です。表示先は明示してください。

```powershell
& .\scripts\GpuOutputProbeHarness\Run-GpuOutputProbe.ps1 `
  -AppExe 'C:\path\GpuOutputProbe.exe' `
  -ReceiverExe 'C:\path\official-examples\WinSpoutDXreceiver.exe' `
  -Mode common -Output both -Width 1920 -Height 1080 `
  -Fps 60 -Seconds 12 -Warmup 2 -MonitorIndex 0 -Windowed `
  -LogRoot '.\TestResults\gpu-output-probe'
```

4Kの比較条件は `-Width 3840 -Height 2160 -Seconds 32 -Warmup 5` とします。`-Mode common|split`、`-Output fullscreen|spout|both` を指定します。ウィンドウ試験では `-Windowed`、全画面では省略します。配列・繰り返し・マトリクス引数はありません。前回の異常を調べる前に次の試験を自動で開始しません。

Spout mutex待ちの指定は `-MutexWaitMs` です。現在の許容範囲は整数0〜8ms、既定0msで、アプリの `--mutex-wait-ms` と `command.json` に記録します。初期の0ms／1ms比較段階では許容範囲を0〜4msとしていました。指定値は待ち時間の上限要求であり、OSによる実際の復帰時間を保証しません。周期の残り時間に応じた実効待ち時間はアプリのイベントから解析します。

前段のmutex比較は `-Mode split -Output both -SendPhaseMs 4 -ReceiverMode official` を固定し、待ち時間だけを4 → 8 → 8 → 4msと変える条件です。runnerのmutex既定0msは維持します。

全画面の準備待ちは **`-PresentWaitMs 0|1`** で指定します。整数0〜1ms、既定0msで、0以外にはfullscreenまたはboth出力が必要です。`--present-wait-ms`、`command.json`、アプリmanifestの値を揃えます。

前段では、同一バイナリで別プロセスの `PresentWaitMs` を0 → 1 → 1 → 0と変えました。0ms条件にも新しい期限・ready権管理を適用し、以前のバイナリの0ms結果だけを比較基準にはしません。

前段の有界比較は `-PresentWaitPlan abba|baab` による同一プロセス・同一swapchain内の区間切替です。既定の `fixed` は従来どおり `PresentWaitMs` を全期間に適用します。`abba` は0 → 1 → 1 → 0ms、`baab` は1 → 0 → 0 → 1msで、`Seconds` を4等分します。切替プランはfullscreenまたはboth出力、`PresentWaitMs=0`、`Seconds/4 > 2*Warmup` が必要です。`--present-wait-plan`、`command.json`、manifestの値を揃えます。区間切替でready権・GPU資源・周期の原点を初期化しません。

親が `-Mode split -Output both -SendPhaseMs 4 -MutexWaitMs 8 -ReceiverMode official -MonitorIndex 1` を固定し、まず1080pで `-PresentWaitPlan abba -Seconds 32 -Warmup 2 -Windowed` を確認します。その後、同じバイナリで4Kの `-PresentWaitPlan abba -Seconds 128 -Warmup 5`、正常確認後に `baab` を直列実行します。4Kでは `-Windowed` を省略します。複数runの自動連続起動は行いません。

**現在の比較は `-DisplayPacing tick|ready` です。** 既定 `tick` は従来の周期冒頭の準備確認を維持します。`ready` は合成後、画像のlease・keyを取得する前に停止通知と表示準備通知を待ちます。`ready` は `-Mode split -Output both -PresentWaitMs 0 -PresentWaitPlan fixed` だけで許可します。`--display-pacing` と `command.json` に記録し、解析でmanifestと照合します。

親がまず1080p・`-DisplayPacing ready -Seconds 12 -Warmup 2 -Windowed` を確認し、その後4K・各 `-Seconds 32 -Warmup 5` で **tick → ready → ready → tick** を直列実行します。split/both、位相4ms、mutex8ms、official受信機、MonitorIndex 1、PresentWaitMs 0、fixedプランを固定し、同一バイナリを使います。4Kでは `-Windowed` を省略します。表示1面の待ち方の実証であり、本体アプリへの統合や新しい表示スレッドは含みません。

**追加の切替は `-SourceSync keyed|fence`（既定 `keyed`）と `-DisplayPacing vsync` です。** `fence` は `-Mode split` かつSpoutを含む出力だけで許可し、`-CopyRetry signal` とは併用できません。`vsync` はfullscreenまたはboth出力、`-PresentWaitMs 0`、`-PresentWaitPlan fixed` が必要で、`common`／`split` の両方で使えます。それぞれ `--source-sync`／`--display-pacing` と `command.json` に記録し、解析でmanifestと照合します。設計は [GPU-FENCE-VSYNC-PROBE-SPEC.md](../../docs/GPU-FENCE-VSYNC-PROBE-SPEC.md)。

**`-DisplayPacing vblank` と `-PresentMarginMs`（double、既定 3、0.5〜8）** は vblank 位相予測の比較条件です。`vblank` は fullscreen または both 出力、`-PresentWaitMs 0`、`-PresentWaitPlan fixed` が必要で、`common`／`split` の両方で使えます。`-PresentMarginMs` は常に `--present-margin-ms` として渡し、既定以外の値は `vblank` でだけ許可します。両方を `command.json`（`displayPacing`、`presentMarginMs`）に記録し、解析で manifest と照合します。設計は [GPU-VBLANK-PACING-PROBE-SPEC.md](../../docs/GPU-VBLANK-PACING-PROBE-SPEC.md)。確認は全画面 1080p 12秒（monitor index 1、`-Windowed` なし）、その後 4K で tick → vblank → vblank → tick を各32秒、fence・位相4ms・retry off・同一バイナリで直列実行します。margin は掃引しません。

**`-ComposeAlign off|vblank`（既定 `off`）と `-ComposeLeadMs`（double、既定 1.5、0.5〜8）** は合成位相の vblank 整列の比較条件です。`vblank` は `-DisplayPacing vblank` かつ fullscreen または both 出力が必要で、`-ComposeLeadMs` の既定以外は `vblank` でだけ許可します。両方を常に `--compose-align`／`--compose-lead-ms` として渡し、`command.json`（`composeAlign`、`composeLeadMs`）に記録して解析で manifest と照合します。設計は [GPU-COMPOSE-ALIGN-PROBE-SPEC.md](../../docs/GPU-COMPOSE-ALIGN-PROBE-SPEC.md)。確認は全画面 1080p 12秒（align vblank）、その後 4K で align off（vblank のみ）→ vblank → vblank → off を各32秒、fence・位相4ms・retry off・margin 3ms・monitor index 1・同一バイナリで直列実行します。slew・lead は掃引しません。

送信周期の位相を比較する段階では `-SendPhaseMs 0` や `-SendPhaseMs 3` を指定します。既定0ms、有限の数値で `0 <= phase < 1000/Fps` が必要です。0以外は `-Mode split` かつSpoutを含む出力だけで許可します。アプリの `--send-phase-ms` と `command.json` へ記録し、Spoutワーカーの予定時刻の原点だけを指定時間分ずらします。合成側の原点と試験終了時刻は変わりません。

前段の位相比較では **`-MutexWaitMs 4` を明示して固定**し、位相だけを変える条件を使いました。例は `-Mode split -Output both -MutexWaitMs 4 -SendPhaseMs 3` です。各段階の基準条件も同一バイナリで測定します。

公式受信機の起動有無を比較する対照試験には **`-ReceiverMode official|none`** を使います。既定は従来どおり `official` です。`none` はSpoutを含む出力だけで許可し、所有受信機を起動しません。Spout送信そのものは有効のままです。`ReceiverExe` と実行ファイル・DLLのハッシュ記録は両条件で維持します。この条件変更はrunnerだけが扱い、アプリのバイナリを変更しません。

前段の受信機対照比較は `-Mode split -Output both -SendPhaseMs 4 -MutexWaitMs 4` を固定し、none → official → official → noneと `-ReceiverMode` だけを変える条件です。自動マトリクスにはしません。所有受信機を起動しないことは、他アプリの接続が存在しないことを保証しません。

実行ごとにUTC時刻とGUIDを含む新規ディレクトリを作り、既存ファイルを上書きしません。アプリのログ先 `app` はアプリ自身が新規作成します。

- `preflight.json`: GPUドライバー・OS側の表示情報・プロセス一覧を読み取りで記録。CIMが提供しない情報は推測せず、エラーや空欄のまま保持します。正確なアダプター・表示条件はアプリのmanifestも参照します。
- `inputs.json`, `command.json`: 実行ファイル・隣接DLLのSHA256、CLI、作業ディレクトリ。
- `app-owned.json`, `receiver-owned.json`: 自分で開始したプロセスのPID・生成時刻・観測QPC。
- `receiver-samples.json`: 自分の受信機のウィンドウ名・位置・可視性と生存確認。約1秒間隔。
- `runner-result.json`: 正常終了・タイムアウト・自分の受信機の終了結果。
- `app/manifest.json`, `app/events.jsonl`, `app/summary.json`: アプリからの元ログ。

同じセッションの同時runnerは名前付きmutexで拒否し、同じ実行ファイルの残存プロセスも起動前に確認します。他アプリを終了しません。指定時間＋既定30秒を超えたアプリは**自動で強制終了しません**。アプリの終了UIを操作し、前回プロセスの状態を確認してください。

official条件の受信機はアプリ開始の約1秒後に `Start-Process -WindowStyle Hidden` で起動し、専用の新規作業ディレクトリを使用します。通常終了時・異常時とも、自分の受信機だけにWM_CLOSEを送ります。5秒以内に終了しない場合は、記録したPIDと生成時刻を再確認して自分の受信機だけを終了します。この場合、試験を正常扱いしません。アプリへの強制終了は行いません。none条件では受信機の起動・終了操作をせず、`receiver-owned.json` も作成しません。

公式サンプルの[ソース](https://github.com/leadedge/Spout2/blob/master/SPOUTSDK/SpoutDirectX/SpoutDX/Windows/Receiver/WinSpoutDX.cpp)ではCLIの送信者名指定はなく、ウィンドウ名も固定です。そのため、実際にこの送信者を受信したか、異なる画像が届いたかはrunner単独では確認できません。別途、親が自分の受信機で目視確認します。グローバルな送信者選択設定は書き換えません。公式サンプル自体はReceiveImageによるCPU読み戻しとGDI描画を行うため、送信側がGPUだけの経路でも、受信側の負荷は含まれます。ローカル実行ファイルの同一性は保存したハッシュで区別します。

## 元ログの解析

Python標準ライブラリーのみを使用します。引数はrun全体ではなく、その中の `app` ディレクトリです。

```powershell
python .\scripts\GpuOutputProbeHarness\analyze_probe.py `
  '.\TestResults\gpu-output-probe\<run>\app'
```

既定の解析窓は `warmup <= t < seconds-warmup`。32秒・warmup 5なら `[5,27)` の22秒間です。`--start 5 --end 27` で明示できます。出力は隣接する新規 `analysis-<UTC>-<GUID>` ディレクトリで、`--output <存在しないディレクトリ>` も指定できます。元ログとアプリのsummaryは変更しません。

切替プランの既定窓は複数設定と内部の区間境界を含むため、固定条件の結果として比較しません。manifestの `presentWaitSegments` にある各 `analysisStartSeconds/analysisEndSeconds` を `--start/--end` へ渡し、区間ごとに新しい解析先を指定します。128秒・warmup 5なら `[5,27)`、`[37,59)`、`[69,91)`、`[101,123)` です。例えば最初の区間は次のように解析します。

```powershell
python .\scripts\GpuOutputProbeHarness\analyze_probe.py `
  '.\TestResults\gpu-output-probe\<run>\app' `
  --start 5 --end 27 --output '.\TestResults\gpu-output-probe\<run>\analysis-segment-0'
```

`analysis.json` の `presentWaitPlan`、`presentWaitSegments`、`presentWaitWindow` にプラン、区間定義、指定窓が単一区間か複数区間か、含む待ち設定とguard内への収まりを記録します。同じ1ms設定の隣接2区間を含む窓も、単一区間とは区別します。混在設定の窓では `presentReadiness.configuredWaitMs=null` とします。

- `analysis.json`: イベント頻度、間隔と画像年齢の平均・p95・p99・最大、異なる画像ID、同一画像の連続再送、ID逆行、IDの飛び、スキップ理由と件数、判定の限界。
- `metrics.csv`: ステージごとの比較用数値。
- `phase-durations.csv`: 合成、GPUコピー、表示描画、Present、SendTexture呼び出し、送信後のGPU完了待ち、送信全体の所要時間。
- `output-id-differences.csv`: 各Spout公開時点で、直前の全画面Present復帰との画像ID差。物理表示の同期差ではありません。
- `selection.csv`: 位相計測を含む新ログだけに追加。取得した画像の年齢区間、合成予定→送信予定の差、公開・選択の観測境界と候補画像IDの上下界。

間隔は解析窓内にある隣接イベント間で算出します。百分位はアプリと同じnearest-rank方式（並べ替えた値の1始まり `ceil(n*p)` 番目）です。`send.start` / `present.start` のimageAgeが生成開始から出力開始までの時間、`send.publish` / `present.return` のimageAgeは各処理の終端までの時間です。頻度は再送を含み、異なる画像IDの数とは分けます。IDの飛びを受信欠落と断定しません。公開頻度は解析窓開始からの完全な1秒区間ごとの件数と最小・最大も示します。端数区間はこの1秒統計から除外します。

所要時間の組み合わせは、元ログ全体の `(worker, scheduledQpc, imageId)` で照合します。同じ画像の再送も別の予定時刻なら別の処理として扱い、開始・終了の両方が解析窓内にある組だけを統計に含めます。欠けた開始／終了、重複キー、逆転した時刻、境界をまたいだ除外組、窓外の除外組をそれぞれ記録します。Presentが実行開始後にスキップ結果を返す場合など、終了イベントがない組の時間を推測しません。

mutex計測イベントがある場合は、`send.acquire.start → send.acquire.end` の所要時間を追加し、`analysis.json` の `acquisition` に設定待ち時間、開始時点の実効要求時間、実測時間、取得成功・busy・abandoned件数を記録します。正の待ち時間を要求した呼び出しだけについて、`max(0, 実測時間 − 実効要求時間)` の分布と超過件数を計算します。0ms呼び出しのコストは別の分布にし、「待ち時間の超過」には数えません。

取得開始QPCは残り時間の計算にも同じ値を使います。このため取得所要時間には、短い待ち時間の決定処理とOS mutex呼び出しの時間が含まれ、OS内で実際に待機していた時間だけを表すものではありません。予算計算と別の時刻を使ってミリ秒境界の判定が食い違うことを避けています。

取得イベントの正の `deadlineQpc` から、復帰時刻が期限以上になった件数、取得は成功したが期限以上になり送信を開始しなかった件数も示します。busyや意図的な `send.deadlineExpired` / `send.cancelled` スキップだけで試験を無効にしません。一方、取得イベントの欠け・重複、正の期限の欠如、変化した画像時刻や期限、設定または周期の残り整数ミリ秒を超える実効要求時間は無効です。新しい `mutexWaitMs` 設定を含むログでは、すべてのSendTexture開始が一意に対応する取得成功と、その期限前の復帰を持つことも検証します。この検証はウォームアップ・終了前の期間を含むログ全体に行います。mutex計測イベントがない旧ログも引き続き解析でき、`acquisition.available=false` とします。

全画面準備待ちの `present.ready.start/end` は `analysis.json` の `presentReadiness` と既存の `phase-durations.csv` に集計します。native待ちと保持済みready権の再利用を分け、所要時間・結果・正の要求時間の超過を示します。0ms呼び出しのコストやretained再利用を正の待ち時間超過へ混ぜません。

ready権は待機成功で取得し、Present試行でだけ消費します。待機成功後に期限・停止へ到達した場合や、描画後にPresentを見送った場合は保持を続けます。次周期は新しい画像を選び、native待機を繰り返さず権を再利用できます。解析器はこの状態をログ全体で追跡し、権のない描画・Present、保持中のnative再待機、同周期の再試行、期限後の描画・Presentを拒否します。

期限はfractional fpsの丸めも含めてGPUの予定時刻から次周期を導き、`min(次周期、共通終了時刻)` と一致することを確認します。設定・実効待ち時間・残り整数ミリ秒の上限、イベント対と画像情報も検証します。新しいcommandが存在するログでは `presentWaitMs` がmanifestと明示的に一致する必要があります。旧ログは引き続き解析でき、`presentReadiness.available=false` とします。期限判断と記録QPCが一致しても、その後のOSによる中断を防げるわけではなく、厳密な実時間動作の証明にはしません。

切替プランの新ログでは、manifestの区間定義とcommandを照合し、各処理の `scheduledQpc` が属する半開区間から設定を選びます。native待ちの実効値は単なる上限判定に加え、`min(区間設定, 開始から期限までの残り整数ミリ秒)` と一致する必要があります。retained再利用は0msです。`present.wait.segment` はGPUループで適用区間が変わった観測を表し、隣接区間の設定値が同じでも記録します。遅れで丸ごと飛ばした区間のイベントは要求しません。予定時刻による所属と実測QPCを混同せず、区間境界でもready権の追跡を継続します。これらの検証は指定した統計窓の外も含むログ全体に適用し、区間情報のない旧ログとの互換性を維持します。

ready方式の `display.wait.start/end` は画像ID・生成時刻を0とし、`analysis.json` の `displayWait` と `phase-durations.csv` に集計します。1周期に1対まで、期限は `min(次合成予定時刻, 共通終了時刻)`、nativeの要求時間は開始時点の残り整数ミリ秒と一致させます。早くtimeoutしても同周期では再待機しません。端数ミリ秒の準備通知を取り逃す可能性が残り、OSの復帰時間も保証しません。retainedはnative待機なし・要求0msです。正の要求時間を超えた所要時間、0ms呼び出し、retained再利用は分けます。

全ログのready権追跡では `display.wait.end` が権を取得し、その後の `present.ready` はretainedだけを許可します。期限超過後に得た権、null選択やsource取得失敗で使わなかった権は次周期へ持ち越せます。次周期は合成を先に行い、最新画像を選び直します。保持中のnative再待機、同周期の重複待機・表示、期限後の選択・描画・Present、取消後の新しいGPU処理を拒否します。tick方式の既存追跡は維持します。

両方式の新ログは `display.select.start/end` でAcquireLatestの前後を記録します。ready方式では同周期の使用可能なwait終了が選択開始以前に必要で、選択終了が画像に関連する準備確認・描画・Presentより前に必要です。合成と選択は同じGPUスレッドなので、選択は開始時点までに `compose.visible` が記録された最新画像と一致させます。先行公開があるのにnullを選ぶことも無効です。sourceのkey取得失敗により選択後の描画がない場合は許容します。待機中の画像情報0とイベント順序を検証してもkey非保持そのものを証明するわけではなく、そこはアプリの寿命管理コードのレビューで確認します。

`sourceSync=fence` の新ログでは `analysis.json` の `sourceSync` に検証結果と件数を記録します。`display.mutex.*`／`copy.mutex.*` の不在、`copy.keyedMutexBusy`（retry含む）・`present.keyedMutexBusy`・`compose.keyedMutexBusy` の0件、各 `copy.start`（および `display.draw.start`）に同じ worker・scheduledQpc・画像IDの `copy.fence.wait`（`display.fence.wait`）がちょうど1件先行すること、フェンス値（value）が画像IDと一致することをログ全体で要求します。`fence` は split かつ Spout 出力かつ `copyRetry` が `signal` でないことが必要で、commandがある場合は `sourceSync` の明示一致も必要です。`keyed` を明示したログや旧ログで `*.fence.wait` があれば無効です。`sourceSync` のない旧ログは判定を変えず `sourceSync.available=false` とします。

`displayPacing=vsync` の新ログでは `analysis.json` の `vsync` に、`display.vsync.wait.start/end` の対（合成枠ごとに1対、GPU worker、画像0、deadlineは `min(次合成予定, 共通終了時刻)`、要求は残り整数msで1以上、`native` のみ）、結果別件数、所要時間、要求超過、`display.vsync.*` skip の件数、窓内のPresent件数を記録します。ログ全体の再生で、ready権は `display.vsync.wait.end`（`ready`、value 1）でだけ得て `present.start` でだけ消費します。権のない選択・描画・Present、権を保持したままの native 再待機、最後の表示より新しい画像がない状態での待機開始、`present.notReady`、`present.ready.*`、同一IDの再表示・IDの減少を無効にします。表示前の `display.select` 対と期限前の選択は既存規則のままです。`vsync` は表示出力、`presentWaitMs 0`、fixed プランが必要です。tick／ready の既存判定は変えません。

`displayPacing=vblank` の新ログでは `analysis.json` の `vblank` に、`presentMarginMs`、manifest の `highResolutionTimer`、窓内の `display.vblank.wait.start/end` の件数と復帰理由（`target`／`compose`／`cancelled`）、起床遅れ `wakeLatenessMs`（target 復帰の end.qpc − deadlineQpc）、要求待ち時間 `requestedWaitMs`、予測誤差 `predictionErrorMs`（表示した frame の `present.scanout` の SyncQPCTime − `display.vblank.predict` の deadlineQpc。predict → `present.start` → `present.return` の PresentCount → `present.scanout` で対応付け）、窓内の Present 件数と predict 付き Present 件数、合成期限より後の `present.start` 件数 `presentsAfterComposeDeadline`（参考値。予測付き試行の期限は予測 vblank なので許容）、そのうち期限を迎えた合成（期限以降に予定された GPU の `compose.start`）が始まる前に Present した件数 `presentsBeforeDueCompose`（参考値。目標を過ぎて起床しても vblank が lead 1 ms 以上先なら合成より先に Present する仕様どおりの動作）、窓内で連続する表示 ID の差が 1 を超えた回数 `presentedIdGaps`（参考値。飛び・重複の判断材料）、`counts`（`noNewerImage`／`notReady`／`bootstrap`）を記録します。ログ全体で、wait は worker ごとに start→end を順に対にし（入れ子は無効）、start の value は非負整数、deadlineQpc は正、kind／outcome は規定値のみ、`error` は無効。`display.vblank.bootstrap` の枠以外の GPU の `present.start` には、同じ枠・同じ画像の直前の `display.vblank.predict` が必要で、表示した predict の deadlineQpc（予測 vblank）は表示ごとに半周期（predict の detail の µs から換算）より大きく増加（予測 vblank あたり表示1回。value の予測 refresh 番号は参考情報）、表示 ID も単調増加でなければなりません。予測付き試行の `display.select.*` の deadlineQpc は `min(直前の同じ枠の predict の deadlineQpc, 共通終了)` で、選択はその期限より前に始まる必要があります（合成期限より後でも可。bootstrap の枠は合成期限）。`present.notReady` と `present.ready.*` は vblank では無効です。`vblank` は表示出力、`presentWaitMs 0`、fixed プラン、0.5〜8 の `presentMarginMs` が必要で、vblank 以外で既定 3 と異なる `presentMarginMs` や `display.vblank.*` イベントがあれば無効です。tick／ready／vsync の既存判定と `scanout` セクションは変えず、vblank イベントのない旧ログは `vblank.available=false` です。

`composeAlign` を含む新ログでは `analysis.json` の `composeAlign` に、`composeAlign`／`composeLeadMs`（options）と `alignSlewMs`（manifest）、`compose.align` の件数（ログ全体、最初の1秒、解析窓）、位相誤差 |e| ms の分布（`phaseErrorAbsMs` は窓内、`phaseErrorAbsMsFirstSecond` は収束前の最初の1秒）、補正量の合計 ms（窓内 `correctionSumMs`、ログ全体 `correctionSumWholeLogMs`）、再構成した最終オフセット `finalOffsetMs`、窓内の GPU `compose.publish` の平均間隔 `composePeriodMs`（整列中の実周期）、窓内で連続する `present.scanout` の SyncQPCTime 差の中央値 `vblankPeriodMs`、その差 `periodDifferenceUs` を記録します。生成→走査の主指標は従来どおり `scanout` セクションです。ログ全体で、`compose.align` は `composeAlign=vblank` でだけ許可し、GPU worker・画像 0・整数の `value`（e µs）・整数文字列の `detail`（c µs）・正の `deadlineQpc`（W）を要求、`c == -clamp(e, ±slew)`（|c| ≤ slew。slew µs は manifest の `alignSlewMs` から probe と同じ丸めで算出）、最初の `present.scanout` より前の補正や 1 観測につき 2 件以上の補正は無効です。オフセットは detail の µs を `c×qpcFrequency/1e6`（切り捨て）で tick に戻した累積和として再構成し、取得された GPU の合成枠（`compose.start` または `compose.*` skip の scheduledQpc）すべてが `origin + そのイベント直前のオフセット + round(i×period)` と一致すること、GPU `compose.start` の scheduledQpc がログ全体で単調増加であること（起点を動かしても過去の予定時刻を渡さない）を検証します。整列中は `display.select` の枠を index 式ではなく「取得された GPU 合成枠であること」で検証します（bootstrap の期限式はそのまま。予測付き試行の期限は予測 vblank）。`vblank` セクションの `presentsAfterComposeDeadline`／`presentsBeforeDueCompose` は再構成したオフセットを含めた合成期限で数えます。`composeAlign` を持たない旧ログは判定を変えず `composeAlign.available=false`、`off` のログは `compose.align` があれば無効、既定以外の `composeLeadMs` は `vblank` 以外で無効、command がある場合は `composeAlign`／`composeLeadMs` の明示一致が必要です。

DXGI フレーム統計を含む新ログでは `analysis.json` の `scanout` に、窓内（観測時刻 `qpc` 基準）の `present.scanout` 件数（mapped／unmapped 別）、`present.start → SyncQPCTime`（`presentStartToSyncMs`）、`generatedQpc → SyncQPCTime`（`generatedToSyncMs`、tick／vsync 比較の主指標）、`compose.publish → SyncQPCTime`（`composePublishToSyncMs`）、統計の観測遅れ（`observationLagMs`＝観測時刻−SyncQPCTime）の平均／p95／p99／最大、窓内の連続する `SyncRefreshCount` の差（`syncRefreshStep.notOneCount` が1でない件数、`counts` が差ごとの件数）、`PresentRefreshCount − SyncRefreshCount` の分布、ログ全体の unmapped 件数、`present.stats.disjoint` 件数、summary の `scanoutPending`／`scanoutDisjoint` を記録します。設計は [GPU-SCANOUT-STATS-PROBE-SPEC.md](../../docs/GPU-SCANOUT-STATS-PROBE-SPEC.md)。

対応付けはログ全体で検証します。`present.return` の `value`（PresentCount）は正の整数で単調増加・重複なし。`present.scanout` の `detail` は `PresentCount:PresentRefreshCount[:unmapped]`、`value` は `SyncRefreshCount`、`deadlineQpc` は正の `SyncQPCTime`、worker は GPU。scanout の PresentCount は観測順に単調増加で、逆行・重複は無効です。mapped の scanout は同じ PresentCount の `present.return` がちょうど1件あり、その `present.return` より後に観測され、画像ID・generatedQpc が一致する必要があります。unmapped は画像0でなければなりません。PresentCount の飛び（未観測の Present）、unmapped、disjoint は件数として記録し、無効理由にはしません。統計は DWM の flip した vblank を表し、物理的な発光時刻ではありません。`present.return` に `value` が無く scanout も無い旧ログは `scanout.available=false` で従来どおり有効です。CSV の列は変えません（`metrics.csv` には `present.scanout` がステージ行として現れます）。

`loopTimerHighResolution` は manifest の同名オブジェクト（両 worker の `Loop` の空き時間待ちに使う waitable timer が高分解能かどうか。`gpu`／`spout` の bool で、Spout worker がなければ `spout` は null）をそのまま保存します。旧ログでは null です。この timer は空き時間の起床精度（`compose.start`／`send.start` の予定時刻からの遅れ）に関わり、vblank 目標待ちの `vblank.highResolutionTimer` とは別です。

`processCpu` はsummaryの `appCpuSeconds`、`appCpuStartQpc`、`appCpuEndQpc`、`appCpuScope` を保存し、QPC差の経過秒と `CPU秒 / 経過秒` による平均占有コア数の近似を示します。対象はエンジン起動からnative資源の片付けまでのアプリプロセス全体で、ログJSON保存を除きます。指定解析窓だけのCPUではなく、公式受信機のCPUも含みません。区間別に解析してもこの値の測定範囲は変わりません。CPU情報のない旧ログは `processCpu.available=false` とし、欠落を0使用量とは扱いません。

位相計測の `compose.publish` はプール公開呼び出しの前、`compose.visible` はその復帰直後の観測です。`send.select.start/end` はAcquireLatestの前後を囲み、両方に取得結果の同じ画像情報を付けます。いずれもプール内部で公開・取得が成立した正確な時刻ではありません。

`analysis.json` の `selection` は、画像年齢の開始・終了境界、選択画像の合成予定から送信予定までの差、compose.visibleから選択境界までの差を示します。位相0で前周期の画像を選んだ場合、予定時刻の差は60Hzなら約16.67msになります。画像が選択開始後に生成された正常な競合では、開始時点の年齢が負になることがあります。これは取得区間の下限として許容しますが、選択終了時点や実際の送信時点で未生成の画像は無効です。公開成功の平均画像年齢など既存の指標は変えません。

確実に候補となった画像は `visible < selection.start`、可能性のある候補は `publish <= selection.end` から求めます。厳密な不等号の違いは、同じQPC値になった境界を確定扱いしないためです。新しい画像の公開前後区間が選択区間と重なる場合は `publicationRaceUncertain` として記録し、選択IDが小さいという理由だけで古い画像を選ぶバグと断定しません。visibleが選択の後に観測されることもあるため、visible→選択の負の差もそのまま保存します。

新ログでは公開前後の対、選択前後の対、画像情報、予定時刻の整合をログ全体で確認します。`sendPhaseMs` を含むSpout出力ログのSendTexture開始には対応する選択対が必要ですが、選択画像と送信画像のID一致は要求しません。null選択、同じ画像の保持、コピーがbusyだった場合に以前の送信用画像を送る動作を許容します。位相イベントがない旧ログは引き続き解析でき、`selection.available=false` です。

選択対は `(worker, scheduledQpc, 試行番号)` で照合します。試行番号は `send.select.start/end` の `value`（欠落・0は初回、1は `--copy-retry signal` の同一枠再試行）で、0・1以外の値は無効です。再試行の対には、同じ枠で先に終わった初回の対、初回終了と再試行開始の間に1件だけの `copy.retry`、初回終了と `copy.retry` の間の `copy.keyedMutexBusy` skip が必要で、再試行の開始は `copy.retry` の deadlineQpc より前でなければなりません。1枠に再試行の対は1組までで、manifestに `copyRetry` がある場合は `signal` 以外で再試行・`copy.retry` があれば無効です。SendTexture開始は同じ枠の最後の試行の対と照合します。`phase-durations` の `send.select.start->send.select.end` だけは組み合わせキーに試行番号を含めるため、同じ画像を選び直しても重複キーになりません。`selection` の各行に `attempt`、集計に `retryAttemptsInWindow`（両端が窓内の再試行の対）と `retrySelectedNewerImage`（再試行のIDが初回より大きい件数）を追加します。

keyed mutexの保持区間だけを短くまとめる補助として `analyze_mutex_holds.py` があります。引数は `app` ではなくrunディレクトリで、1runにつき1行のJSONを標準出力に書き、ファイルは作りません。

```powershell
python .\scripts\GpuOutputProbeHarness\analyze_mutex_holds.py '.\TestResults\gpu-output-probe\<run>'
```

表示側・コピー側の保持時間、初回busyのうち同じ画像の表示側保持区間で説明できない件数、`copy.retry` 後の再取得成功・失敗と新しい画像を得た件数、native表示待ちの所要時間を示します。保持イベントのない旧ログは統計をnullにします。 `sourceSync=fence` のログも同様に保持統計をnull、`copy_busy_first` を0として動き、`sourceSync` を出力に含めます。

アプリsummaryの有効フラグがfalse、終了状態がcompleted以外、ログ欠落、errorイベント、壊れたJSON行、必要な出力の欠如、解析窓の途中でログが終わる場合は無効にします。出力公開の画像ID逆行、元のcompose.publishが一意に見つからない画像、元画像とgeneratedQpcが異なる画像、処理の組み合わせが重複・時刻逆転している場合も無効です。元画像の照合には解析窓より前のログも使います。runnerの結果がある場合は、runner異常と、受信機が解析窓全体で生存した証拠がない場合も無効です。無効な場合も取得できた診断値は保存し、終了コード2を返します。入力を読めない場合は1、有効なら0です。

受信機生存確認はofficial条件に適用します。旧ログはofficialを既定とし、起動に失敗したofficial条件をnoneへ変更して救済しません。新しいnone条件は `command.json` と `runner-result.json` の両方に `receiverMode=none` が明示されて一致し、runnerの `receiver` が明示的null、`receiver-samples.json` が存在して空配列、所有記録や終了・接続確認の証拠がないことを要求します。モード片方の欠落、矛盾、受信機情報、非空サンプル、runner失敗は無効です。

有効なnone条件も受信性能試験には分類しません。解析結果の `receiverMode=none`、`measurementKind=sender-control-without-owned-receiver`、`receiverCoverage=null` とし、送信側の対照測定として指標を保存します。所有受信機を起動しない条件であることと、他アプリの接続不存在を証明しないことを限界として明示します。

送信者との接続、画像の実受信、物理4K60表示は別途確認が必要です。GPU生成パターンの結果をmpvデコード込みの性能として扱いません。

## ネイティブ実行を伴わない検証

```powershell
python -m unittest discover -s scripts/GpuOutputProbeHarness -p "test_*.py" -v
```

一時ディレクトリに合成ログを作り、解析窓境界、60Hz計算、再送、コピー再試行の対、vblank の予測・待ち・bootstrap の規則、合成位相整列（補正の上限・オフセット再構成・予定時刻の単調性・command 照合・旧ログ不変）、エラー・欠落・途中終了の拒否、受信機の時間範囲、上書き拒否を検証します。`test_analyze_mutex_holds.py` は保持区間の要約を同様に検証します。GPU・受信機・本体アプリは起動しません。

## 任意の受信画像確認（性能測定とは別の短い試験）

`Capture-OwnedReceiver.ps1` はrunnerが起動して現在も動いている公式受信機のクライアント領域だけをPrintWindowで取得する補助です。runnerからは呼びません。受信機やプローブを新しく起動しません。性能測定中のキャプチャは測定負荷を変えるため、画像確認専用の短い試験で使ってください。

```powershell
& .\scripts\GpuOutputProbeHarness\Capture-OwnedReceiver.ps1 `
  -OwnedRecord '.\TestResults\gpu-output-probe\<現在実行中の画像確認用run>\receiver-owned.json' `
  -EvidenceRoot '.\TestResults\receiver-evidence' `
  -Samples 2 -IntervalMilliseconds 500 -ShowOwnedWindow
```

PID・プロセス生成時刻・実行ファイルのフルパスを照合し、そのPIDが所有するウィンドウが1つに特定できる場合だけ対象とします。`-ShowOwnedWindow` を省略すれば可視性を変更しません。指定した場合は必要に応じて自分の受信機だけを非アクティブで表示し、終了時に元の非表示・最小化状態へ戻す要求を非同期で送ります。他アプリのウィンドウは操作しません。

`EvidenceRoot` は上例のような短い専用パスを推奨します。日時・GUIDを含む長いrunディレクトリのさらに下へ入れ子にすると、Windowsや利用ツールのパス長制限に達しやすくなります。補助プロセスの作業ディレクトリはスクリプトの配置先に固定し、ログ・PNGへの絶対パスを渡します。証拠保存先の長さをプロセスの作業ディレクトリ制限に持ち込まないためです。

PrintWindowは[同期呼び出しで停止し得るAPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)なので、サンプルごとに専用の非表示PowerShellプロセスで実行します。既定8秒で戻らなければ、記録したPID・生成時刻・実行ファイルが一致するキャプチャ補助プロセスだけを終了し、それ以上の取得を止めます。受信機・プローブは終了しません。表示と復元には[ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync)を使い、受信機の応答を無期限に待ちません。

新規 `receiver-evidence-<UTC>-<GUID>` 配下に `receiver-client.png`、PrintWindow前後のQPC・画素サンプルの状態を含む `capture.json`、補助プロセス所有記録と `evidence-result.json` を保存します。戻り値がfalse、サンプル格子が単色・黒、例外・時間超過は明示して失敗扱いにします。全画面やデスクトップのスクリーンショットへの代替は行いません。

成功したPNGも、親が画像番号や動く目印を実際に確認するまでは接続・画像更新の証拠と断定しません。PrintWindowがtrueでも古い画像や空白を返す可能性があり、2枚の画像だけで60Hz受信や物理表示を保証することはできません。

## 実行環境の注意

runner と `Capture-OwnedReceiver.ps1` の Add-Type 内 C# は Windows PowerShell 5.1（C# 5 コンパイラ）でも動く宣言に揃えています（2026-09-10）。`out` 引数はインライン宣言を使わず事前に宣言してください。過去の run は PowerShell 7.6.5 で実行されており、`preflight.json` の `powerShell` で使用版を確認できます。

