# 報告: v0.6.0 段 5b（試験素材の ffmpeg、ランナーの記録の追加、制御文字の検査）

担当: サブエージェント B。作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v060`（着手時 `cfaf89e`）。
指示書: `docs/prompts/2026-09-30-v060-stage5b-ffmpeg-runner.md`。制御文字の検査は親から追加で受けた 1 件（9 節）。
実機の試験（アプリの起動、E2E、LTC シナリオ、ランナー本体、`-PreflightOnly`）は行っていない。`-?` は使っていない。

## 1. コミット

| SHA | 件名 |
| --- | --- |
| `c0ec768` | test: 試験素材の ffmpeg の解決を 1 つにし、版を記録する（v0.6.0 段 5b の A） |
| `8dc095e` | test: ランナーの結果 JSON にコミットの空き・終了の秒数・ProRes の内訳を足す（v0.6.0 段 5b の B） |
| `4d36109` | build: 公開前の制御文字の検査を足す（v0.6.0 段 5b の追加） |
| （この報告） | docs: 段 5b の報告 |

## 2. A: 試験素材の ffmpeg

### 変更

- `scripts/TcsFfmpeg.psm1`（新設）: 解決の順は `TCS_FFMPEG`（ffmpeg.exe のフルパス。ファイルが無ければ止める）→ `-FfmpegDir` → PATH。
  ffprobe は `TCS_FFPROBE` → 解決した ffmpeg と同じフォルダ → PATH。ほかに版の読み取り、6 未満の警告、サイドカー、色タグの確かめ
- 版の番号: `ffmpeg version 4.2.3` / `n5.0` / `8.0.1-…` は先頭の数字。git のビルド（`N-109850-…`）は番号が無いので
  libavcodec の番号で決める（58 = 4、59 = 5、60 = 6、61 = 7、62 = 8）。開発機の `Program Files` の 2023 版は libavcodec 60 → 6
- `make-e2e-media.ps1`・`make-heavy-media.ps1`・`capture-setup.ps1`: 共有の解決に置き換え。出力の先頭 3 行が
  `ffmpeg-version: <-version の 1 行目> (major N)`、`ffmpeg: <パス> (<どこから>)`、`ffprobe: …`
  - `capture-setup.ps1` は引数 `-FfmpegPath`（既定 `ffmpeg`）を `-FfmpegDir`（既定なし）に変えた（呼び出し元は無い。`git grep` で確認）
  - `make-e2e-media.ps1`・`make-heavy-media.ps1` の `-FfmpegDir` の既定（`C:\Program Files\ffmpeg\bin`）は変えていない
- サイドカー `ffmpeg-version.txt`（素材のフォルダ）: 1 ファイル 1 行 `<名前> | <版の 1 行目> | made <UTC>`。
  既存の行は残す。作り直さずに再利用したファイルで行の無いものは `unknown (made before the version record)` を 1 回書く
  （既存の素材を今の ffmpeg の版と取り違えないため）
- 6 未満の警告: 新しく作るファイルがあるときだけ、その回に 1 回（`Write-Warning`）。全部再利用なら出ない
- `make-heavy-media.ps1` の ProRes（`prores_ks` profile 3、`yuv422p10le`、20 秒、音声なし）:
  - `heavy_e_4k5994_prores422hq.mov`（3840x2160、60000/1001、bt709 の 3 タグと `-movflags +write_colr`）
  - `heavy_f_1080p5994_prores422hq.mov`（1920x1080、同じ）
  - `heavy_g_4k5994_prores422hq_untagged.mov`（4K、タグも `write_colr` も付けない。フォールバックの確認用）
  - 作った直後に ffprobe で 3 タグを読み、タグ付きは 3 つとも `bt709`、タグ無しは 1 つも `bt709` でないことを確かめる。
    違えばそのファイルを消して止める（残すと次の回に「既存」として再利用されるため）
  - 絵は既存の heavy と同じ作り（頭・地・尻の色、秒の焼き込み、時間方向のノイズ）。ノイズのため 4K は 20 秒で約 4.4 GB
  - 追加の引数: `-Only`（名前のワイルドカード、`-Only heavy_e*,heavy_f*,heavy_g*` のカンマ区切りも可）、
    `-OmitColorTags`（タグの確かめが止まることを見るためだけのもの）
- `make-heavy-media.ps1` の既存の不具合を 1 件直した: `heavy_d_1080p30_ltc_ch1` は `.mov` で書くのに、既存の確認は
  `.mp4` の名前で見ていたので、毎回作り直していた。名前を `.mov` にした（指示書の「既存の素材は作り直さない」に合わせた）
- 試験: `tests/.../Helpers/FfmpegTool.cs`（新設）。`TCS_FFMPEG` → PATH（試験には `-FfmpegDir` に当たる引数が無い）、
  ffprobe は `TCS_FFPROBE` → 同じフォルダ → PATH。見つからなければ従来どおり名前で起動する。
  `AccuracyVideoFixture` は ffmpeg と ffprobe（各 2 か所）をここから取り、素材を作る前に journal へ
  `ffmpeg-version: …` の 1 行（6 未満なら `ffmpeg-warning: …` も）、素材のフォルダに `ffmpeg-version.txt` を書く
  （この素材は毎回新しく作るので、警告は毎回の判定）
- 単体の試験 `FfmpegToolTests`（解決の順 5、版の読み取り 5、警告 4、サイドカー 1）
- ランナーの事前確認の `ffmpeg is not on PATH` を共有の解決に置き換えた（`TCS_FFMPEG` だけで PATH に無い構成でも通る）。
  `prereqs:` の行に `ffmpeg=<版の 1 行目> (<どこから>)` を足した

既存の E2E の素材のファイルの中身は変えていない（既定の出力先 `artifacts\media`・`artifacts\media-heavy` には何も書いていない）。

### 素材の生成の結果（すべて作業ツリーの `artifacts\tmp-5b` へ。確かめた後に消した）

| 回 | `TCS_FFMPEG` | 記録された版（ログの先頭の行とサイドカー） | 結果 |
| --- | --- | --- | --- |
| t1 | PATH の先頭（ImageMagick 同梱） | `ffmpeg version 4.2.3 …`（major 4） | E2E の素材 10 本を新しく作成。警告 1 行。ffprobe は ImageMagick のフォルダに無いので PATH（`Program Files`）から |
| t2 | `C:\Program Files\ffmpeg\bin\ffmpeg.exe` | `ffmpeg version N-109850-g78f46065d8-20230212 …`（major 6、libavcodec 60） | 10 本を新しく作成。警告なし。ffprobe は同じフォルダ |
| t3 | t2 と同じ、出力先は t1 のフォルダ | 先頭の行は N-109850 | 10 本とも `skip (exists)`、警告なし。サイドカーは t1 の 4.2.3 のまま（作っていないので書き換えない） |
| t4 | t1 と同じ、サイドカーの無いフォルダ（t2 の素材の写し） | 先頭の行は 4.2.3 | 10 本とも再利用、警告なし。サイドカーは全行 `unknown (made before the version record)` |
| t6 | `Program Files` の版、`-Only heavy_f* -OmitColorTags` | N-109850 | `colour tags check failed (expected bt709): … primaries=unknown transfer=unknown space=unknown` で終了コード 1。作ったファイルは消えた |
| t5 | `Program Files` の版、ProRes の 3 本 | N-109850（3 本ともサイドカーに記録） | 3 本とも作成、タグの確かめを通過（下の表）。約 8.5 分 |
| t5b | PATH の先頭（4.2.3）、t5 のフォルダ | 先頭の行は 4.2.3 | 3 本とも再利用、警告なし、サイドカーは N-109850 のまま |

ProRes の 3 本（ffprobe、`Program Files` の ffprobe で読んだ値）:

| ファイル | codec / profile | 大きさ | pix_fmt | r_frame_rate | nb_frames | primaries / transfer / space | MB |
| --- | --- | --- | --- | --- | --- | --- | --- |
| heavy_e_4k5994_prores422hq.mov | prores / HQ | 3840x2160 | yuv422p10le | 60000/1001 | 1199 | bt709 / bt709 / bt709 | 4359 |
| heavy_f_1080p5994_prores422hq.mov | prores / HQ | 1920x1080 | yuv422p10le | 60000/1001 | 1199 | bt709 / bt709 / bt709 | 1097 |
| heavy_g_4k5994_prores422hq_untagged.mov | prores / HQ | 3840x2160 | yuv422p10le | 60000/1001 | 1199 | unknown / unknown / unknown | 4359 |

`TCS_FFMPEG` の優先: t1 と t2 で、PATH の先頭が同じ（ImageMagick の 4.2.3）まま、記録された版が指した方になった。
make-* の `-FfmpegDir` の既定（`Program Files`）より `TCS_FFMPEG` が勝つことも t1（4.2.3 が使われた）で確かめた。
C# 側の `TCS_FFMPEG` は単体の試験（偽の ffmpeg.exe のフォルダ）でだけ確かめた。`AccuracyVideoFixture` を実際に回すのは E2E なので行っていない。

## 3. B: ランナーの記録の追加

### 変更

- `scripts/run-ltc-scenarios.ps1`: 事前確認で `Win32_OperatingSystem.FreeVirtualMemory`（KB）を GB にして見る。4 GB 未満なら
  `PREREQ-ERROR system commit free is <N> GB, under 4 GB: …` を出して終了コード 2（ほかの事前確認と同じ扱い）。
  値は ReportDir の `runner-preflight.json`（`commitFreeGbAtStart`、`measuredAt`）に書き、`prereqs:` の行にも `commit_free_gb=`
- 試験の側: `E2EAppRunner.ExitNormally` が最初に閉じる要求を送った時刻と BtnExitNormal を押した時刻（UTC）を覚え、
  `DescribeExit(phase)` が `{ phase, exited, pressed, requestedAtUtc, pressedAtUtc, exitedAtUtc, seconds, waitedSeconds }` を返す
  （終わりは `Process.ExitTime`。起点は押した時刻、押せなかったときは閉じる要求の時刻）。
  `LtcScenarioE2ETests` の `VerifyAndExit`（15 秒待ち）の後に journal（`harness.jsonl`）へ `app-exit-timing` を 1 行。
  15 秒で終わらなかった回と事前確認だけで抜けた回は、`Dispose` の 10 秒待ちの後にもう 1 行（まだ終わっていなければ
  `seconds` は null、`waitedSeconds` がそこまでの待ち時間＝下限。この後 `Dispose` が kill する）。
  既存の `app-exit` の行と合否の判定（15 秒）は変えていない
- `scripts/ltc-run-report.ps1`（結果 JSON を作る側）に 3 項目を追加。集計の関数は `scripts/LtcRunMetrics.psm1`（新設）:
  - `commitFreeGbAtStart`: `runner-preflight.json` から（無ければ null）
  - `appExit`: `{ count, medianSeconds, maxSeconds, over15s: [ { test, seconds, exited, logLines } ] }`。シナリオごとに、終わりを
    見た最後の `app-exit-timing` を使う（無ければ最後の行）。`count`・中央・最大は終わりを見た回だけ。15 秒を超えた回と、
    終わりを見ないまま待ちが 15 秒を超えた回（`exited: false`、`seconds` は下限）を `over15s` に入れ、押す 1 秒前から
    消えた 1 秒後までのアプリのログの `終了手順…` の行（ExitCoordinator）と shim の `destroy: …` の行（tcs_player_destroy の段）を時刻順に添える
    （`exited` は指示書の形に足した項目）
  - `prores`: `{ gpu, cpu, adapterMismatch }`。`load.summary … profile=prores-gpu|prores-cpu` の数と、
    `load.fail … reason=decoder-adapter-mismatch`（段 2 の ProRes の確かめ）の数。D16-b の
    `load.skip … reason=decoder-adapter-mismatch`（H.264/HEVC のアダプタ別のクラス）は別の検査なので数えない。
    trx の `Times/@start` より前の行は数えない（下の 7 節の 1）
  - 既存の項目の名前と意味は変えていない。`schema` は `ltc-run-result/1` のまま。標準出力に `RESULT-5B …` の 1 行を足した
    （既存の `RESULT` の行は変えていない）
- `scripts/test-ltc-run-metrics.ps1`（新設）: %TEMP% に偽の ReportDir（trx、app-logs、4 シナリオの harness.jsonl と
  tcs-gst-raw.log、runner-preflight.json）を作り、`ltc-run-report.ps1` をそこへ実行して JSON を確かめ、最後に消す。
  アプリも試験も起動しない

### 集計の確かめ

- 自己試験 `pwsh -NoProfile -File scripts\test-ltc-run-metrics.ps1`: 19 項目すべて ok（`ALL OK`。初版で「18」と書いたのは数え違い。10 節の追加の後は 20 項目）。偽の成果物での出力:

```json
{
  "commitFreeGbAtStart": 18.52,
  "appExit": {
    "count": 3,
    "medianSeconds": 1.0,
    "maxSeconds": 17.5,
    "over15s": [
      { "test": "C-2", "seconds": 17.5, "exited": true,
        "logLines": [
          "2026-09-30 10:02:00.005 +09:00 [INF] 終了手順: 新規受付停止",
          "2026-09-30 10:02:00.006 +09:00 [INF] 終了手順: GStreamer 停止",
          "2026-09-30 10:02:00.030 [tcs-gst] destroy: enter",
          "2026-09-30 10:02:00.031 [tcs-gst] destroy: bus join begin joinable=1",
          "2026-09-30 10:02:16.899 [tcs-gst] destroy: exit elapsed_ms=16870.0",
          "2026-09-30 10:02:17.000 +09:00 [INF] 終了手順: 資源解放" ] },
      { "test": "C-3", "seconds": 25.1, "exited": false, "logLines": [] }
    ]
  },
  "prores": { "gpu": 2, "cpu": 1, "adapterMismatch": 1 }
}
```

- 既存の実行の成果物（`timecode-sync-player-v06` の `artifacts\analysis-data\pwsh-preflight`）は読むだけにし、
  scratchpad へ写してから `ltc-run-report.ps1` を当てた。既存の項目は写しの前の `run-result.json` と `label`（フォルダ名）以外すべて同じ。
  追加の 3 項目は `commitFreeGbAtStart: null`（runner-preflight.json が無い）、`appExit.count: 0`（この回の試験のコードに
  `app-exit-timing` が無い）、`prores` は 0/0/0（ProRes の素材なし）

## 4. 構文・バイト・試験

- 全 `.ps1`・`.psm1`（git 管理下と新設、37 本）を pwsh 7.6.6 の `Parser.ParseFile` で見て errors=0、制御文字 0、裸の LF 0（実行はしていない）
- 新設・変更した `.ps1`・`.psm1`・`.cs` はすべて CRLF。`package-release.ps1`・`ltc-run-report.ps1` の BOM は残した
- 途中で 1 回、Python の文字列で書いた `'ffmpeg\bin'` の `\b` が 0x08 に化けた（run-ltc-scenarios.ps1、コミット前に Parser とバイトの検査で見つけて直した）
- `scripts/inspect-gop.ps1`、`src/`、`native/gst-shim/src` は変えていない
- 非E2E（`--filter "FullyQualifiedName!~E2ETests"`）: **2852 件合格、失敗 0、スキップ 0**（3 つのコミットの後）

## 5. 親が実機で確かめる手順（ランナーを 1 周したとき）

1. `prereqs:` の行に `ffmpeg=<版> (<どこから>)` と `commit_free_gb=<N>` が出ること。`make-e2e-media.log` の先頭の行が `ffmpeg-version: …`
2. ReportDir の `runner-preflight.json` の `commitFreeGbAtStart`
3. `run-result.json`:
   - `commitFreeGbAtStart` が 2 の値
   - `appExit.count` が正常終了したシナリオの数（LtcScenario の 26 前後。LtcHardwareLoop はアプリを kill するので入らない）、
     `medianSeconds`・`maxSeconds`（v0.5.4 の検証機の集計は 63 回で最大 0.18 秒）。`over15s` が空か、中身に終了の段の行があること
   - `prores`: ProRes の素材の回で `gpu`/`cpu` の数、`TCS_FORCE_DECODER_LUID_MISMATCH=1` の回で `adapterMismatch` が増えること
   - `RESULT-5B` の行と一致すること
4. `harness.jsonl` に `app-exit-timing`（`phase: verify`）が 1 シナリオ 1 行
5. `-PreflightOnly` の回では各シナリオの `Dispose` の行（`phase: dispose`）で `appExit` が数えられる

## 6. 利用の注意

- 開発機で ProRes を作るときは `TCS_FFMPEG` を `Program Files` の 2023 版（major 6）へ向ける。make-* は既定の `-FfmpegDir` でも同じ版になる
- 重い素材の ProRes 3 本は合計約 9.8 GB。`-Only heavy_e*,heavy_f*` で必要な分だけ作れる

## 7. 未解決・判断が要る点

1. **既存の集計が日のログ全体を数えている**: `app-logs` へは日単位のログファイル（`timecodesyncplayer-YYYYMMDD.log`・`tcs-gst-YYYYMMDD.log`）を
   丸ごと写しているので、`ltc-run-report.ps1` の既存の `zeroCapsLoads`・`fastLoads`・`errFtl`・`previewStalled` などは、同じ日の
   それより前の回の行も数えている（pwsh-preflight の写しでは、app-logs の tcs-gst のログに 13:58 からの行があり、この回の trx の開始は 15:16）。
   ランナー本体の `SUMMARY err_ftl=` は開始時刻で絞っている。既存の項目の意味は変えない指示なので触っていない。追加の `prores` だけ trx の開始で絞った
2. **C: の空き 20 GB の確認はランナーに無い**（`git grep` で該当なし）。「同じ扱い」は、コミットの空きをランナーの事前確認（PREREQ-ERROR、終了コード 2）に入れる形にした。C: の空きも入れるかは親の判断
3. `TestVideoFactory.cs`（非E2E の一部が使う）も `ffmpeg` を名前で呼んでいる。指示書の対象に無いので変えていない
4. `AccuracyVideoFixture` の解決の順の変更で、開発機では `TCS_FFMPEG` が無い限り従来どおり PATH の先頭（4.2.3）を使う。
   設計書の TSP-Fable の補足 (a)（切り替えの直後に両機で非E2E と E2E を 1 回ずつ）は親の実機の手順
5. 終了の秒数の起点は UIA の `Invoke` の直前の時刻（押す操作の遅れを含む側）。`Invoke` が例外になった回は閉じる要求の時刻になる
6. ProRes の heavy は既存の heavy_a〜c と同じ色の組を使っている（e = a、f = b、g = c）。1 つのプレイリストに混ぜると絵での判別ができない。
   ProRes だけで組む（`-Media` で e/f/g を選ぶ）前提

## 8. ツリーに残したもの

- `artifacts\tmp-5b` は消した（素材の生成の試し。約 10 GB）。ほかに gitignore の下へ書いたものは `bin`・`obj` のビルドだけ
- 試しのログと pwsh-preflight の写しは scratchpad（リポジトリの外）

## 9. 追加: 公開前の制御文字の検査（親から、TSP-Fable の判断）

- `scripts/check-control-chars.ps1`（新設、`#requires -Version 7.0`、CRLF）: `git ls-files` の `*.md`・`*.txt` は
  `[\x00-\x08\x0b\x0c\x0e-\x1f]` とタブ、`*.ps1`・`*.psm1` はタブ以外の同じ範囲を探す（CR は改行の一部として許す）。
  見つかれば `<ファイル>:<行>: 0x<コード>` を出して終了コード 1、無ければ 0。最後に `control-chars: files=N hits=M`。
  `-Path`（カンマ区切り可）で対象を渡せる。バイトを Latin-1 で読むので UTF-8 の多バイト文字は当たらない。371 ファイルで約 4 秒
