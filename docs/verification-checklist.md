# 実機確認チェックリスト

リリース前に、LTC同期・ギャップ・信号断・ProResのGPUデコード・Spout出力などの主な機能を、
実機の環境（実際のLTC音源と動画ファイル）で確かめるための手順書。
自動テスト（非E2E・E2E）で確かめきれない、実際の音声デバイスや長時間の再生を伴う挙動を対象とする。

---

## 事前準備

- [ ] Debugビルドを最新にする: `dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj`
- [ ] `native\tcs_gstreamer.dll`とGStreamerランタイムを配置する（[SETUP.md](SETUP.md)）。Spoutも確かめるときは`SpoutDX.dll`も置く
- [ ] ProResを確かめるときは、ソースからのビルドでは`pwsh -File scripts\get-prores-plugin.ps1`でgst-prores-d3d11を`native\gst-prores`に置く（インストーラーとzipには同梱）
- [ ] LTC音源を用意する。ループしないLTCソースを使う（ループすると最終トラックの終端を越えられない）
  - LTC音声ファイル（WAV）を使うときは、プレイリスト全体のTimelineOutを十分に越える長さまで再生できるものにする
  - LTCジェネレータを使うときは、最終トラックの終端を越えても巻き戻らずに進み続ける設定にする
- [ ] テスト用プレイリストを用意する。短い動画2本（例: 各20〜30秒）で、Track2のTimelineOffsetはTrack1の直後か、少しGapを空けた位置にする
- [ ] アプリを起動し、LTCデバイスを選んでSTARTを押し、LTCタイムコードの表示が進むことを確かめる
- [ ] ModeはContinue、SyncはONにする
- [ ] FPSはLTCと同じ値に固定する（24 / 25 / 29.97 DF / 30）。Autoのままだと画面に「Auto は確認用です。本番は固定にしてください」と出る。Autoは確認用なので、合否はこの固定の設定で判定する。Autoで回したときは、結果の表のメモ欄にそう書く
- [ ] 信号断時は、ケース4までは既定の「ランスルー」のままにする

ログは次の2つで、どちらも同じlogsフォルダに出る。

- アプリ: `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\timecodesyncplayer-YYYYMMDD.log`
- shim（GStreamer）: `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\tcs-gst-YYYYMMDD.log`

---

## ケース1: Gap = Freezeで最終トラックの終端を越える

- [ ] GapをFreezeにする
- [ ] LTCを流し、Track1→Track2と同期して再生されることを確かめる
- [ ] LTCがTrack2（最終トラック）の終端を越えても進み続けるようにする
- [ ] 期待する挙動: 最終フレームが表示されたまま残る（暗転しない）
- [ ] 期待するログ（アプリ）
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: no tracks, entering gap freeze target=...`（durationが取れないときは`Continue mode: gap freeze activated, holding current frame because duration is unavailable`）
- [ ] 終端を越えた状態で1〜2分置き、表示が乱れず、警告が続けて出ないことを確かめる

## ケース2: Gap = Blackで最終トラックの終端を越える

- [ ] アプリを再起動するか、LTCを止めてプレイリストを読み込み直してから、GapをBlackにする
- [ ] ケース1と同じく、LTCを最終トラックの終端の先まで進める
- [ ] 期待する挙動: 黒い画面に変わる
- [ ] 期待するログ（アプリ）
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: entered gap, rendering black frame`（または`Continue mode: gap, forcing black frame`）

## ケース3: 終端の後にLTCを戻す（ループ相当）

- [ ] ケース1かケース2の終端の状態から、LTCをプレイリストの範囲内（例: Track1の中ほど）へ戻す
- [ ] 期待する挙動: 該当するトラックを読み込み直し、同期した再生に戻る
- [ ] 期待するログ（アプリ）: `Continue mode: switching to track ... at media position ...`
- [ ] 戻った後のシークが安定し、`Continue mode: sync seek ... success=True`が出る

## ケース4: 信号断時 = ランスルー（既定）

- [ ] 信号断時が「ランスルー」であることを確かめる
- [ ] トラックの再生中にLTCを止める（ケーブルを抜く、または音源を一時停止する）
- [ ] 期待する挙動: 映像は止まらず、そのまま進み続ける。画面に「信号断で停止中」「タイムコード停止で停止中」は出ない
- [ ] 期待するログ（アプリ）: `LTC signal lost: playback paused`は出ない（Debugビルドでは損失の確定が`sync.gate signal-loss-confirm`の行に出る）
- [ ] LTCを再開すると、映像がLTCの位置へ追従する（ずれていれば`Continue mode: sync seek ... success=True`で合わせる）

## ケース5: 信号断時 = 停止

