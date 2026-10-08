# ERYTHEIA 試験基盤の束（2026-10、下書き）

v0.6.6 のレビューで見えた試験の抜けを直す束です。利用者の指示（2026-10-08「改善をしていないなら、してください」）を受けて、TSP-Fable が 7 項目と順を決めました。製品の版は上げません。配布物に影響する変更が出たときだけ、版の判断を TSP-Fable に諮ります。

## 0. 原則

- 製品のコード（src・native）は触りません。触るのは 6 で shim の表やファイルの選択の型を変える場合だけで、TSP-Fable の承認を受けてからにします
- 1 項目 1 ブランチ 1 担当で進め、main へは --no-ff で入れます。入れる前に TSP-Fable に 1 行で知らせます
- 実機の試験は直列に 1 本ずつ回します
- 項目ごとに、非E2E を全部と、変えた試験を回します

## 1. Debug の行に頼る確かめを無くす

- 事実（2026-10-08、main の読み）
  - アプリのログの最小の段階は、Debug のビルドでは Debug、Release のビルドでは Information です（`App.xaml.cs` の 91〜95 行）。src の `Log.Debug` は 20 ファイルに 69 か所あります。sync.gate の行はすべて Debug で、Information の sync.gate は 0 件です
  - E2E で Debug の行を判定に使っているのは 1 本です。`LtcInputMeterE2ETests.CableLoop_WhileLtcFlows_UiHeartbeatStaysOnTime` が `ui.heartbeat`（Debug）の end の行を 45 秒待ち、1 tick ごとの lateMs から中央値と最大を出しています。Release のビルドでは時間切れになります
  - Debug の行を出力だけに使っているのは `ScrubE2ETests` の DumpTail（`sync.gate seek-settled` ほか）です。判定には効きません
  - スクリプトでは、`run-timecodesyncplayer-diagnostics.ps1` が「Continue mode: waiting for file load stability」（Debug）を数えています（報告の表だけ）。「Timecode sync seek」の連発の数えには、Debug の「…seek suppressed」も入ります（`-TreatWarningsAsFailure` のときは WARN の理由になる）。`analyze-v054-gates.py` は sync.gate を集計しています（解析だけ）
  - ランナー（`run-ltc-scenarios.ps1`・`LtcRunMetrics.psm1`・`ltc-run-report.ps1`）は Debug の行に頼っていません
  - ついでに、src に出所が無く、いつ数えても 0 件の文字列が見つかりました
    - 試験の側: `LtcScenarioE2ETests` の「applying the first Jump frame」「applying the held value change frame」、L2 の「landing window closed without progress」
    - 診断のスクリプト: 「Continue mode: exiting gap」「frame-step not yet reflected」「SpoutOutput:」、引用符の無い「Timecode sync pending Settled」
- 直し
  - heartbeat の試験は、Information の「UI heartbeat summary: firstLateMs=… maxLateMs=… ticks=…」で判定する形にします。中央値は summary に無いので、判定を最大と ticks の 2 つにするか、summary に中央値を足すかを決めてください。足すなら製品のログの行の追加になり、版の判断が要ります。親の案は、判定を最大と ticks にすることです
  - DumpTail のキーは Information の行に替えます
  - 診断のスクリプトの数えは、型を Information の行（「Timecode sync seek ltc=」など）に絞ります
  - 出所の無い文字列は、src の今の行（「applying the confirmed Jump frame once」など）に直すか外します。直すときは、その確かめが今まで 0 件で通っていたこと（何も確かめていなかったこと）を、試験の名前と一緒に一覧で残します
- 確かめ: 開発機で、E2E の一式（LTC シナリオと L-3 は除く）を Release の exe（配布物と同じ作り、`TIMECODE_SYNC_PLAYER_E2E_APP_PATH`）で 1 回通します
- 以後の規則: 「E2E とランナーの確かめは Information 以上の行だけで行う」を CLAUDE.md と担当の定型に入れます。単体・結合の試験のメモリのシンク（Debug まで受ける）は対象外です

### 1 の実施（2026-10-08、ブランチ ti/1-debug-lines）

- heartbeat の試験の判定
  - 要約の行「UI heartbeat summary」は、MainWindow の構築から 30 秒の区間の終わり（reason=window）か、終了の手順（reason=closing）で 1 回だけ出ます（`UiHeartbeatRecorder.Window`）。試験は起動から 45 秒まで要約の行を待ちます
  - 事実: 要約の maxLateMs は、ほぼ常に起動の 1 回目の tick（firstLateMs）の値です。構築の直後に区間を始め、1 回目の tick がメッセージループの開始まで待つためです。開発機の手元のログ 230 件では、maxLateMs と firstLateMs が違う回は 0 件、maxLateMs の中央値は 838ms、1000ms 以上は 8 件でした。reason=window の回の ticks は 248〜267 でした
  - 判定は 2 つにしました。1 つ目は「maxLateMs が firstLateMs と同じ（最大は起動の 1 回目）か、1000ms 未満」、2 つ目は「reason=window かつ ticks が 200 以上」です。ticks の床と実測の下限の差は 48 tick で、約 5 秒の止まりにあたります
  - Release の構成では、受信中の 1 秒未満の止まりは見ていません（起動の 1 回目が最大になるため）。受信中の窓に限った数字（中央値・最大）も取れなくなりました。見えるのは、起動の 1 回目より遅い tick（1 つ目の判定）と、数秒の止まり（2 つ目の判定）だけです
  - 次の製品の版の材料: 要約に「起動の 1 回目を除いた最大（maxLateAfterFirstMs）と中央値」を出す案です。製品のログの行の変更なので、この束では入れません（棚卸し `v0.6.4-inventory.md` の #44）