- `scripts/package-release.ps1`: `$projectRoot` を決めた直後（版の読み取り・ビルドより前）に呼び、1 件でもあれば throw で止まる。
  package-release 自体は実行していない（ビルドとダウンロードに進むため）。構文は Parser で errors=0
- `docs/RELEASE-PROCEDURE-0.4.md` の 0 節の表に `6b` として 1 行（6 の grep の次）
- 確かめ:
  - `v0.6.0` の先頭（`89a14dd`）の `*.md`・`*.txt`・`*.ps1` 371 本を `git archive` で scratchpad へ出して `-Path` で検査 → **hits=0、終了コード 0**
  - **このブランチ（`agent-b-v060`）では hits=69**: `cfaf89e` は親の修正より前なので、`docs/PARENT-HANDOVER-2026-09-12.md`・
    `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`・`docs/design/v0.6.0-prores-gpu.md`・`docs/RELEASE-PROCEDURE-0.4.md` 64 行目ほかに残っている。
    このブランチでは直していない（`v0.6.0` 側で直っているので、取り込みで解消する想定。取り込みの後に 1 回実行して 0 件を確かめること）
  - scratchpad に SETUP.md の写しを置き、3 行目にタブ、5 行目に 0x08 を入れて検査 → `tab-copy.md:3: 0x09`・`tab-copy.md:5: 0x08`、終了コード 1。
    タブを含む `.ps1` は 0 件（タブは許す）

