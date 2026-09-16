# 実機確認チェックリスト

このチェックリストは、リリース前にLTC同期・ギャップ挙動・Spout出力などの主要機能を
実機環境（実際のLTC音源・実際の動画ファイル）で確認するための手順書。
自動テスト（非E2E・E2E）でカバーしきれない、実際の音声デバイスや長時間再生を伴う挙動を
対象とする。

---

## 事前準備

- [ ] Debugビルド最新化: `dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj`
- [ ] `native/tcs_gstreamer.dll`とGStreamerランタイムを配置済みか確認（[SETUP.md](SETUP.md)。Spout確認も行う場合は`SpoutDX.dll`も）
- [ ] LTC音源の準備。**重要: ループしないLTCソースを使うこと**（ループすると最終EOFを踏めない）。
  - 推奨: LTC音声ファイル（WAV）を用意し、プレイリスト全体の TimelineOut を**十分に越える長さ**まで再生できるようにする
  - LTCジェネレータを使う場合は、最終トラック終端を越えても巻き戻らずに進み続ける設定にする
- [ ] テスト用プレイリスト: 短い動画2本（例: 各20〜30秒）。Track2 の TimelineOffset を Track1 の直後または少しGapを空けて設定
- [ ] アプリ起動 → LTCデバイス選択 → START → LTCタイムコード表示が進むことを確認
- [ ] Mode: **Continue**、Sync: **ON**

ログの場所: `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\timecodesyncplayer-YYYYMMDD.log`

---

## ケース1: GapBehavior = Freeze で最終トラックEOF

- [ ] Gap: **Freeze** に設定
- [ ] LTCを流し、Track1 → Track2 と同期再生されることを確認
- [ ] LTCが Track2（最終トラック）の終端を越えて進み続ける
- [ ] **期待挙動:** 最終フレームが保持表示され続ける（暗転停止しない、映像が固まったまま表示継続）
- [ ] **期待ログ:**
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: no tracks, entering gap freeze target=...`（duration取得不可の場合は `gap freeze activated, holding current frame`）
- [ ] 終端越えの状態で1〜2分放置し、表示が乱れない・警告が連発しないことを確認

## ケース2: GapBehavior = Black で最終トラックEOF

- [ ] アプリ再起動（またはLTC停止→プレイリスト再ロード）後、Gap: **Black** に設定
- [ ] 同様にLTCを最終トラック終端越えまで進める
- [ ] **期待挙動:** 黒フレーム表示に遷移する
- [ ] **期待ログ:**
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: entered gap, rendering black frame`（または `gap, forcing black frame`）

## ケース3: EOF後のLTC復帰（ループ相当）

- [ ] ケース1またはケース2の終端状態から、LTCをプレイリスト有効レンジ内（例: Track1 の中間）へ戻す
- [ ] **期待挙動:** 該当トラックが再ロードされ同期再生が復帰する
- [ ] **期待ログ:**
  - `Continue mode: switching to track ... at media position ...` または
    `Continue mode: exiting gap, resuming playback at ...`
- [ ] 復帰後のシークが安定していること（`Continue mode: sync seek ... success=True`）

## ケース4（ついで確認・任意）: 既存機能の簡易回帰

機材セットアップ済みのついでに確認しておくと安心な項目:

- [ ] トラック間Gap（Freeze）: Track1→Gap→Track2 で前トラック最終フレームが保持される
- [ ] 同期seek: LTCを数回ジャンプさせ、`Timecode sync seek ... success=True` が出て追従する
- [ ] Spout出力（SpoutDX.dll がある場合）: 受信側（Resolume等）でフレームが届く

---

## GPU経路（OutputBackend=Gpu）の実機確認

GPU経路はrunner（親worktree `TestResults\gpu-mutex-retry-session-20260910T0752Z\Invoke-AppGpuTrial.ps1`）で
1プロセスずつ実行する。オプションの意味・結果ディレクトリの構成・集計スクリプトは
[HANDOVER-GPU-OUTPUT-2026-09-12.md](HANDOVER-GPU-OUTPUT-2026-09-12.md) を参照。
GStreamer×Gpu の検証項目（V1〜V11）の定義と結果は
[GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md](GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md) を参照。
この節の G1〜G7 は V1〜V9 の実機確認を日常手順に落としたもので、数値の合否は V の表に従う。

### 事前準備（GPU）

- [ ] `settings.json`は既定の`"outputBackend": 1`のままでよい（v0.4 の再生バックエンドは GStreamer 固定。v0.3 の`"backend"`キーは無視され、警告ログが 1 行出ます）
- [ ] `native\tcs_gstreamer.dll`とGStreamer 1.28.2ランタイムを配置（[SETUP.md](SETUP.md)）
- [ ] Spout確認は`native\SpoutDX.dll`と公式WinSpoutDXreceiverを用意
- [ ] consoleセッション（`query session`で`>console`）で、他にTimecodeSyncPlayer／WinSpoutDXreceiver／
      GpuOutputProbe／gst-launch-1.0／tcs-shim-testが動いていないこと
- [ ] 4K確認は4K表示先（例: `\\.\DISPLAY2`）をrunnerの`-DisplayDeviceName`へ指定

### ケース G1: 1080p・60秒（GStreamer×Gpu、Spout受信機あり）