- [ ] 信号断時を「停止」にする（Sync ON、ギャップの外で確かめる。ギャップ中は止めない）
- [ ] トラックの再生中にLTCの音を止める（無音にする）
- [ ] 期待する挙動: 再生が一時停止し、画面に「信号断で停止中」と出る
- [ ] 期待するログ（アプリ）: `LTC signal lost: playback paused timeoutMs=... reason=SignalLoss`
- [ ] LTCを戻すと再生が再開する。ログは`LTC signal restored: playback resumed resumeFrames=...`
- [ ] 音は出たままタイムコードの値だけが進まない状態（ジェネレータの一時停止など）を作れるときは、画面に「タイムコード停止で停止中」、ログに`reason=TimecodeHeld`が出ることも確かめる
- [ ] 確認が終わったら、信号断時を本番で使う値に戻す

## ケース6: ProResのGPUデコード

NVIDIA（RTX級）のGPUで確かめる。ProResの素材をプレイリストに入れて使う。

- [ ] 「ProRes の GPU 復号」を「自動」にしてProResの素材を開く
- [ ] 期待する挙動: 画面のメタデータ行のデコーダ名が`V:proresd3d11dec`になる
- [ ] 期待するログ（shim）
  - `prores-gpu: mode=auto source=... vendor=0x10de -> enabled`
  - `loaded (...)`の行に`decoder=proresd3d11dec`
- [ ] 「ProRes の GPU 復号」を「無効」に変える。画面に「再起動の後に反映」と出るので、アプリを再起動してから同じ素材を開く
- [ ] 期待する挙動: デコーダ名が`V:avdec_prores`になる
- [ ] 期待するログ
  - アプリ: `ProRes: CPU で復号（proResGpu=off、理由は tcs-gst のログ）`
  - shim: `load.skip ... profile=prores-gpu reason=prores-gpu-off`
- [ ] 確かめた後は「自動」に戻し、アプリを再起動する

## ケース7（ついでの確認・任意）: 既存機能の簡易回帰

機材を用意したついでに確かめておくとよい項目。

- [ ] トラック間のGap（Freeze）: Track1→Gap→Track2で、前のトラックの最終フレームが表示されたまま残る
- [ ] 同期シーク: LTCを何度かジャンプさせ、`Continue mode: sync seek ... success=True`が出て追従する（ModeがSingleのときは`Timecode sync seek ... success=True`）
- [ ] Spout出力（SpoutDX.dllがあるとき）: 受信側（Resolumeなど）にフレームが届く

---

## GPU経路の実機確認

GPU経路はrunner（`scripts\GpuOutputProbeHarness\Invoke-AppGpuTrial.ps1`）で1プロセスずつ実行する。
オプションの意味、結果ディレクトリの構成、集計スクリプトは
[HANDOVER-GPU-OUTPUT-2026-09-12.md](HANDOVER-GPU-OUTPUT-2026-09-12.md) を参照。
GStreamer×Gpuの検証項目（V1〜V11）の定義と結果は
[GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md](GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md) を参照。
この節のG1〜G7はV1〜V9の実機確認を日常の手順にしたもので、数値の合否はVの表に従う。
runnerの`-PlayerBackend`は既定の`Gstreamer`のままでよい（v0.4から再生はGStreamerだけ）。

### 事前準備（GPU）

- [ ] `settings.json`は既定の`"outputBackend": 1`のままでよい（v0.3の`"backend"`キーは無視され、警告ログが1行出る）
- [ ] `native\tcs_gstreamer.dll`とGStreamer 1.28.2ランタイムを配置する（[SETUP.md](SETUP.md)）
- [ ] Spoutを確かめるときは`native\SpoutDX.dll`と公式のWinSpoutDXreceiverを用意する
- [ ] consoleセッション（`query session`で`>console`）で、ほかにTimecodeSyncPlayer／WinSpoutDXreceiver／
      GpuOutputProbe／gst-launch-1.0／tcs-shim-testが動いていないこと
- [ ] 4Kの確認では、4Kの表示先（例: `\\.\DISPLAY2`）をrunnerの`-DisplayDeviceName`に指定する

### ケースG1: 1080p・60秒（Spout受信機あり）

- [ ] runner: `-MediaPath <1080p60素材> -Label app-1080p -Seconds 60`
- [ ] `app\events.jsonl`の`compose.publish` / `present.return` / `send.publish`が60Hz相当で続く
- [ ] `app\summary.json`の`validPerformanceResult`が`true`、`errors`が0、`present.status`の失敗が0
- [ ] 受信側（WinSpoutDXreceiver）でフレームの更新が途切れない

### ケースG2: 4K・32秒と受信機断