## 10. 追加 2 点（親の判断を受けて）

親の判断: 7 節の 1（取り込み後に親が 0 件を確かめる）・2（既存の項目の意味は変えない、負債として親が記録）・
5（今のまま）。以下はその後の追加。実機・ランナー本体・E2E は回していない。

| SHA | 件名 |
| --- | --- |
| `fce28db` | test: ランナーの事前確認に C: の空き 20 GB を足す（v0.6.0 段 5b の追加 A） |
| `55ac480` | test: TestVideoFactory も試験の ffmpeg の解決と版の記録にそろえる（v0.6.0 段 5b の追加 B） |
| （この追記） | docs: 段 5b の報告に追加 2 点を追記 |

### A. C: の空き 20 GB（`fce28db`）

- `run-ltc-scenarios.ps1` の事前確認で `[IO.DriveInfo]::new('C:\').AvailableFreeSpace` を GB で見る。20 GB 未満なら
  `PREREQ-ERROR free space on C: is <N> GB, under 20 GB: clean artifacts before the run`（終了コード 2、コミットの空きと同じ書き方）。
  `prereqs:` の行に `c_free_gb=`
- 値は `runner-preflight.json` の `cDriveFreeGbAtStart` と、`run-result.json` の `cDriveFreeGbAtStart`（追加だけ）。`RESULT-5B` の行に `c_free_gb=`
- `LtcRunMetrics.psm1` に `Get-TcsDriveFreeGb`・`Get-TcsRunnerPreflightValue`
- 自己試験に 1 項目（`cDriveFreeGbAtStart from runner-preflight.json`）。20 項目すべて ok
- 関数を単体で呼んだ値（15:50 ごろ、開発機）: C: の空き 47.16 GB、コミットの空き 18.51 GB（どちらも始められる側）
- 構文: 全 `.ps1`・`.psm1` 37 本 errors=0、制御文字 0、裸の LF 0

