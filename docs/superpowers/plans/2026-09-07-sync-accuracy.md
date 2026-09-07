# 同期精度の基準測定

**Goal:** VB-CABLE 経由の 25 fps LTC に対する実出力フレームのずれ、シーク復帰、Black/Freeze 境界を測定する。
**Architecture:** 任意有効化の非同期 JSONL 計測、機械可読フレーム番号付き動画、独立した解析器、監視付き実機テスト。
**Tech Stack:** .NET 8 / WPF / xUnit / FlaUI / NAudio / ffmpeg / Python 3 標準ライブラリ / PowerShell 7。
**Spec:** この文書の測定契約。ユーザー承認済みの ms 基準測定を実装する。

## Global constraints

- 既存 session-refactor 作業ツリーのみを変更。同期設定・許容幅・シーク制御を変更しない。
- 元プロジェクト・元動画は変更しない。生成物は TestResults 配下。
- 原点はアプリの LTC デコード通知到着。終点は WriteableBitmap への実ピクセル反映直後。物理表示や Spout 受信側の遅延は対象外。
- ms 表記は 1 ms 精度の証明ではない。25 fps 入力は 40 ms 刻み、動画もフレーム区間を持つ。
- 計測失敗・欠損・不明画像を隠さない。品質基準未合意のため精度そのものの合否判定はしない。
- 共有ビルドは root が担当。アプリ実機試験は root だけが実行する。

## 共通契約

### マーカー

1920x1080 BGR32 画像の x=32, y=32 から 48 セル、セル幅16、高さ32。中央画素を白/黒として MSB-first 読む。白>=192、黒<=64、それ以外は無効。
6 bytes は [0xDD,0xAA,frameIndex high,frameIndex low,clipId,checksum]。
checksum は先頭5 bytes の XOR。clipId は1/2/3。frameIndex は0始まり。
黒判定はマーカー欠損と区別し、画面各所のサンプルが黒であることを確かめる。kind は経路情報であり実画像判定の代用にしない。

### アプリ trace JSONL (camelCase)

環境変数 TIMECODE_ACCURACY_TRACE に新規出力パスを指定した場合のみ有効。
全イベント: type, ticks (Stopwatch.GetTimestamp)。meta: schema=1, frequency, boundary="bitmap-publication", reference="decoded-ltc-receipt"。
ltc: seconds (当該テスト25 fpsならTimecodeを25で秒換算), fps。
frame: kind (normal/black/frozen/buffered), width,height, clipId nullable, frameIndex nullable, isBlack, markerValid, probeTicks (画像解析の処理時間)。ticks は画像解析前の公開時刻。
end: dropped, errors, events。アプリ終了時にフラッシュ。非同期有界キュー、ドロップは必ず集計。無効時はファイルやタスク生成なし。
並列投入の並びはticksで安定ソートする。LTC通知はdispatcher投入前に記録する。

### Fixture manifest / phase journal

fixture.json: schema=1, ltcFps=25, clips=[{id,name,fpsNumerator,fpsDenominator,frameCount,timelineOffset,mediaIn,mediaOut,path}]。
動画は最低12秒、プレイリストでは [0,10) 秒を使用。clip1=24/1,offset0、clip2=30000/1001,offset12、clip3=60/1,offset24。
phases.jsonl: {type:"phase-start",name,mode:"black"|"freeze",ticks,frequency,startSeconds,durationSeconds}; {type:"phase-end",name,ticks}; 最終 {type:"completed",ticks}。
black-sweep: LTC0から35秒、freeze-sweep: 同じ35秒。seek-a/b/c/back: LTC3/15/27/3から各5秒。音声停止・切替の時刻ではなく実際に受信したLTCで解析する。

### 解析の原則