- 置き換え（前 → 後）
  - `LtcInputMeterE2ETests` の heartbeat: `ui.heartbeat`（Debug）の seq と end の行 → 「UI heartbeat summary: …」（Information）
  - `ScrubE2ETests` の DumpTail のキー: `sync.gate seek-settled`（Debug）→ `Timecode sync pending`（Information。Settled と TimedOut の行）
  - 診断のスクリプトの「Continue mode: waiting for file load stability」（Debug）: Information の代わりが無いので、パターンの表と Continue Sync Health の行から外しました
  - 診断のスクリプトの「Sync seek bursts」: `Timecode sync seek` → `Timecode sync seek ltc=`。Debug の「…seek suppressed」と Information の「…seek gated」を数えなくなります。`-TreatWarningsAsFailure` では、抑止やゲートの行が多いだけの回は WARN（終了コード 1）にならなくなります。実際のシークの連発は今までどおり WARN です
  - `scripts/analyze-v054-gates.py`: 冒頭のコメントに「Debug のビルドのログが要る」を足しただけです（解析専用）
- 今まで何も確かめていなかった試験（src に出所が無く、いつ数えても 0 件だったもの）

| 試験（またはスクリプト） | 元の文字列 | 何を確かめていたつもりだったか | 直した先 |
|---|---|---|---|
| `LtcScenarioE2ETests` の `LandingLatencySecondsSince`（D38、記録だけ） | `applying the first Jump frame` | Jump の値がアプリに届いた時刻（最初の Jump の適用） | 外した（2026-09-19 のログが最後。今の適用の行「applying the confirmed Jump frame once」は同じ式に既にある） |
| `LtcScenarioE2ETests.L2` の l2-summary の `landingWindowClosed`（記録だけ） | `landing window closed without progress` | シークで追い付けずに着地の窓が閉じた回数 | 外した（v0.5.4 B6b の門の除去で行が無くなった。l2-summary の項目も外した） |
| `run-timecodesyncplayer-diagnostics.ps1` の性能の警告の文脈の分類 | `Continue mode: exiting gap` | 性能の警告がギャップの出口の直後かどうか | `Sync lifecycle: "GapExit"`（正規表現は `Sync lifecycle: "?GapExit"?`） |
| 同じスクリプトのパターンの表 | `frame-step not yet reflected` | コマ送りが位置に反映されない回数 | 外した（今のコマ送りの行は「FrameStep landed」で、反映されない回の行は無い） |
| 同じスクリプトの Spout Output Health とパターンの表 | `SpoutOutput:`（初期化・送信開始・SendImage false・SendFrame の例外・SpoutDX 欠落） | Spout の初期化と送信の失敗 | `SpoutDX.dll` の確認の Warning（`[WRN]` か `[ERR]` の後に `SpoutDX.dll`）だけを数える。他の行は今の src に無いので外した |
| 同じスクリプトの Continue Sync Health | 引用符の無い `Timecode sync pending TimedOut` / `Settled` | 同期のシークの保留の終わり方の数 | `Timecode sync pending "?TimedOut"?` / `"?Settled"?`（実際のログは引用符付き） |
| 同じスクリプトの性能の警告の分類 | `displayed FPS is below source FPS` | 表示の fps が素材の fps を下回った警告の数 | `FPS is below source FPS`（今の行は「full-resolution bitmap publication FPS is below source FPS」）。1 節の 7 つの外で、この洗い出しで見つけた |
| `test-timecodesyncplayer-diagnostics.ps1`（上のスクリプトの自己試験） | 作り物の行（`SpoutOutput: …`、引用符の無い `pending Settled`） | 上の分類が数えること | 今の src の行の形に直した（`pending "Settled"`、`SpoutDX.dll` の Warning） |

  - 訂正: 1 節の事実の「applying the held value change frame」は、出所があります。src の「Timecode sync: applying the {Reason} frame once」の Reason が "held value change" で、開発機のログにも 2026-10-03 まで出ています。試験はそのまま残しました
  - 直した確かめは、どれも記録か報告の表だけで、合否には効きません。直した後に急に落ちるようになる確かめはありません