### B. TestVideoFactory（`55ac480`）

- `FfmpegTool`（試験のヘルパー）の順を `TCS_FFMPEG` → 既定のフォルダ（`%ProgramFiles%\ffmpeg\bin`、スクリプトの `-FfmpegDir` の既定と同じ）→ PATH にした。
  `AccuracyVideoFixture` も同じヘルパーなので同じ順になる
- `TestVideoFactory` は `FfmpegAvailable`・`GetOrCreate`・`GetOrCreateVariant` の ffmpeg を `FfmpegTool` から取り、作った素材の版を
  素材のフォルダ（`TestTempPaths.Root`）の `ffmpeg-version.txt` に同じ形で書く。6 未満なら警告を標準エラーへ 1 回
  （このクラスには journal が無いため）
- `FfmpegToolTests`: 既定のフォルダが PATH より勝つ試験を足し、`TCS_FFMPEG` が既定のフォルダにも勝つことを確かめる形にした
- 非E2E: **2853 件合格、失敗 0、スキップ 0**。`TestVideoFactoryTests`（非E2E、実際にエンコードする）の後のサイドカー:
  `test_clip.mp4 | ffmpeg version N-109850-g78f46065d8-20230212 …`、`test_clip_alt.mp4 | 同じ`
- **挙動の変化（親が知っておくこと）**: `TCS_FFMPEG` が無いとき、試験の ffmpeg は PATH の先頭から既定のフォルダへ変わる。
  - 開発機: 4.2.3（ImageMagick 同梱）→ 2023 版（N-109850、major 6）
  - 検証機（設計書 7 節の事実による。確かめていない）: scoop の 8.0.1 → `Program Files` の n5.0（major 5 なので警告が出る）。
    設計書の補足 (b) のとおり、検証機の既定のフォルダを 8.0.1 へ向けるか、検証機で `TCS_FFMPEG` を 8.0.1 に設定しないと、
    検証機の試験は今より古い ffmpeg で素材を作る
  - 設計書の補足 (a): 切り替えの直後に両機で非E2E と E2E を 1 回ずつ回し、参照フレームの判定などの既存の合否の基準がそのまま通るかを見る（親の実機の手順）

