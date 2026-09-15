# V11（decodeMode: hardware / software）検証手順

対象: `docs/V04-SCOPE-mpv-removal-decode-mode.md` 5 節の V11-a〜V11-d。
判定は親と利用者が行う。ここには**回し方・素材・設定の与え方・ログの見方**だけを書く。

前提:

- 実機は 1 本ずつ。自分の PID のみ終了。開始前に一声かける。
- 各 run は別プロセスなので、`decodeMode` の「再起動が必要」は run ごとに満たされる
  （`settings.json` はプレイヤー生成時に 1 回読まれる）。
- ここで使う manifest は `Run-V1Matrix.ps1` → `Invoke-AppGpuTrial.ps1` の既存機構
  （50 秒、Spout ON、公式受信機、DISPLAY2 全画面、GStreamer、exit ダイアログ Normal）。
- 開発 worktree のアプリは**システムの公式 GStreamer**（`C:\Program Files\gstreamer\1.0\msvc_x86_64`）
  を読む。V11-a/b/d はその前提でよい（同梱ランタイムの経路は V11-c で確認する）。

---

## 0. 共通の準備

### 0-1. ビルド

```powershell
# アプリ（pkg worktree の Debug）
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj

# shim（V11 の decodeMode 入り。native\tcs_gstreamer.dll へ配置）
powershell -File native\gst-shim\build-shim.ps1 -Config Debug
Copy-Item native\gst-shim\build-debug\tcs_gstreamer.dll native\tcs_gstreamer.dll -Force
```

実行ファイル: `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe`

### 0-2. 素材（V1 の 11 本）

素材の実体は現状 `C:\Users\<user>\Documents\timecode-sync-player-wt-integrate-20260912\artifacts\media\v1`
にある。

| clip | コンテナ | hardware での期待デコーダ（V1 実測） |
| --- | --- | --- |
| `v1_h264_1080p23976.mp4` | qtdemux | d3d11h264dec |
| `v1_h264_1080p25.mp4` | qtdemux | d3d11h264dec |
| `v1_h264_1080p2997.mp4` | qtdemux | d3d11h264dec |
| `v1_h264_1080p5994.mp4` | qtdemux | d3d11h264dec |
| `v1_h264_1080p60.ts` | tsdemux | d3d11h264dec |
| `short\v1_h264_1080p60.mxf`（10 秒素材） | mxfdemux | d3d11h264dec |
| `v1_h265_1080p60.mp4` | qtdemux | d3d11h265dec |
| `v1_h265_10bit_1080p60.mp4` | qtdemux | d3d11h265dec |
| `v1_h265_4k60.mp4` | qtdemux | d3d11h265dec |
| `v1_h264_1080p60_aac.mp4` | qtdemux | d3d11h264dec（音声あり） |
| `v1_prores422_1080p60.mov` | qtdemux | avdec_prores（CPU。GPU profile が無いコーデック） |

注意:

