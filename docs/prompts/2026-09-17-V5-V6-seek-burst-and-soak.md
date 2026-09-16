# V5 のシーク連打と V6 の 60 分連続（v0.4 の完了条件の残り）

作成: 2026-09-17 03:00、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（Q3 統合 `093de7e` 以降）** を `agent-a` へ通常マージし、**shim を自分のツリーでビルド**してから（他ツリーの DLL は流用しない）。
完了の定義: `docs/release-0.4-plan.md`「V1〜V10 合格」のうち V5 のシーク連打と V6 の 60 分が未実施／部分。**実機を使うので、一報のうえ親の合図を待つ。**

## 1. V5: シーク連打（約 1 分）

- `Invoke-AppGpuTrial.ps1`（H1 修正済み。`-SeekAtSeconds` は「秒:正規化位置 0..1」）で 1080p60 素材を `-ClickPlay`、`-Seconds 40`、
  `-SeekAtSeconds "5:0.2,6:0.8,7:0.1,8:0.9,9:0.5,10:0.3,11:0.7,12:0.05,13:0.95,14:0.5"`（1 秒間隔で 10 回）
- 見る数字（`analyze_probe.py` と runner-result.json、アプリログ）: 各シークの `Seek command sent ... success=true` が 10 件、シーク後に `compose.publish` が止まらない（最長の無公開区間 ms）、
  `error` なし、デバイス消失 0、exit 0、終了時に残プロセス無し。**シーク後の最初のフレームの pts が目標から 1 フレーム以内**（D10 の修正が効いていること。app-stderr の `lease` / `frame` ログ）
- 実行前に `TCS_LEASE_LOG=1` と `TCS_FRAME_LOG=1` を付ける

## 2. V6: 60 分連続再生（GStreamer × GPU 合成 × Spout）

- 素材: 1080p60（`test_1080p60.mp4` は 30 秒なので、**プロジェクトで同じ素材を 120 本並べる**か、`ffmpeg -stream_loop` で 61 分の mp4 を作業ツリー内に生成する。どちらにしたかを報告）
- `Invoke-AppGpuTrial.ps1 -Seconds 3660 -ClickPlay -PlayerBackend Gstreamer`。**出力トレース（`TIMECODE_SYNC_PLAYER_OUTPUT_TRACE`）は付けない**（100 万イベントで頭打ちになる）。
  `-OverallTimeoutSeconds 3900`
- 見る数字（2 秒ごとの `Playback perf` 行と runner の受信サンプル、アプリログ）:
  - `gpuPublishedFrames` の増分が全区間で 120 ± 2 / 2 秒（落ちた区間の数と最大の落ち幅）
  - `playbackRate` が 1.000 のまま
  - アプリの private bytes / working set の推移（開始 5 分後と終了時の差。**増え続けていないこと**）
  - `ERR` 0、デバイス消失 0、`ring: recreated` 0、Spout 受信の連続性（受信側サンプルの欠落区間）
  - exit 0、残プロセス無し
- 60 分の間、**他のペインはビルド・実機を止める**（親が指示する）。開始時刻と予定終了時刻を報告に書く

## 3. 報告

V5 と V6 それぞれ、数字と証跡パス（`TestResults/gpu-app/<run>`）。**合否は書かない。** ローカルの絶対パスは書かない。