## 11. 追加: ランナーの既定から L-1・L-3 を外す（`44ccb6f`）

制約（親が実機の一式を回している）に従い、dotnet build・dotnet test・shim のビルド・素材の生成はしていない。
行ったのは編集、Parser.ParseFile、`check-control-chars.ps1`、`test-ltc-run-metrics.ps1` だけ。**非E2E は回していない**（親が取り込みの後に回す）。

### 変更（`scripts/run-ltc-scenarios.ps1`）

- 既定の絞り込み: `(FullyQualifiedName~LtcHardwareLoopE2ETests|FullyQualifiedName~LtcScenarioE2ETests)&FullyQualifiedName!~L1_&FullyQualifiedName!~L3_`。
  `-IncludeL1`・`-IncludeL3`（switch）で、それぞれの除外の項を付けない。試験の名前で `L1_`・`L3_` で始まるのは
  `L1_Single_ContinuousFollow_DoesNotStall` と `L3_ProductionDay_KeepsOneProcessStableAcrossRehearsalBreakAndShow` の 2 つだけ（`tests` を grep）
- `-Filter` を明示したときはその値をそのまま使う。ただし正の項（`!` を含まない項）が `L3_` か `ProductionDay` を名指し、`-IncludeL3` が無いときは
  `PREREQ-ERROR the -Filter names L-3 (L3_, 12 hours): pass -IncludeL3 as well to run it`（終了コード 2）
