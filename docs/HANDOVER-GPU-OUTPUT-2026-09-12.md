# GPU 出力の引き継ぎ（2026-09-12）

段階 0〜6b（本体統合）+ 段階 5 終了処理の成果と、検証環境・集計・既知の事項を最小限にまとめる。

## ブランチと基点

| 項目 | 値 |
| --- | --- |
| 成果ブランチ | `codex/output-engine-20260911-1247` |
| 基点 SHA | `a9b934951726dfc9f7e2e611f94a5fd115db48f8`（a9b9349: 段階 5 + R-1/P-1） |
| 状態 | 親確認で合格。全 E2E 55 成功・0 失敗 |

## 検証 worktree

| 用途 | パス |
| --- | --- |
| 親（runner・集計・結果） | `C:\Users\codea\Documents\timecode-sync-player\.superpowers\worktrees\session-refactor`（branch `refactor/session-lifecycle`） |
| アプリ実行対象（runner 既定 `-AppExe`） | `C:\Users\codea\Documents\timecode-sync-player-wt-verify-oe-20260911-1344`（a9b9349、ビルド済み） |
| 結果ルート | `<親>\TestResults\gpu-app-20260911\<timestamp>-<label>\` |
| 試作の検証資材 | `<親>\TestResults\gpu-mutex-retry-session-20260910T0752Z\`（runner、`evaluate_runs.py`、`gst_delivery_check.py`） |

## runner: Invoke-AppGpuTrial.ps1

`<親>\TestResults\gpu-mutex-retry-session-20260910T0752Z\Invoke-AppGpuTrial.ps1`（Windows PowerShell 5.1）。
1 回の起動で 1 run。console セッション必須で、TimecodeSyncPlayer / GpuOutputProbe /
WinSpoutDXreceiver / gst-launch-1.0 / tcs-shim-test が動作中なら拒否する。起動時に
`outputBackend=1`（+ `-PlayerBackend Gstreamer` で `backend=1`）の `settings.json` を run ディレクトリへ書き、
`TIMECODE_SYNC_PLAYER_SETTINGS_PATH` などで隔離する。終了は WM_CLOSE → ダイアログのボタンを UIA で操作
（`-ExitDialog None` では無操作）。

主要オプション:

| パラメータ | 既定 | 内容 |
| --- | --- | --- |
| `-MediaPath`（必須） | — | 素材。`-ProjectPath` 指定時はアプリ引数としては使われない（パラメータ自体は必須） |
| `-Label`（必須） | — | run ディレクトリ名の接尾 |
| `-Seconds` | 32 | 計測秒 |
| `-DisplayDeviceName` | `\\.\DISPLAY2` | 全画面の表示先 |
| `-AppExe` | verify worktree の Debug EXE | 実行対象 |
| `-LogRoot` | `...\TestResults\gpu-app-20260911` | 結果ルート |
| `-NoFullscreen` / `-NoSpout` | off | 全画面／Spout の省略 |
| `-KillReceiverAfterSeconds` | 0 | 受信機を強制終了（abandoned 経路） |
| `-PlayerBackend` | `Mpv` | `Mpv` / `Gstreamer` |
| `-ProjectPath` | `''` | プロジェクトを `--load-project` で開く |
| `-ScreenshotAtSeconds` | 0 | 指定秒に表示先のスクリーンショット（`display-*.png`） |
| `-TestCardOnAtSeconds` / `-TestCardOffAtSeconds` | 0 | テストカードの ON/OFF |
| `-ClickPlay` | off | 明示的に再生開始 |
| `-ExitDialog` | `Normal` | `None`（無操作）/ `Normal`（`BtnExitNormal`）/ `Force`（`BtnExitForce`） |
| `-SimulateDeviceLoss` | `''` | `10` や `10,20`（`TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS`） |
| `-GpuRetryAtSeconds` | 0 | `BtnGpuRetry` を押す秒 |

例:

```powershell
$runner = 'C:\Users\codea\Documents\timecode-sync-player\.superpowers\worktrees\session-refactor\TestResults\gpu-mutex-retry-session-20260910T0752Z\Invoke-AppGpuTrial.ps1'
powershell -File $runner -MediaPath "D:\media\test_1080p60.mp4" -Label app-1080p -Seconds 60 -PlayerBackend Mpv
powershell -File $runner -MediaPath "D:\media\test_4k.mp4" -Label app-4k-gst -Seconds 32 -PlayerBackend Gstreamer -KillReceiverAfterSeconds 10
powershell -File $runner -MediaPath "D:\media\test_1080p60.mp4" -Label app-loss -Seconds 40 -SimulateDeviceLoss 10,20 -GpuRetryAtSeconds 25
```

## TestResults/gpu-app-20260911 の構成（run ごと）

```text
<timestamp>-<label>\
  runner-result.json     runner の設定・手順・終了コード・error
  inputs.json            実行ファイル・素材・DLL の SHA-256
  settings.json          その run の隔離設定
  app-log-tail.txt       アプリログ末尾 400 行
  display-*.png          スクリーンショット（指定時）
  app\
    manifest.json        QPC 周波数・原点・options（analyze_probe.py の前提）
    events.jsonl         本体の出力トレース（stop 時に一括書き出し）
    summary.json         validPerformanceResult・メトリクス・ソース診断
  analysis-*\
    analysis.json, metrics.csv, phase-durations.csv, selection.csv, output-id-differences.csv
```

## 集計スクリプト

- **`analyze_probe.py`**（`<親>\scripts\GpuOutputProbeHarness\analyze_probe.py`。この成果 worktree の
  同名ファイルは更新前なので親側を使う）:
  `python analyze_probe.py <run>\app [--start S] [--end S] [--output DIR]`。
  `events.jsonl` から phaseDurations・compose/present/send・scanout を集計し、新しい
  `analysis-*` ディレクトリを書く。`validPerformanceResult` が false なら終了コード 2。
- **`gst_delivery_check.py`**（親の `gpu-mutex-retry-session-20260910T0752Z` にあり）:
  `python gst_delivery_check.py <run>`。`app\manifest.json` と `app\events.jsonl` を読み、
  distinct/sec・NotReady/sec・id deltas・`gst.delivery` の到着間隔と arrival→acquire age・
  present/sec・errors を表示する。
- 複数 run の比較は `evaluate_runs.py`（同ディレクトリ）を使う。

## 既知の軽微事項

- `analyze_probe.py` の「Display selection started after its deadline」は親側で更新済み
  （vblank は vblank−1ms まで許容）。あわせて `send.acquire.end=abandoned` を acquired 扱いにする
  更新も親側のみ。成果 worktree の scripts 版は未反映のため、解析は親側を使う。
- 受信機断で `send.acquire.end=abandoned` が 1 件出る run は、上記更新前の解析だと invalid になる。
- L-4（単発スパイクで lead が跳ぶ）は未対応。
- HAP、120Hz 表示先、複数画面、実デバイス消失は未検証。復旧確認は疑似消失のみ。
- GStreamer の E2E は `artifacts\media` の素材が必要。
- mpv×Gpu×Spout の実機（受信機あり）は未検証。

## 次の候補

1. `video/x-hap` 分岐（HAP 圧縮テクスチャ直受け）。
2. 120Hz 表示先・複数画面（3面）の整列と年齢測定。
3. 実デバイス消失（デバイス無効化・ドライバー再起動）での復旧確認。
4. mpv×Gpu×Spout の実機確認（受信機あり・実素材）。
5. L-4 の lead 跳び対策（単発スパイクの除外または平滑化）。
6. GStreamer E2E の素材整備と CI 手順化。