- 確かめの結果（開発機、2026-10-08）
  - 非E2E の全件: 合格 3292、スキップ 2、失敗 0
  - Release の exe での E2E の一式（LTC シナリオと L-3 は除く）: 100 件のうち合格 83、スキップ 16、失敗 1。heartbeat の試験は合格
  - 落ちた 1 件は `PlaylistRowLookE2ETests.DraggingTheLastRowByTheGripToTheTop_ReordersAndOffsetBoxStillTakesClicks` で、つかみでの並べ替えの後の `WaitUntil` の時間切れ（「Condition was not satisfied before timeout.」）。ログの行を読まない試験で、Debug の exe でも同じく落ちたので、Debug の行とは別の理由（開発機でのドラッグの操作）です。この項目では直していません
- Release の構成で弱くなった確かめ: `LtcInputMeterHeartbeatE2ETests.CableLoop_WhileLtcFlows_UiHeartbeatStaysOnTime` は、LTC の受信中の 1 秒未満の止まりを見なくなりました（上の heartbeat の項）

## 2. 落ちの自動の集計

- ランナーの各回の後に、その回の時間の窓の中の、イベントログの .NET Runtime 1026（TimecodeSyncPlayer）とダンプの有無を集めます。run-result に「起動の数／落ちの数／ProRes を GPU で先頭に読んだ起動の数（起動から 2 秒以内に FetchMetadata の proresd3d11dec）」と、落ちの番地を出します
- 事前確認に、ダンプの環境変数（`DOTNET_DbgEnableMiniDump=1`・`DOTNET_DbgMiniDumpType=2`・出力先は ReportDir の dumps）を恒久で入れます。full のダンプ（1 GB 超）は、落ちを追うときだけにします
- 落ちが 1 回でもあれば、run-result の判定を「失敗」にします（今は落ちた試験が失敗するだけ）
- 検証機のランナーにも同じ変更を入れます（TSP-TestMachine にパッチを Taildrop で）

### 2 の実施（2026-10-08、ブランチ ti/2-crash-count）

- 実装: `scripts/run-ltc-scenarios.ps1`・`scripts/ltc-run-report.ps1`・`scripts/LtcRunMetrics.psm1`。単体は `scripts/test-ltc-crash-count.ps1`（作ったイベントログの行とアプリのログで、落ち 0、落ち 1、窓の外の 1026、ほかのアプリの 1026 を確かめる）
- 集め方: ランナーは試験の後（終わりから 3 秒以上たってから）、試験の開始からその時までの Application のイベントログの .NET Runtime 1026 と Application Error 1000 を、全アプリの分だけ `crash-events.json` に書きます。TimecodeSyncPlayer.exe の行に絞るのは集計の側です
- 1026 からは例外の番号と番地を、1000 からはモジュール・オフセット・プロセスの番号を取ります。1000 はプロパティの位置で読むので、表示の言語に左右されません。同じ落ちの 1026 と 1000（10 秒以内）は 1 件にまとめ、1026 の無い 1000 も 1 件と数えます
- run-result.json に足した項目
  - `crashes`: count、1026 と 1000 の数、窓、各件（時刻・番号・番地・モジュール・プロセスの番号・シナリオ・直前の起動の時刻と版・起動からの秒・その起動が ProRes を GPU で先頭に読んだか・ダンプのファイル名）、dumps（ReportDir\dumps のダンプの数と名前）
  - `launches`: 窓の中の起動の数（「=== TimecodeSyncPlayer v… 起動」の行、Information）
  - `proresGpuFirstLaunches`: 起動から 2 秒以内（次の起動の前）に「FetchMetadata: … V:proresd3d11dec」（Information）が出た起動の数
  - `verdict`・`failReasons`: 失敗した試験があれば `tests`、落ちが 1 回でもあれば `crash`。どちらかがあれば `fail`
- 割り当て: 各件は、時刻より前（ログの書き込みの順を見て 1 秒の余裕）の最後の起動と、trx の開始から終わりの間に時刻が入る試験に割り当てます。ダンプは `tsp-<プロセスの番号>.dmp`（1000 から）で、無ければ落ちの後 120 秒以内に書かれたものを当てます
- 判定: 落ちが 1 回でもあれば、ランナーの終了コードは 1 で、SUMMARY の行に `crashes=N`、落ちの各件を `CRASH …` の行に出します。イベントログを読めなかった回は `crashes=unknown`（count は null）で、判定は今までどおり試験の結果だけです。落ちのあった回は、合格でも output-trace を消しません
- ダンプ: 事前確認で `DOTNET_DbgEnableMiniDump=1`・`DOTNET_DbgMiniDumpType=2`・`DOTNET_DbgMiniDumpName=<ReportDir>\dumps\tsp-%p.dmp` を決めて runner-preflight.json の `dump` に書き、`dotnet test` の直前（ビルドの後）にランナーのプロセスの環境に入れます。試験のプロセスと、そこから起動するアプリが継ぎます。full のダンプは `-MiniDumpType 4` です
- 古い回（crash-events.json の無い回）を集計し直すと、`crashes.count` は null、起動の数は trx の開始〜終わりで数えます
- 確かめ（開発機、2026-10-08）: 自己試験 `scripts/test-ltc-crash-count.ps1` は全項目合格、既存の `scripts/test-ltc-run-metrics.ps1` も合格。非E2E の全件は合格 3292、スキップ 2、失敗 0。ランナーを S-1 の 1 本（ProRes の素材を先頭）で 1 回回し、合格 1、`crashes.count=0`、`launches=1`、`proresGpuFirstLaunches=1`、`verdict=pass` で、runner-preflight.json に `dump` が出ました。C: の空きが 20 GB に届かなかったため、この回だけ手元の写しで C: の確認を 15 GB に下げています（コミットしたランナーは 20 GB のまま）