- 絞り込みの決定を事前確認（`prereqs:` の行の直後）へ移し、1 行 `prereqs: filter=<最終の値> given=<True|False> l1=<yes|no|possible> l3=<…>` を出す。
  後段の既存の `filter=` の行と、ltc-run-report.ps1 に渡す `-Filter`（結果 JSON の `filter`）は最終の値のまま
  - 既定のとき: `l1`・`l3` は switch で決める（yes / no）
  - 明示のとき: 項を読む。正の項が名指し → yes、否定の項で除外 → no、正の項がクラス全体（`~LtcScenarioE2ETests` で終わる）か Category → possible、ほか no。
    フィルターの評価ではなく項の読み取り（例: `Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests` は実際には含まないが possible と出る）
- 先頭の使い方のコメント: 新しい既定、`-IncludeL1`・`-IncludeL3`、L-1 の例に `-IncludeL1` を付けた。「前の 22 シナリオ」のフィルタの例は既定と同じになったので削った。
  「空振りで確かめる」という注記を「実行しないで確かめる（Parser.ParseFile、check-control-chars.ps1）。`-?` は使わない」に直した

### 確かめ

- 構文: 全 `.ps1`・`.psm1` 37 本 errors=0、制御文字 0、裸の LF 0。`check-control-chars.ps1 -Path scripts/run-ltc-scenarios.ps1` は hits=0
- 判定の関数だけを AST から取り出して 8 通りの絞り込みで確かめた（スクリプト本体は実行していない）: 既定（両方除外）→ no/no、
  `…!~L3_` だけ → possible/no、除外なしのクラス全体 → possible/possible、`~NoSuchTest` → no/no、`~L3_ProductionDay` → no/yes、
  `~LtcScenarioE2ETests.L1_` → yes/no、旧来の「22 シナリオ」の例 `~LtcScenarioE2ETests&!~L1_` → no/possible、上の Category の例 → possible/possible。8 通りすべて期待どおり