- `Run-V1Matrix.ps1` は `-MediaDir` の**トップレベルだけ**走査する。MXF は `short\` にあるので、
  まとめて回す場合は 11 本を 1 つの作業ディレクトリへコピーする（例:
  `artifacts\media\v11\` を作ってコピー）。または MXF だけ `Invoke-AppGpuTrial.ps1` で個別に回す。
- MXF は 10 秒素材なので、個別に回す場合は `-Seconds 10`、集計窓は `v1_matrix_summary.py <root> 2 9`。
- `v1_prores422_1080p60.mov` は**長さが 30 秒**。50 秒 run の既定窓 `[8,48)` で集計すると 30 秒以降に
  フレームが来ず `dist/s` の下限が落ちる（例: `25..60`）。**ProRes は窓 `[8,28)` で集計する**
  （V1 の記録もこの窓）。`v1_matrix_summary.py` は root 単位で窓を取るので、ProRes の run だけ
  別 root へ置いて（run ディレクトリのコピーまたは junction で）`8 28` を渡す。

### 0-3. decodeMode の与え方

`Invoke-AppGpuTrial.ps1` に `-DecodeMode`、`Run-V1Matrix.ps1` に `-DecodeMode` / `-LabelPrefix` を追加済み。

- **hardware（既定の非回帰）**: `-DecodeMode ''`（空）。`settings.json` に `decodeMode` を**書かない**。
  shim 側も既定の hardware のまま。
- **software**: `-DecodeMode software` → `settings.json` に `"decodeMode":"software"` が入り、
  プレイヤー生成時に shim へ `TCS_DECODE_MODE_SOFTWARE` が渡る。

### 0-4. ログの見方（全 V11 共通）

| 見るもの | 場所 | 読み方 |
| --- | --- | --- |
| run の成否 | run ディレクトリの `runner-result.json` | `error` が空、`appExit=0`、`decodeMode` に指定値 |
| 使用デコーダ | `app\summary.json` の `sourceDiagnostics.decoder`、または `app-log-tail.txt` の `loaded (... / profile N) ... decoder=<name>` | profile 番号も見る（hardware: 0〜3 が GPU、software: 4〜8 が CPU） |
| software のフォールバック警告 | `app-log-tail.txt` / `app-stderr.txt` | `software decode requested but no CPU decoder was usable; using hardware profile <name>` |
| 実フレーム数/秒 | `app\events.jsonl` の `source.acquire` で `detail=Ready` の imageId を 1 秒ごとに数える | `v1_matrix_summary.py` の `dist/s` 列（`min..max`） |
| 表示 | `present.scanout` を 1 秒ごとに数える | 同 `minPr` 列（59 以上なら 60Hz 表示に追従） |
| Spout | `send.publish` を 1 秒ごとに数える | 同 `spout` 列 |
| 合成 p99 | `compose.start` → `compose.complete` | 同 `cp99` 列（ms） |
| CPU（プロセス全体） | `app\summary.json` の `appCpuSeconds` / `(appCpuEndQpc-appCpuStartQpc)/1e7` | 同 `cpu` 列（平均占有コア数の近似） |
| エラー | `events.jsonl` の `error` ステージ件数、`app-log-tail.txt` の `[ERR]`/`[FTL]` | 同 `err` 列は 0 が期待 |

集計は `scripts\GpuOutputProbeHarness\v1_matrix_summary.py`:

```powershell
python scripts\GpuOutputProbeHarness\v1_matrix_summary.py <LogRoot> 8 48
```

50 秒 run の既定の解析窓は `[8,48)` 秒。`dist/s` が素材 fps ±1 なら実フレームが追従している。

---

## V11-a: `hardware` 既定の非回帰

1. 素材 11 本を 1 ディレクトリへ集める（0-2）。
2. 行列を回す（`-DecodeMode` を**指定しない**）:

```powershell
$app = 'C:\Users\<user>\documents\timecode-sync-player-wt-pkg-20260915\src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe'
$media = 'C:\Users\<user>\documents\timecode-sync-player-wt-pkg-20260915\artifacts\media\v11'
& scripts\GpuOutputProbeHarness\Run-V1Matrix.ps1 -MediaDir $media -AppExe $app -LogRoot '.\TestResults\v11-a' -Seconds 50
```

3. 集計:

```powershell
python scripts\GpuOutputProbeHarness\v1_matrix_summary.py TestResults\v11-a 8 48
```

読み方（V1 との比較）:

- `decoder` 列が 0-2 の表の**期待デコーダと一致**する（ここが V11-a の主指標）。
- `dist/s` が素材 fps ±1（1080p）、`minPr` ≥ 59、`err=0`、`exit=0`。
- `cp99` は V1 実測の水準（約 0.8〜1.0ms）と同程度か。
- MXF を個別に回した場合は、その run だけ別 root で集計し `2 9` 窓にする。
- ProRes は 0-2 の注記どおり `[8,28)` 窓で別途集計する（既定窓だと 30 秒以降が空白になる）。

---

## V11-b: `software` での再生能力（達成 fps と CPU）

1. 同じ 11 本を software で回す（hardware とは別の LogRoot にする）:

```powershell
& scripts\GpuOutputProbeHarness\Run-V1Matrix.ps1 -MediaDir $media -AppExe $app `
    -LogRoot '.\TestResults\v11-b' -Seconds 50 -DecodeMode software -LabelPrefix v1-sw
```

2. 集計:

```powershell
python scripts\GpuOutputProbeHarness\v1_matrix_summary.py TestResults\v11-b 8 48
```

読み方:

- `decoder` 列が `avdec_h264` / `avdec_h265` / `avdec_prores` など **CPU デコーダに変わっている**
  こと（software が効いている証拠）。GPU 名のままなら V11-c の警告が出ているか確認する。
- **1080p**: `dist/s` の下限が素材 fps −1 以上（＝実フレームが素材 fps に追従）。
  例: 25fps 素材なら `dist/s` が 24..25、29.97 なら 29..30。