## 3. 合否の範囲の決め方

- 規則: 数（relocate・holdEntries・3 つの和ほか）の範囲を、「3 回以上の回の最小〜最大」と「中央 ±2√N」の広いほうにします。v0.6.4 の棚卸しの #29 のとおり、50 前後の数で幅 3 は、√N の揺れ（約 7）より狭すぎます
- 開発機と検証機の手元の回（v0.6.5・v0.6.6、各 3 回以上ある数）から計算し直して、表を作ります。計算はスクリプトにします（手で決めない）。入力は回ごとの集計（`check.py` の出力か run-result）、出力は範囲の表（Markdown）です
- 上に外れたら止める・下は説明できれば記録、の読み方は変えません

### 3 の実施（2026-10-08、ブランチ ti/3-ranges）

- 実装: `scripts/compute-ltc-ranges.py`（標準ライブラリだけ）。自己試験は `scripts/tests/test_compute_ltc_ranges.py`（unittest）
- 使い方（例。入力はいくつでも、形は中身で見分けます）

```powershell
python scripts\compute-ltc-ranges.py <check の出力>... --kinds "std1=std,std2=std,heavy-a=heavy,prores1=prores,prores2=prores,l1-1=l1,l1-2=l1" `
    --exclude "check-a691b58-prores*=F-4・S-1 の落ちで数が欠けた" --title "開発機" --out ranges.md