- `test-ltc-run-metrics.ps1`: `ALL OK`（20 項目）

### 親が知っておくこと

- **L-1 は既定で走らなくなった**。合否の一式の「L-1 6 回」をランナーの既定（`-Filter` なし）で回していたなら、`-IncludeL1` を付ける必要がある。
  `-FollowSeconds` などの `-Follow*` は、L-1 が絞り込みに入っていないと効かない（警告は出さない）
- 明示の `-Filter` がクラス全体（例: 旧来の「22 シナリオ」の `FullyQualifiedName~LtcScenarioE2ETests&FullyQualifiedName!~L1_`）のときは、L-3 を含むのに止まらない
  （指示どおり止めるのは L3_ を名指す指定だけ）。`prereqs: … l3=possible` の行で分かる。これも止めるかは親の判断
- `dotnet test --filter` の括弧のまとめ（`(A|B)&C`）は VSTest の書式どおりのはず。実機では確かめていないので、一式の最初の回で
  `dotnet-test.log` の実行件数が L-1・L-3 を除いた数になっていることを見ること

## 12. 追加: l3=possible でも止める（`a544160`）

親の判断: l3=possible でも止める。L-1 は `-IncludeL1` か `-Filter FullyQualifiedName~L1_` で明示する（親の駆動は後者）。
括弧の書式は親が取り込み後の最初の回で確かめる。制約は 11 節と同じ（ビルド・試験・素材の生成なし）。

