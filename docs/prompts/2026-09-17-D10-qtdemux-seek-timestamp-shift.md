# D10: MP4（B フレームあり）の accurate シーク後、qtdemux のタイムスタンプが先頭 DTS 分ずれる → shim は stream time を使う

作成: 2026-09-17 00:10、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（段 3 統合 `d5af2a6` 以降）** を `agent-a` へ通常マージしてから始める。
**shim（`native/gst-shim`）の小さな修正を含む。** 除去担当は段 4 で C# 側だけを触るので衝突しない（`native/gst-shim` は同期担当だけが触る）。

## 1. 事実（SH1 / SH1-b、2026-09-16 23:50〜00:05、同期担当の切り分け）

- `test_1080p60.mp4`（x264、B フレームあり、先頭 DTS = −0.0333）を 15.000 へ FLUSH+ACCURATE でシークすると、qtdemux は正しい IDR（packet #900、pts 15.000）を最初に push するが、
  **buffer の pts を 15.0333、segment を start=15.0333 / time=15.000 として送る**。以後の全サンプルも一律 +0.0333（先頭 DTS の絶対値 = 2 フレーム）
- decoder / sink はどれも落としていない。**中身は正しく、ラベル（PTS）だけが先頭 DTS 分ずれている**（qtdemux 1.28.2 のシーク経路。上流の問題と推定）
- shim は buffer の生 PTS を `pts_ns` としてリースに載せ、`tcs_player_get_time_pos` も同系の値を返すため、**アプリはシーク後、実際より 2 フレーム進んだ位置にいると思い込む**
- V3 の測定素材（`-preset ultrafast`、B フレーム無し、先頭 DTS 0）では起きない → V3 の数字は影響を受けていない
- **現場の素材（カメラ収録・通常の x264、B フレームあり）ではシーク・トラック切替のたびに 1〜2 フレームの同期バイアスになる**。出荷前に直す

## 2. 親の決定

- shim は **生の buffer PTS ではなく、segment で写像した stream time**（`gst_segment_to_stream_time(seg, GST_FORMAT_TIME, pts)`）を `pts_ns`（リース情報・frame ログ・delivery イベント）と
  `tcs_player_get_time_pos` の基礎にする。qtdemux の `segment.time=15.000 / start=15.0333` はこの写像で 15.000 に戻る
- `running_ns`（`gst_segment_to_running_time`）は今のまま（表示スケジューリング用。意味が違う）
- `segment` が無い／`GST_CLOCK_TIME_NONE` のときは従来どおり生 PTS へフォールバックし、1 回だけログ
- shim テストの `lease pts within one frame of the seek target` はこの修正で通るはず。**テストの期待値は緩めない**
- I13（`frame_lock` 保持中に GStreamer の状態変更・シークを呼ばない）は変えない。`on_new_sample` の中で segment を読むだけ

## 3. 検証

| 項目 | 内容 |
| --- | --- |
| shim | Debug ビルド 0 エラー、`check-shim-lock-rule.py` PASS、`--policy-only` failures=0、**実素材 `test_1080p60.mp4` failures=0**（`within one frame` が通る） |
| 単体 | shim の写像に単体テスト（segment start≠time のケースで stream time が返る、segment 無しは生 PTS）。非E2E 全件 |
| 実機 1 | V3 を Smooth で 1 本（B フレーム無し素材。**数字が段 3 の sample -28.9 / 38.3 から動かないこと**が期待） |
| 実機 2 | B フレームあり素材で着地を確認: `Invoke-AppGpuTrial.ps1 -MediaPath <artifacts\media\test_1080p60.mp4> -Label d10-<SHA> -Seconds 20 -PlayerBackend Gstreamer` に加え、
  `GStreamerBackend_SurvivesRepeatedTrackSwitches`（切替で先頭 DTS 負の素材をロード）。アプリログの位置とフレームのマーカーが一致するかを見る手段があれば添える |
| 報告 | コミット、diff の要約、shim テストの前後、非E2E 件数、実機 1・2 の数字、`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` への追記案（D10 節、事実と修正）。合否は書かない |

## 4. 守ること

- `Output/`、`RenderSession`、`Gst/` の C# アダプタは触らない（除去担当が段 4 で改名・置換中）。触るのは `native/gst-shim` と shim テスト、必要なら shim の単体テストだけ
- **公開リポジトリ**: ローカルの絶対パスを書かない
- 実機を使う前に一報。除去担当の E2E と重ねない。順番は親が決める
- main への書き込みはしない。コミットは `agent-a`、日本語