- [ ] runner: `-MediaPath <1080p60素材> -Label app-1080p -Seconds 60 -PlayerBackend Gstreamer`
- [ ] `app\events.jsonl`の`compose.publish` / `present.return` / `send.publish`が60Hz相当で継続
- [ ] `app\summary.json`の`validPerformanceResult`が`true`、`errors`0、`present.status`失敗0
- [ ] 受信側（WinSpoutDXreceiver）でフレーム更新が途切れない

### ケース G2: 4K・32秒（GStreamer×Gpu）と受信機断

- [ ] runner: `-MediaPath <4K素材> -Label app-4k -Seconds 32 -PlayerBackend Gstreamer`
- [ ] 合成・表示・Spoutが60Hz、表示落ち・画像飛び0
- [ ] `-KillReceiverAfterSeconds 10`で受信機を強制終了しても、アプリが故障せず送信を継続し、
      保持画像の再送で復帰する（`send.acquire.end`の`abandoned`は正常扱い）

### ケース G3: GStreamer×Gpu（1080p／4K）

- [ ] runner: `-PlayerBackend Gstreamer`、素材は`artifacts\media`のH.264 1080p60／HEVC 4K
- [ ] `source.acquire`が`Ready`で、世代変更（`gst.generation`）後に古い画像が返らない
- [ ] `gst_delivery_check.py`でdistinct/secが概ね素材fps、NotReadyが定常的に続かない
- [ ] `gst.delivery`の到着間隔と`arrival→acquire age`が破綻していない
- [ ] `-KillReceiverAfterSeconds`で受信機断後も継続する

### ケース G4: キャンバス配置（スクリーンショット）

- [ ] `-ScreenshotAtSeconds`で1920×1080／3840×2160キャンバスの全画面を撮影
- [ ] 縦横比維持・はみ出しの切り落とし・黒余白・中央配置が仕様どおり
- [ ] クリップのFit（高さ合わせ／幅合わせ）が次の確定画像から反映される

### ケース G5: テストカード

- [ ] `-TestCardOnAtSeconds 10 -TestCardOffAtSeconds 20`でON/OFF
- [ ] カードON/OFFで再生・LTC同期が停止／再開しない（再生中は進み続ける）
- [ ] カードは全画面・Spout・プレビューで同時に表示・解除される

### ケース G6: 終了ダイアログ3経路

- [ ] `-ExitDialog None`: ×／Alt+F4で確認ダイアログが表示され、再生・LTC・出力が継続する。
      runnerは無操作のため60秒後に未終了をerrorとして記録する（想定内）。確認後は手動で終了する
- [ ] `-ExitDialog Normal`: `BtnExitNormal`で5手順（新規受付停止→再生停止（ダイアログ表示は「mpv／GStreamer 停止」）→出力停止→
      全画面終了→資源解放）が進み、終了コード0でプロセスが残らない
- [ ] `-ExitDialog Force`: `BtnExitForce`で追加確認なしに終了する（終了コード2）。プロセスが残らない

### ケース G7: デバイス消失

- [ ] `-SimulateDeviceLoss 10`: 1回目の消失で自動復旧し、画面が戻る（ログに`gpu.recover`）
- [ ] `-SimulateDeviceLoss 10,20 -GpuRetryAtSeconds 25`: 復旧後の再発で「GPU 出力停止。再試行」が
      表示され、`BtnGpuRetry`の手動再試行で復帰する
- [ ] 実デバイス消失（デバイス無効化・ドライバー再起動）は未検証。可能な環境では結果を記録する

### 結果記録（GPU）

| 項目 | 結果 | メモ |
|---|---|---|
| G1 1080p 60秒 | ⬜ OK / ⬜ NG | |
| G2 4K 32秒・受信機断 | ⬜ OK / ⬜ NG | |
| G3 GStreamer×Gpu | ⬜ OK / ⬜ NG / ⬜ 未実施 | |
| G4 キャンバス配置 | ⬜ OK / ⬜ NG | |
| G5 テストカード | ⬜ OK / ⬜ NG | |
| G6 終了ダイアログ3経路 | ⬜ OK / ⬜ NG | |
| G7 デバイス消失 | ⬜ OK / ⬜ NG / ⬜ 未実施 | |

---

## 事後確認

- [ ] ログ全体に `ERR` / `FTL` / `Exception` / `success=false` が**0件**であること
- [ ] 診断レポート生成: `scripts\run-timecodesyncplayer-diagnostics.ps1`
      → `artifacts\diagnostics\timecodesyncplayer-diagnostics-*.md` を確認
- [ ] `Playback perf warning` が出ている場合、文脈分類（Track switch aftermath / Gap exit aftermath / Normal playback）を確認し、Normal playback 中の警告がないこと

## 結果記録

| 項目 | 結果 | メモ |
|---|---|---|
| ケース1 (Freeze EOF) | ⬜ OK / ⬜ NG | |
| ケース2 (Black EOF) | ⬜ OK / ⬜ NG | |
| ケース3 (LTC復帰) | ⬜ OK / ⬜ NG | |
| ケース4 (簡易回帰) | ⬜ OK / ⬜ NG / ⬜ 未実施 | |
| 事後ログ確認 | ⬜ OK / ⬜ NG | |

**実施日:** ____年__月__日