- 各 LTC 到着時の最後に公開された画像を照合し、静止・更新停止も測定対象にする。未公開、不明マーカー、別クリップは別集計。
- signedErrorMs = (実frameIndex/fps - (mediaIn + receivedSeconds - timelineOffset))*1000。frame PTS - 期待media位置。frame interval [PTS,PTS+1/fps) 内に目標がある場合、intervalErrorMs=0。それ以外は最短距離に符号を付ける。
- 定常集計は各phase最初の有効LTCとclip切替から1秒を除外。全サンプルも保存し、除外数・未測定数・各fpsの件数を明示。
- 平均符号付き誤差、平均絶対誤差、絶対誤差p95/p99/max、20/40/80/250ms超の件数と時間を報告。入力間隔の異常や長い保持を隠さない。時間集計は観測間隔で重み付けし大きな入力欠落を無制限に補間しない。
- seek phase の復帰は最初の対応LTC通知から、正しいclipかつ|intervalError|<=80msと<=250msに0.5秒連続して留まる区間の開始まで。未収束も結果として残す。
- gap境界は最初のgap LTC通知と、それ以降の正しい実出力イベントの差。BlackはisBlack、Freezeは前clipの終了直前のフレーム番号（末尾1frameの許容）を確認。既に正しい出力なら到着時点では0ms（下限打切り）と明示。
- trace end無し、dropped/errors>0、phase不足、frame/input不足は測定不完全とする。測定不完全と精度が悪いことを区別。

## Task 1: アプリ計測 (agent)

作成: src/TimecodeSyncPlayer/AccuracyFrameMarker.cs, SyncAccuracyTrace.cs、および対応unit tests。
変更: App.xaml.cs startup/exit、MainWindow.xaml.cs LTC callback、FrameRenderer.cs 全画像公開経路。
テスト: 独立golden marker、壊れたchecksum/中間輝度/短いbuffer/黒画像、trace meta/endと時刻/正常flush、disabledの副作用なし。解析処理をタイムコードや同期制御に混ぜない。

## Task 2: 独立解析器 (agent)

作成: scripts/analyze-sync-accuracy.py, scripts/tests/test_sync_accuracy.py。
CLI: python scripts/analyze-sync-accuracy.py --trace PATH --fixture PATH --phases PATH --output DIR。
出力: accuracy-summary.json, accuracy-samples.csv, accuracy-report.md。完全な測定はexit0、不完全は非zero。悪い精度は報告しexit0。
テスト: 既知遅延、画像保持、間違いclip、欠損end/drop、gap、復帰しないケース。合成データは期待値を手計算して検証。

## Task 3: 動画・実機ハーネス (agent)

作成: tests/TimecodeSyncPlayer.Tests/Helpers/AccuracyVideoFixture.cs, E2E/SyncAccuracyE2ETests.cs, scripts/run-timecodesyncplayer-accuracy.ps1。
既存 E2EAppRunner/LtcSignalPlayer/MonkeyJson を再利用。専用Category=Accuracy、環境変数 TIMECODE_ACCURACY_REPORT_DIR 無しはskip。fixtureは実際に符号化したフレームをffmpegで読み戻してマーカー確認する。
アプリ子環境にのみTIMECODE_ACCURACY_TRACE設定、設定隔離。明示した6phasesを実行、正常終了でtrace flush。phase journalは随時flush。
runnerは既存real-project runnerの監視・TRX必須・所有PID cleanup・ERR/FTL採取方式を踏襲。total4分/idle60秒（fixture生成含む起動余裕は120秒）、--SkipBuild対応。runnerが解析器を実行、結果パス表示。ファイルや標準出力不足の偽成功を防ぐ。

## Task 4: 統合・独立レビュー・測定報告 (root + fresh reviewer)

上記契約の整合を確認し全体build、unit tests、Python tests、opt-in skipを検証する。VB-CABLE実測を実施し、独立レビューで計測の虚偽精度・除外漏れ・常時静止の見逃しを確認。必要な測定修正のみ実施して再実測。
docs/SYNC-ACCURACY.md に手順・測定範囲・実測値・未検証点を記録。git diff確認、必要回帰検証、コミットは日本語。main統合・pushは行わない。