python scripts\compute-ltc-ranges.py docs\design\test-infra-2026-10-ranges\test-machine-runs.json --title "検証機"
```

- 入力の形
  - run-result.json（ランナーの出力）: 読めるのは failed・prores.gpu・L-2 の maxFrameDeficitSeconds・crashes.count だけです。relocate や holdEntries は今の run-result に無いので、`metrics`（または `counts`）の辞書が足されたら読みます
  - check.py の出力のテキスト: 「##### <label>」の後の「  reloc: 35+17=52」「  hold: 87」「  back3: 33」「  bound: 2」「  stale: 0」「  pump: 0」「  assert_: 7」「  l2def: 0.003」「  l2gpu: 0.001」「  prores: {'gpu': 142, …}」の行
  - 手で作った JSON（`{"runs": [{"source", "label", "kind", "metrics"}]}`）。検証機の表はこの形で付録に置きました
- 回の名前は「<ファイル名>#<label>」で、`--exclude` はこの名前に当てます。種類の対応（`--kinds`・`--kinds-file`）は label へのワイルドカードです。失敗のあった回（check の result の 2 つ目が 1 以上、run-result の failed）は数が欠けるので自動で除き、除いた回と理由を出力に書きます
- 計算の細部
  - 中央 ±2√中央 は整数に外向きに丸めます（下端は切り捨てで 0 未満にしない、上端は切り上げ）
  - 「広いほう」は、下端と上端をそれぞれ広い側にとります（両方を含む範囲）。2 つが入れ子なら広いほうそのものです。今のデータで入れ子でなかったのは、検証機の std-bc の assertion と ProRes GPU、L-1 の relocate、prores-rtx の assertion の 4 つで、表の「範囲の元」に「両方の外側」と出ます
  - 回が 3 未満の種類は「回が足りない（N 回、範囲に使わない）」と出し、±2√中央 は参考に出すだけです
  - 小数の項目（L-2 の maxFrameDeficit・maxGpuDeficit）は √ を使わず、最小〜最大だけです
  - 今までの「整数は比べるとき ±1 まで可」は足しません（√ の幅が ±1 を含むため。中央が 0 の数だけは、この差で今より狭くなります。下の「狭くなる項目」）
- 入力にした回
  - 開発機（14 回）: v0.6.5 の std1・heavy-a・prores1、v0.6.6 の途中のビルド（a691b58）の std1・std2・heavy-a・l1-1、v0.6.6 の候補（91985cd）の std1・std2・heavy-a・prores1・prores2・l1-1・l1-2。種類は std1・std2 → std、heavy-a → heavy、prores1・prores2 → prores、l1-1・l1-2 → l1。a691b58 の prores1・prores2 は F-4・S-1 の落ちで数が欠けているので除きました。読んだ回の数は付録の `test-infra-2026-10-ranges/dev-runs.json`（`--dump-runs` の出力）にあります
  - 検証機（29 回）: v0.6.5（899ee68）と v0.6.6（91985cd）の固定の一式の表から手で写し、付録の `test-infra-2026-10-ranges/test-machine-runs.json` に置きました。種類は std-a（インストール版、トレース無し）、std-bc（RTX の zip の std b・c と、v0.6.6 の ffmpeg 無しの std）、prores-rtx（ProRes RTX と、v0.6.6 の gio の採取の回）、l1（L-1 の 6 素材）、a（A、1 時間）、amd-auto（内蔵 AMD の自動）、amd-off（内蔵 AMD で ProRes の GPU デコードを切った参考の回）。PR4 は数が全部 0 なので入れていません。ffmpeg 無しの std と gio の採取の回は、同期の数に効く条件が親の種類と同じとみて入れました（外すと std-bc は 4 回、prores-rtx は 2 回で回が足りない）

#### 開発機の範囲（新／今）

今は v0.6.6 の check.py の R の表（v0.6.4 の設計書 8-2 から、v0.6.5 の 9 節で std の holdEntries 87〜89・3 つの和 31〜33 に読み直したもの）です。今の整数の範囲は、比べるときに ±1 まで通しています。assertion の今は上限だけです。

| 項目 | std（5 回） | heavy（3 回） | prores（3 回） | l1（3 回） |
|---|---|---|---|---|
| relocate | 35〜65 ／ 50〜52 | 37〜67 ／ 51〜54 | 35〜65 ／ 50〜53 | 0〜7 ／ 2〜3 |
| holdEntries | 68〜106 ／ 88（87〜89） | 67〜105 ／ 88〜89 | 67〜105 ／ 85〜89 | 0 ／ 0 |
| 3 つの和 | 19〜43 ／ 32（31〜33） | 20〜44 ／ 32〜33 | 20〜44 ／ 32〜33 | 0 ／ 0 |
| boundary | 0〜5 ／ 2 | 0〜5 ／ 2 | 0〜5 ／ 2 | 0 ／ 0 |
| dropping stale | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| pump: held | 66〜104 ／ 81〜86 | 19〜43 ／ 31〜35 | 0〜1 ／ 1〜2 | 0 ／ 0 |
| assertion | 0〜10 ／ 上限 25 | 3〜17 ／ 上限 18 | 0〜10 ／ 上限 21 | 0〜7 ／ 上限 3 |
| ProRes GPU | 0 ／ – | 0 ／ – | 118〜166 ／ 134〜142 | 0 ／ – |
| reference-stale・recapture-failed | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| L-2 maxFrameDeficit（秒） | 0.000 ／ 0.000 | 0.003 ／ 0.003 | 0.000〜0.003 ／ 0.001〜0.003 | – |
| L-2 maxGpuDeficit（秒） | 0.000 ／ 0.000 | 0.001〜0.002 ／ 0.002 | 0.001〜0.002 ／ 0.001 | – |

観測の値（回の順）: std の relocate 50・50・50・50・51、holdEntries 87・83・86・87・88、3 つの和 31・31・31・33・33、pump 86・85・85・85・83、assertion 6・4・5・4・6。heavy の relocate 53・52・49、holdEntries 87・86・84、和 32・34・31、pump 33・31・30、assertion 10・9・11。prores の relocate 50・52・50、holdEntries 83・87・86、和 31・33・32、pump 0・0・1、assertion 4・6・5、ProRes GPU 142・142・142。l1 の relocate 3・3・5、assertion 3・3・4。

#### 検証機の範囲（新／今）

今は v0.6.4 の設計書 8-3 の表（v0.6.2+44cc043 の一式から）と、その後の決め（A の relocate 82〜89、std a 系の relocate 52〜67・3 つの和 33〜42、内蔵 AMD の relocate は記録だけ。v0.6.5 の 9-4）です。比べるときは整数 ±1 まで通しています。

| 項目 | std-bc（5 回） | prores-rtx（3 回） | l1（12 回） | a（4 回） |
|---|---|---|---|---|
| relocate | 38〜68 ／ 52 | 37〜67 ／ 52 | 0〜4 ／ 1〜4 | 64〜102 ／ 82〜89 |
| holdEntries | 69〜107 ／ 89〜90 | 70〜108 ／ 89 | 0 ／ 0 | 0 ／ 0 |
| 3 つの和 | 21〜45 ／ 33 | 21〜45 ／ 33 | 0 ／ 0 | 0 ／ 0 |
| boundary | 0〜5 ／ 2 | 0〜5 ／ 2 | 0 ／ 0 | 0 ／ 0 |
| dropping stale | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| pump: held | 0〜1 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| assertion | 4〜23 ／ 上限 39 | 18〜42 ／ 上限 76 | 0〜3 ／ 上限 1 | 0 ／ 上限 0 |
| ProRes GPU | 37〜94 ／ b 76・c 37 | 155〜211 ／ 183 | 0〜3 ／ 0〜3 | 11〜29 ／ 20 |
| reference-stale | 0〜12 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| recapture-failed | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 | 0 ／ 0 |
| L-2 maxFrameDeficit（秒） | – | – | – | 0.017〜0.027 ／ 0.020〜0.027 |
| L-2 maxGpuDeficit（秒） | – | – | – | 0.017 ／ 0.016〜0.017 |

- 回が足りない種類（範囲に使わない）: std-a は 2 回（relocate 67・67、holdEntries 88・87、3 つの和 42・42、pump 4・4、assertion 10・6）、amd-auto は 2 回（relocate 101・96、assertion 42・51）、amd-off は 1 回。std-a は今の範囲（relocate 52〜67、和 33〜42）のまま使い、あと 1 回で計算できます。amd-auto の relocate は利用者の決定どおり記録だけです
- std-bc の ProRes GPU は、std b と std c で素材が違う（76 と 37）ので、種類でまとめた範囲には意味がありません。素材ごとの値（b 76・c 37）で見ます
- std-bc の reference-stale 0〜12 は、v0.6.5 の std c の 12（記録だけにした回）が入ったためです。今の check.py は reference-stale と recapture-failed を 1 でも止めていて、計算の範囲とは読み方が違います

#### 今の範囲との違い

- 広くなる項目: 中央が 20 以上の数（relocate・holdEntries・3 つの和・std と heavy の pump・ProRes GPU・A の relocate）は、std-bc の ProRes GPU を除いてすべて ±2√中央 で決まり、今より大きく広がります。たとえば std の holdEntries は 88（87〜89）から 68〜106 に、A の relocate は 82〜89 から 64〜102 になります。boundary は 5 回とも 2 でしたが、中央 2 の ±2√2 で 0〜5 になります
- 狭くなる項目と理由
  - 中央が 0 の数（dropping stale、L-1 と A の holdEntries・3 つの和・boundary・pump、prores-rtx の pump）: 今は比べるときに ±1 で 1 まで通っていましたが、計算の範囲は 0 です。√0 が 0 で、最小〜最大も 0 のためです。reference-stale・recapture-failed は今の check.py でも 1 で止めているので、0 の種類では変わりません
  - 開発機の prores の pump: held: 今の 1〜2（±1 で 0〜3）から 0〜1 へ。v0.6.5・v0.6.6 の 3 回が 0・0・1 で、中央 0 の √ も 0 のためです。3 回だけの計算です
  - assertion の上限: 開発機の std 25 → 10、heavy 18 → 17、prores 21 → 10、検証機の std-bc 39 → 23、prores-rtx 76 → 42。今の上限は v0.6.3 の回の max × 1.5 で、ログの大きい回を含んでいました。v0.6.5・v0.6.6 の回は 4〜11（開発機）と小さく、√ の幅も小さいためです。assertion の数は v0.6.4 の 8-2 の注意どおり試験の操作の間隔に敏感で、記録だけの数です
  - 検証機の L-1 の relocate: 今の 1〜4（±1 で 0〜5）から 0〜4 へ。中央 1 の √ の幅が 0〜3 で、最大の 4（M4）が上端になるためです
  - A の L-2 maxGpuDeficit: 0.016〜0.017 から 0.017 へ。下側だけの違いで、下は記録の読み方なので止まりは変わりません
- TSP-Fable への材料（規則は変えていません）
  - holdEntries・3 つの和・boundary は試験の筋書きでほぼ決まる数で、観測の開き（最小〜最大の幅）は holdEntries で 1〜5、3 つの和で 0〜3、boundary で 0 でした。±2√中央 の幅（holdEntries で ±19 前後、boundary で ±3）はこれよりずっと広く、上に外れたら止める読み方では、今より大きな増えまで止まりません。√ の幅を当てる数を relocate・pump・A の relocate のような数え上げの揺れのある数に限るか、今の規則のまま全部に当てるかを決めてください
  - assertion は記録だけのままにするか（そうなら狭くなっても止まらない）、計算の範囲で止めるかを決めてください
  - reference-stale・recapture-failed は「1 でも止める」のままにするか、計算の範囲にするかを決めてください（std-bc は 0〜12 になる）
- 確かめ（開発機、2026-10-08）: 自己試験 12 件は合格（3 回で最小〜最大が広い場合、3 回で ±2√中央 が広い場合、入れ子でない場合、回が 2 の場合、小数の項目、中央 0、check の出力・run-result.json・手で作った JSON の読み、失敗の回と除外、Markdown の出力）。変えたファイルの制御文字の検査は全部 0 件。非E2E の全件は合格 3260、スキップ 37、失敗 0（この作業の作業ツリーで実行。スキップが 1・2・6 の実施の 2 件より多いのは作業ツリーの環境の差とみていますが、理由は確かめていません。この項目は C# の試験を変えていません）

## 4. 位置の測り方（先に影響の大きさを出す）

- 事実: 試験の `Position()`（`LtcScenarioE2ETests.cs` の 2147〜2151 行）は、UIA で TimeLabel の文字列を読み、タイムコードの文字列から秒に換えています
  - 呼び出しは 3 ファイルで 56 か所です。そのうち判定に使っているのは L1 の追従の監査、R1・R3・R5 の着地と保持、CheckHold（S2・C1・C2・G5・F4）、L2・L3 の標本で、残りは待ちと出力です
  - ほかにも、13 の E2E が TimeLabel を直接読んでいます
- 置き換えの先の候補
  - 出力のトレース（`TIMECODE_SYNC_PLAYER_OUTPUT_TRACE`）の `compose.acquire` の PtsNs です。合成に使ったフレームの位置で、E2EAppRunner はもう回ごとにトレースを分けています
  - `TryGetTimePos` は成功のときにログを出さないので、そのままでは使えません
  - 位置を持つ Information の行は、どれも事件のときだけ出ます
- 段階の案
  - 段 1: 判定に使う所（L1 の監査、R1・R3・R5、CheckHold）だけを、トレースの PtsNs で測る補助に替えます。待ちと出力は TimeLabel のままにします。トレースの無い構成（本番の構成の回）では、今の TimeLabel で測ります
  - 段 2: L2・L3 の標本
  - 段 3: そのほかの E2E
- 段 1 の差分の大きさと、トレースの有無で判定に渡る値が変わらないこと（試験の構成は本番と同じ、の規則）を先に TSP-Fable に出し、承認を受けてから着手します

## 5. まっさらな環境の一式

- 検証機に新しい Windows のユーザーを作り、PATH に ffmpeg・GStreamer が無く、設定が無い状態で、インストーラーで入れます。そこで標準のシナリオ 1 通りと、F7DurationWithoutFfprobeE2ETests を回します。これを固定の一式の恒久の 1 本にします
- ランナーは PATH に ffmpeg を足すので（`make-ltc-scenario-project.ps1` の 81 行目、PATH の末尾）、このユーザーではランナーに ffmpeg の場所を引数で渡し、アプリのプロセスの PATH には入らないことをログ（「ffprobe: not found on PATH」）で確かめます
- 手順は docs/verification-checklist.md に節を足して書きます

## 6. 形式の表と同梱の突き合わせ

- 事実
  - 同梱のプラグインは 19 個で、gstmatroska と gstavi は入っていません
  - shim の拡張子の表は、.mkv・.webm・.mka を matroskademux に、.avi を avidemux に回しています
  - アプリのファイルの選択の型は `*.mp4;*.mov;*.avi;*.mkv;*.mxf;*.ts;*.m2ts` です（`MainWindow.xaml.cs` の 1003・1016 行）
  - 公開文書（README の日英・USER-MANUAL・SETUP・settings）は、容器の対応を書いていません（コーデックだけ）
  - v0.6.6 のノートと CHANGELOG の既知の制限は、.mkv/.webm が開けないことだけを書いていて、.avi は書いていません
- 決めること（TSP-Fable へ）
  - (a) 文書の側で約束しない: 既知の制限に .avi を足し、ファイルの選択の型から .avi・.mkv を外します（型の変更は製品のコード）。shim の表は今のまま（開発機のフルの GStreamer では開ける）
  - (b) 同梱を足す: package-release に gstmatroska・gstavi を足します（配布物が変わり、ライセンスの一覧と版の判断が要る）
  - (c) shim の表から外す: 表に無い拡張子として「対応していない拡張子」を返します（製品のコード）
  - 親の案は (a) です。推奨の容器は mp4・mov で、ファイルの選択の型で選べるのに開けない、という見え方を無くすのがいちばん小さい直しです
- 試験: package-release の一覧（`$gstPluginDlls`）、shim の表（`kDemuxByExtension`）、ファイルの選択の型の 3 つを読み、型に出す拡張子の demux が同梱にあることを確かめる単体を足します（DLL の名前と demux の対応は表に持つ）

### 6 の実施（2026-10-08、ブランチ ti/6-formats）

- (a) の文書と試験の側だけを入れました。製品の振る舞いは変えていません。v0.6.6 のノートは触っていません
- 現場準備ガイド（docs/USER-MANUAL.md）に「1-6. 容器の対応」の表を足し、1 節の推奨の表に「容器」の行を足しました。開ける: .mp4・.mov（推奨）、.ts・.m2ts・.mts、.mxf。開けない: .mkv・.webm・.mka、.avi（同梱の GStreamer にその容器の demux が入っていない）。今のファイルの選択の画面では .mkv と .avi も選べること（「選べても開けません」）も書きました
- README（日英）の推奨の文に「容器は mp4 と mov」を 1 句足しました
- shim の表（`kDemuxByExtension`）の .mkv・.webm・.mka・.avi の行に、「同梱の GStreamer に matroskademux / avidemux は入っていない。開発機のフルの GStreamer では開ける」のコメントを足しました。表の中身は同じです。Debug のビルドの前後で DLL の SHA-256 は変わりましたが、違いは COFF のヘッダの時刻（4 バイトのうち 1 バイト）と PDB の参照の age（2→3）の 2 バイトだけで、.text・.data ほかの節は同じでした
- 試験: `ContainerSupportConsistencyTests`（非E2E、3 件）。package-release の一覧・shim の表・ファイルの選択の型・現場準備ガイドの 1-6 の表を読み、demux → DLL の対応は試験の中の表に持ちます
  - 型に出す拡張子のうち同梱に demux が無いものが、既知の .avi・.mkv と一致する（増えたら落ちる）
  - 現場準備ガイドの「開けない」が、shim の表で同梱に demux が無い拡張子と一致する（「開ける」は同梱に demux がある）
  - shim の表で同梱に demux が無い行に注記のコメントがある（同梱にある行には無い）
- 型から .avi・.mkv を外すのと、shim の表から外すかの決めは、棚卸しの #46 に回しました（次の製品の版）
- 確かめ（開発機、2026-10-08）: 新しい試験 3 件は合格。現場準備ガイドの「開けない」から .mka を消すと落ちることも確かめました（戻して一致を確認）。非E2E の全件は合格 3295、スキップ 2、失敗 0

## 7. 利用者の目で触る回

- docs/verification-checklist.md に、公開の条件として「版ごとに 1 回、初めて触る人が 15 分自由に操作し、画面の録画（音声つき）とアプリの logs の zip を渡す」を書きます
- 受け取り方は docs/design/field-feedback-2026-10-06.md の 5 節（音声は文字起こし、画面は 1 分ごとと指摘の時刻のコマ、録画の開始時刻とログの時刻の突き合わせ）です
- 公開の判断の前に、報告を整理して、直すか次の束かを利用者が決めます

## 7b. 項目を入れるたびの E2E（ドラッグの E2E の調べから、TSP-Fable 2026-10-08）

- 事実: v0.6.6 の R-13（LTCのメーター）の領域が、止めている間も Hidden で場所を取り、1080p で最大化したときにプレイリストの一覧が 222px から 191px に縮んだ。3 行目がはみ出し、つかむとフォーカスで 1 行スクロールして、先に入れた R-12 の並べ替えの E2E が開発機で落ちるようになった。R-12 の担当の 3/3 は R-13 の合流の前で、その後は開発機で一度も回っていなかった（ti/drag-e2e で試験の側を直した）
- 規則: 1 版に複数の項目を入れるときは、項目を入れるたびに非E2E を全部回すのに加え、見せ方に触れた項目を入れた後は、それより前に入れた項目の UI の E2E を回し直し、候補を作る前に E2E の一式（LTC シナリオ以外）を Release の exe で一度回する。CLAUDE.md と docs/SETUP.md の試験の節に入れた

## 8. 順と合否

- 順: 1 → 2 → 6 の決め → 3 → 5 → 7 → 4（4 は影響が大きいので、段 1 の案の承認を受けてから）
- 合否: 項目ごとに非E2E の全件が失敗 0 で、変えた試験が開発機で通ること。1 は Release の exe での E2E の一式、2 は検証機のランナーでも 1 回、5 は検証機のまっさらなユーザーでの 1 回

## 9. レビュー（TSP-Fable、2026-10-08）

- 承認。順は 1 → 2 → 6 → 3 → 5 → 7 → 4
- 1: heartbeat の試験は、Information の「UI heartbeat summary」の maxLateMs と ticks で判定します（製品のログは変えない、版の判断は不要）。src に出所の無い文字列（試験に 3 つ、診断のスクリプトに 4 つ）はこの項目で直します。原文を正しい行に直すか、確かめる相手が無ければ外します。「今まで何も確かめていなかった試験」の一覧（試験の名前、何を確かめていたつもりだったかを 1 行ずつ）をこの設計書に残します
- 2: ダンプは既定で MiniDumpType=2、落ちが 1 回でもあれば失敗。可
- 4: 段の案で可（段 1 は判定の所だけ、置き換えの先は出力のトレースの compose.acquire の PtsNs）。段 1 の差分の大きさ（試験の数と、変わる判定の一覧）を先に TSP-Fable に出します。本番の構成の回（トレース無し）は TimeLabel のままになります。将来、本番の構成でも位置をログに出す案は、製品の変更なので次の版の材料として棚卸しに 1 行
- 6: (a) で可。ただし製品のコード（ファイルの選択の型から .avi・.mkv を外す）は次の製品の版に回し、棚卸しに 1 行。この束で入れるのは文書だけです
  - v0.6.6 のノートは触りません
  - 現場準備ガイドに「容器の対応」の表を足します（mp4・mov・ts/m2ts・mxf は可、mkv/webm・avi は同梱の demux が無く開けない）
  - README（日英）の推奨の文に「容器は mp4 と mov」を 1 句足します
  - shim の表の .avi は、同梱に無い demux を指す行として表に注記します。表から外す（「対応していない拡張子」の行が出る）か注記だけにするかは、文書と合わせやすい方を 6 の設計で選びます（表から外すのは製品のコードなので、外すなら次の版）
  - 同梱に足す (b) は採りません
- 3・5・7: 可