- **4K（`v1_h265_4k60.mp4`）**: 達成した `dist/s` と `cpu` をそのまま記録する。60 に届かなくても
  未達として記録し、限界（実フレーム数と CPU コア数）を文書化する。合否は親が決める。
- `cpu` 列（平均占有コア）を素材ごとに表へ写す。参考: 事前計測（合成映像）では HEVC 4K が
  実時間の約 3.5 倍。実素材では 1〜1.5 倍程度まで落ちうる。
- ProRes は 0-2 の注記どおり `[8,28)` 窓で別途集計する（30 秒素材のため既定窓では下限が落ちる）。
- V11-d と同じ run を使うので、追加の実行は不要。

---

## V11-c: ソフトウェアデコーダが無いときの GPU フォールバック警告

素の環境では profile 表の全コーデックに CPU profile があるため、**CPU デコーダを外した検証用の
パッケージコピー**を作って確かめる（製品の配布物・システムの GStreamer は変更しない）。

1. 検証用パッケージを作って展開する:

```powershell
scripts\package-release.ps1 -SkipInstaller -OutputDirectory '.\TestResults\v11-c-pkg'
Expand-Archive '.\TestResults\v11-c-pkg\TimecodeSyncPlayer-v<version>-win-x64.zip' `
    -DestinationPath '.\TestResults\v11-c-pkg\dist' -Force
```

2. CPU デコーダを提供するプラグインを外す:

```powershell
# H.264 / H.265 / VP9 / ProRes の CPU デコーダ (avdec_*) を外す
Remove-Item '.\TestResults\v11-c-pkg\dist\gstreamer\lib\gstreamer-1.0\gstlibav.dll'
# AV1 の CPU デコーダ (dav1ddec) まで外す場合はこちらも
# Remove-Item '.\TestResults\v11-c-pkg\dist\gstreamer\lib\gstreamer-1.0\gstdav1d.dll'
```

配布 zip は同梱ランタイム（`dist\gstreamer`）だけを使い、プラグイン探索もそこへ固定されるので、
外したプラグインは decodebin のフォールバックからも見えない。

3. 映像のみのクリップ（例: `v1_h264_1080p25.mp4`）を software で回す:

```powershell
& scripts\GpuOutputProbeHarness\Invoke-AppGpuTrial.ps1 `
    -MediaPath '<media>\v1_h264_1080p25.mp4' -Label 'v11-c-no-cpu-decoder' -Seconds 10 `
    -AppExe '.\TestResults\v11-c-pkg\dist\TimecodeSyncPlayer.exe' `
    -LogRoot '.\TestResults\v11-c' -PlayerBackend Gstreamer -DecodeMode software
```

注意: `gstlibav.dll` を外すと AAC 音声と ProRes も使えなくなる。**音声なしの H.264 クリップ**を使う。

4. 期待（ログの見方）:
   - `app-log-tail.txt`（または `app-stderr.txt`）に
     `software decode requested but no CPU decoder was usable; using hardware profile h264-gpu`。
   - 同じ run の `loaded (... / profile 0) ... decoder=d3d11h264dec`（GPU profile に落ちた）。
   - `runner-result.json` は `error` 空・`appExit=0`。

---

## V11-d: 出力側の不変（software でも表示・合成・Spout が同等）

V11-b の run（software、50 秒、Spout ON、DISPLAY2）をそのまま使い、V11-a の表と並べる。

読み方:

- 表示: `minPr` ≥ 59（表示が 60Hz に追従）。
- 合成: `cp99` を hardware（V11-a）と並べる。差が大きい場合は `compose` の内訳（`compose.fence` 等）を
  events.jsonl で確認する。
- Spout: `spout` 列（毎秒の send.publish 平均）が hardware と同水準。
- `err=0`、`exit=0`。
- `cpu` は software の方が大きくなる（CPU デコードのため）。これは想定内で、V11-b の記録として扱う。
- 「同等」の最終判断は親と利用者が行う。ここでは hardware / software の表を並べて差を示す。

---

## 付録: 実行順序の例（1 セッション）

1. V11-a: 11 本（+ MXF 個別）→ 集計 → デコーダ名一致を確認。
2. V11-b / V11-d: 同じ 11 本を software で → 集計 → fps・CPU・表示・合成・Spout を記録。
3. V11-c: パッケージコピーで 1 本（10 秒）→ 警告ログと decoder を確認。
4. 追加実行は条件を変えたときだけ。run 間の再起動は不要（別プロセスのため）。
