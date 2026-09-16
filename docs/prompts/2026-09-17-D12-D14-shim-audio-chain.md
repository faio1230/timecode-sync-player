# D12〜D14 と O1: shim の音声分岐・映像分岐・プロファイル待ち・ログ出力（v0.4.1）

作成: 2026-09-17 05:50、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（`452a828` 以降）** を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
背景: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` 末尾「D12〜D15」。検証機（クリーン環境）で v0.4.0 が音声付き素材を 1 本も再生できず、親が開発機でも再現した。
**これは v0.4.1 の修正。挙動を変えるのは下の 4 点だけ。同期の目標・補正・H-3・不変条件 I1〜I13 には触らない。shim の C ABI は変えない。**

## 1. 直すもの（`native/gst-shim/src/tcs_gstreamer.cpp`）

| # | 内容 | 方針 |
| --- | --- | --- |
| D12 | 音声分岐に **audioresample** が無く、44.1kHz 音声が wasapi2sink（共有モード、端末は 48kHz）と not-negotiated になり全プロファイル失敗 | `aconvert2` と sink の間に `audioresample` を入れる（`audioconvert ! queue ! audioconvert ! audioresample ! autoaudiosink`）。`TCS_FAKE_AUDIO` / fakesink の経路も同じ形にする |
| D13 | 映像分岐に **queue** が無く、demux が映像サンプルを先に出す MP4 では appsink の preroll で demux スレッドが止まり、音声シンクが preroll できず 15 秒タイムアウト | demux の映像パッドの直後（parser の前）に `queue` を 1 つ入れる。**遅延を増やさない**ため `max-size-buffers` は小さく（2〜4 を目安）、`max-size-time` / `max-size-bytes` は 0。シーク時のフラッシュで queue が空になること（D10 の stream time、SH1 の着地 1 フレーム以内が崩れないこと）を V5 相当で確認 |
| D14 | 不一致プロファイル 1 つにつき約 6 秒（bus スレッド起動前に `gst_element_get_state` 3 秒 × 2）。AV1 は av1-gpu まで 18 秒 | bus の ERROR を待ち時間の途中で拾って即座に次の試行へ進む（例: `gst_bus_timed_pop_filtered` で `ERROR | ASYNC_DONE` を待つ、または bus スレッドを先に起動して `p->failed` を見る）。**pad caps mismatch の早期打ち切り（既存の `capsMismatch`）は残す** |
| O1 | shim の LOG は stderr のみで GUI 起動では捨てられ、アプリログには `all video profiles failed` しか残らない | (a) 環境変数 **`TCS_LOG_FILE`**（パス）があれば LOG を stderr に加えてそのファイルへ追記する（open 失敗は無視、行ごとに flush、既存の `TCS_LEASE_LOG` / `TCS_FRAME_LOG` の量には従う）。(b) `all video profiles failed` の `set_error` に**最後の bus エラーの文言**を付ける（例: `... (last: Internal data stream error)`）。アプリ側でこの環境変数を logs ディレクトリへ向ける変更は除去担当が行う（名前はこの通りに固定） |

## 2. 検証（実機は一報のうえ親の合図。開発機の実機は同期担当だけが使う）

- 再現素材（ffmpeg で作業ツリー内に作る。生成コマンドを報告に書く）:
  (a) H.264 720p30 + AAC **44.1kHz**（`sine` 音声、20 秒）、(b) 同じで **48kHz**、映像トラック先頭（ffmpeg 既定）、(c) (b) を `-itsoffset 0.05` で音声先頭にした対照、(d) 既存の `artifacts/media/v1/v1_h264_1080p60_aac.mp4`
- 修正前に (a)(b) が失敗し (c)(d) が成功することを 1 回ずつ記録（`Invoke-AppGpuTrial.ps1 -NoSpout -Seconds 15`、`app-stderr.txt` の `load.attempt` / `attempt ... failed` / `load.summary`）
- 修正後: (a)〜(d) がすべて **最初の一致プロファイルで** 読め、`load.summary` の `total_ms` が 4 本とも **3 秒未満**（D14）。音声が出ること（V2 の RMS 手順で 1 本、44.1kHz 素材）
- 回帰: `check-shim-lock-rule.py` PASS、shim の実素材テスト、E2E 一部（`GStreamerBackend_SurvivesRepeatedTrackSwitches` / `SystemScenarioE2ETests`）、**V5 のシーク連打を 1 回**（着地 delta、`compose.publish` の最長無公開）、**V3 を 1 本**（Smooth。sample 平均 / p95-p5 が -28.9 / 38.3 から大きく外れない）
- `TCS_LOG_FILE` を付けた run でファイルに LOG が出ること、付けない run で挙動が変わらないこと

## 3. 報告

コミット、変更した行の要約、修正前後の (a)〜(d) の `load.summary`、V5・V3 の数字、証跡パス（`TestResults/gpu-app/<run>`）。**合否は書かない。ローカルの絶対パスは書かない。**