### 変更（`scripts/run-ltc-scenarios.ps1`）

- 11 節の「項の読み取り」をやめ、明示の `-Filter` を L-1・L-3 の試験（FullyQualifiedName・Name・Category=E2E）に当てて評価するようにした。
  `~` は含む（大小を区別しない）、`=` は等しい、`!` は否定、`&` が `|` より先、括弧。ほかの属性の項・読めない形の項は当たるものとして扱う（走りうる側に倒す）
  - 選ばない → `no`、選び、正の項が名指す（`L3_`・`ProductionDay`／`L1_`・`ContinuousFollow`）→ `yes`、選ぶが広い項（クラス全体、Category など）による → `possible`
- 止める条件: `-Filter` を明示し、`-IncludeL3` が無く、`l3` が `no` でない（`yes` と `possible` の両方）。終了コード 2。メッセージ:
  `the -Filter may run L-3 (L3_ProductionDay, 12 hours; l3=possible). Either add '&FullyQualifiedName!~L3_' to the -Filter, or pass -IncludeL3 to run it`
- 既定の絞り込み（`-Filter` なし）は止めない（`-IncludeL3` が無ければ `!~L3_` が付く）
- 先頭の使い方のコメントを合わせた（L-1 だけを走らせる 2 つの書き方も 1 行）

### 確かめ（関数を AST から取り出した判定の試験、スクリプト本体は実行していない）

17 通りすべて期待どおり（`bad=0`）。主なもの:

| -Filter | l1 | l3 | -IncludeL3 | 止まる |
| --- | --- | --- | --- | --- |
| 既定（両方除外） | no | no | なし | いいえ |
| 除外なしのクラス全体 `(…LtcHardwareLoopE2ETests\|…LtcScenarioE2ETests)` | possible | possible | なし | **はい** |
| 同じ | possible | possible | あり | いいえ |
| `FullyQualifiedName~L3_ProductionDay` | no | yes | なし | **はい** |
| `FullyQualifiedName~L1_` | yes | no | なし | いいえ |
| 旧来の `FullyQualifiedName~LtcScenarioE2ETests&FullyQualifiedName!~L1_`（L-3 に 15 分入った形） | no | possible | なし | **はい** |
| 上に `&FullyQualifiedName!~L3_` を足したもの | no | no | なし | いいえ |
| `Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests` | no | no | なし | いいえ（11 節では possible と出ていたもの） |
| `Category=E2E` / `Priority=1`（知らない属性） | possible | possible | なし | **はい** |
| `(FullyQualifiedName~S1_\|FullyQualifiedName~C1_)&Category=E2E` | no | no | なし | いいえ |

- 構文: 全 `.ps1`・`.psm1` 37 本 errors=0、制御文字 0、裸の LF 0。`check-control-chars.ps1 -Path scripts/run-ltc-scenarios.ps1` は hits=0
- `test-ltc-run-metrics.ps1`: `ALL OK`
- 判定の試験は scratchpad の使い捨て（リポジトリには入れていない）。非E2E は回していない（親が取り込み後に回す）
