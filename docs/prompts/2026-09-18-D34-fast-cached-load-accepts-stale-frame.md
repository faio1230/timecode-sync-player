# D34: 前回成功プロファイルの再利用（attempt=0）で、別素材でも残骸フレーム 1 枚で「成功」になり、caps 0x0@0 のままロードが返る

作成: 2026-09-18、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`。版上げの 2 コミットはそのまま残す）。
基点: main の最新を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
根拠: 同期担当の机上解析（`docs/analysis/2026-09-18-D34-fast-cached-load.md`、H1）と検証機の shim ログ（下記）。
**製品コードの変更（shim + アプリ）。同期の目標・補正には触らない。**

## 1. 証跡（検証機、候補 5、shim ログ `tcs-gst-*`）

```
直前: loaded (qtdemux / profile 2) <M3> decoder=d3d11vp9dec 3840x2160@60.000 mem=d3d11
      load.summary <M3> paused=0 total_ms=437.0 attempt=3 profile=vp9-gpu
当該: pad caps mismatch for profile vp9-gpu -> next attempt
      load.attempt <M1> paused=0 attempt=0 profile=vp9-gpu result=ok teardown_ms=43.6 build_ms=4.4 set_state_ms=3.3 preroll_ms=100.2 first_frame_ms=100.3 duration_ms=0.1 total_ms=151.7 frames=1
      loaded (qtdemux / profile 2) <M1> decoder=d3d11vp9dec 0x0@0.000 mem=d3d11
      load.summary <M1> paused=0 total_ms=151.8 attempt=0 profile=vp9-gpu
（同じ run の通常の M1 ロードは attempt=8 prores-cpu で 3840x2160@60.000、total_ms=1083.7）

別例: 直前 <M2> h264-gpu 成功 → <M1> を attempt=0 profile=h264-gpu で result=ok、seek failed (gst_element_seek FALSE, accurate, target=14.937)、loaded 0x0@0.000
```

- 発生: 成功ロード 148 件中 1、155 件中 2、163 件中 1。**必ず「直前に成功した別素材のプロファイル」**（M1 を vp9-gpu / h264-gpu、M2 を prores-cpu）。素材固有ではない
- アプリ側の結果: `Gst loadfile rc=0 elapsedMs=110〜152`、`FetchMetadata` が 1 行も出ない（尺 −1 / サイズ 0）、`videoFps=30.000 defaultVideoFps=true`、位置は 0.000 または切替前の PTS（14.000）のまま、絵は黒または旧絵（G-5 / G-6 / C-2 の失敗、候補 2 のメタデータ時間切れ 4 件）

## 2. 直すこと

1. **shim（本体）**: ロード試行の成功条件に「映像 caps が確定している（幅・高さ・fps が 0 でない）」と「`pad caps mismatch` が出ていない」を入れる。満たさなければ `result=caps-missing`（または mismatch）として次の attempt へ回し、**`last_good` を更新しない**。attempt=0 の teardown で前のパイプラインの残骸フレームがゲートに入り「frames=1」と数えられる経路（解析の H1: `tcs_gstreamer.cpp` 2429 → 2815/2826、2672、2888）を塞ぐ: 新パイプラインの世代でないフレームは数えない
2. **shim（防御）**: `loaded ... 0x0@0.000` を成功として返さない（最終判定でも caps を検査し、全 attempt が失敗なら `all video profiles failed` と同じ失敗にする）
3. **アプリ**: `FetchMetadata` の予約（D33-b）を「サイズか尺が取れるまで、ロード後 5 秒を上限に 100ms タイマーで再試行し、取れなければ警告ログ 1 行」にする（`_duration > 0` のゲートで二度と呼ばれない経路を塞ぐ）。`ResetPlayerStateForNewTrack` で `MetaLine` を空にし、前トラックの表示を残さない
4. **アプリ（小）**: 出力トレースの保存で `manifest.json` が既に在るとき失敗せず、連番のサブフォルダ（`output-trace/<起動時刻>-<pid>/`）に保存する（検証機で 115 起動中 105 回が保存失敗）。テスト側もフォルダを分ける修正を検証機が入れるが、製品側でも失敗しないこと

## 3. 検証

- shim 単体（あれば）と非E2E 全件。`scripts/check-shim-lock-rule.py`
- 開発機で再現: 生成素材で「直前に別プロファイルで成功 → 次の素材を読む」を作る。h264（GPU）→ 4K ProRes（CPU）→ AV1 の順に `-Media` で並べたシナリオ（例: F-4、G-5、C-2）を 3 サイクル。shim ログに `loaded ... 0x0@0.000` が **0 件**、`result=caps-missing`（新設）が出るなら次の attempt で成功していること
- E2E（実機は一報のうえ親の合図）: `LtcScenarioE2ETests` 22 本（生成素材）と 4K 3 本の F/G/C 系。`LtcHardwareLoopE2ETests`。統合は実機ロード 1 本を含む
- ロード時間が悪化しないこと（attempt=0 の正常ケースは今までどおり 100ms 台）

## 4. 報告

コミット（shim / アプリ / トレースで分ける）、変更の要約、単体の増減、E2E の結果、shim ログの `0x0@0.000` 件数、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。版は上げない（agent-a の版上げコミットはそのまま）。**