- [ ] runner: `-MediaPath <4K素材> -Label app-4k -Seconds 32`
- [ ] 合成・表示・Spoutが60Hzで、表示落ちと画像飛びが0
- [ ] `-KillReceiverAfterSeconds 10`で受信機を強制終了しても、アプリは止まらずに送信を続け、
      保持画像の再送で戻る（`send.acquire.end`の`abandoned`は正常として扱う）

### ケースG3: 1080p／4Kの配信

- [ ] 素材は`artifacts\media`のH.264 1080p60／HEVC 4K
- [ ] `source.acquire`が`Ready`で、世代の変更（`gst.generation`）の後に古い画像が返らない
- [ ] `gst_delivery_check.py`でdistinct/secがおおむね素材のfpsになり、NotReadyが続かない
- [ ] `gst.delivery`の到着間隔と`arrival→acquire age`が乱れていない
- [ ] `-KillReceiverAfterSeconds`で受信機を切った後も続く

### ケースG4: キャンバス配置（スクリーンショット）

- [ ] `-ScreenshotAtSeconds`で1920×1080／3840×2160のキャンバスの全画面を撮る
- [ ] 縦横比の維持、はみ出しの切り落とし、黒の余白、中央配置が仕様どおり
- [ ] クリップの配置（高さ合わせ／幅合わせ）が、次に確定した画像から反映される

### ケースG5: テストカード

- [ ] `-TestCardOnAtSeconds 10 -TestCardOffAtSeconds 20`でON/OFFする
- [ ] カードのON/OFFで再生とLTC同期が止まらない（再生中は進み続ける）
- [ ] カードは全画面・Spout・プレビューで同時に出て、同時に消える

### ケースG6: 終了ダイアログの3経路

- [ ] `-ExitDialog None`: ×／Alt+F4で確認ダイアログが出て、再生・LTC・出力は続く。
      runnerは操作しないため、60秒後に「終了していない」をerrorとして記録する（想定内）。確かめた後は手で終了する
- [ ] `-ExitDialog Normal`: `BtnExitNormal`で5つの手順（新規受付の停止 → 再生の停止（ダイアログの表示は「GStreamer 停止」）→ 出力の停止 →
      全画面の終了 → 資源の解放）が進み、終了コード0でプロセスが残らない
- [ ] `-ExitDialog Force`: `BtnExitForce`で追加の確認なしに終了する（終了コード2）。プロセスが残らない

### ケースG7: デバイス消失

- [ ] `-SimulateDeviceLoss 10`: 1回目の消失で自動で復旧し、画面が戻る（`app\events.jsonl`に`gpu.recover.start`と`gpu.recover.done`）
- [ ] `-SimulateDeviceLoss 10,20 -GpuRetryAtSeconds 25`: 復旧の後にもう一度消えると「GPU 出力停止。再試行」が
      出て、`BtnGpuRetry`の手動の再試行で戻る
- [ ] 実際のデバイス消失（デバイスの無効化、ドライバーの再起動）は未検証。試せる環境では結果を記録する

### 結果記録（GPU）

| 項目 | 結果 | メモ |
|---|---|---|
| G1 1080p 60秒 | OK / NG | |
| G2 4K 32秒・受信機断 | OK / NG | |
| G3 1080p／4Kの配信 | OK / NG / 未実施 | |
| G4キャンバス配置 | OK / NG | |
| G5テストカード | OK / NG | |
| G6終了ダイアログ3経路 | OK / NG | |
| G7デバイス消失 | OK / NG / 未実施 | |

---

## 事後確認

- [ ] アプリのログに`ERR` / `FTL` / `Exception` / `success=False`が0件であること
- [ ] 診断レポートを作る: `pwsh -File scripts\run-timecodesyncplayer-diagnostics.ps1`
      →`artifacts\diagnostics\timecodesyncplayer-diagnostics-*.md`を確かめる
- [ ] `Playback perf warning`が出ていたら、診断レポートの文脈の分類（Track switch aftermath / Gap exit aftermath / Normal playback）を見て、Normal playbackの警告が無いことを確かめる

## 結果記録

| 項目 | 結果 | メモ |
|---|---|---|
| ケース1（Freezeで終端越え） | OK / NG | |
| ケース2（Blackで終端越え） | OK / NG | |
| ケース3（LTCを戻す） | OK / NG | |
| ケース4（信号断・ランスルー） | OK / NG | |
| ケース5（信号断・停止） | OK / NG | |
| ケース6（ProResのGPUデコード） | OK / NG / 未実施 | |
| ケース7（簡易回帰） | OK / NG / 未実施 | |
| 事後のログ確認 | OK / NG | |

FPSの設定（固定の値、またはAuto）: ________

実施日: ____年__月__日
