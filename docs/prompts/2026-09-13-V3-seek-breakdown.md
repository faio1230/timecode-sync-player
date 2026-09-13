# V3: シーク経路とギャップ再入の時系列を計測で出す

worktree は新しく `C:\Users\codea\Documents\timecode-sync-player-wt-output-engine-20260911-1247`（cwd 固定）。
main から新ブランチ `codex/v3-seek-breakdown-20260913` を切る。不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。

## 前提（守ること）

- **`/code-review ultra native/gst-shim/` の結果が出るまで、shim の製品コードには手を入れない。**
  **計測イベントの追加は可**（下記 1）。製品の挙動を変える変更は結果を待つこと。
- 実機は VB-CABLE の精度ハーネス。**開始前に時刻を報告**すること。1 プロセスずつ、自分の PID のみ終了。
- **合否判定はしない。** 数字を出すところまでが依頼。採用と判定は親が行う。

## 背景（親の再集計、`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の「V3 の再集計」）

同じ計測器・同じ出力経路（`outputBackend=1`）で、プレイヤーだけ変えた比較:

- 定常誤差（black-sweep 除く）: 中心は GStreamer が 0 に近い（平均 −95.6ms 対 mpv −103.6ms）が、
  **ばらつきは 3.3 倍**（p95−p5 が 259.5ms 対 78.3ms）。
- 回復時間: シークは同程度（GStreamer 202〜355ms、mpv 154〜204ms）。freeze ギャップは GStreamer が速い（102ms 対 303ms）。
  **black ギャップの再入だけ GStreamer が約 12 秒**（mpv 253〜659ms）。誤差が 400〜700ms の帯に長く留まる。

**原因は推測しない。下記の時系列を計測で出すこと。**

## 1. シーク 1 回を 1 行にした時系列表を作る

各行が 1 回のシーク。列は**すべて同じ QPC 時計**の時刻（と差分）:

| 列 | 内容 | 出所 |
| --- | --- | --- |
| a | `SyncDecisionEngine` が Seek を決めた時刻 | **新規イベント `seek.decide`** |
| b | shim の seek 呼び出し 開始 / 復帰 | **新規イベント `seek.issue` / `seek.return`** |
| c | 新位置の最初のフレームが shim に到着した時刻と pts | 既存 `gst.delivery` |
| d | そのフレームの `compose.publish` | 既存 |
| e | `present.scanout` | 既存 |

**不足しているのは a と b だけ**。`events.jsonl` に 3 イベントを追加する。
**既定経路のコストを増やさないこと**（I4: 使わない機能は tick にコストを足さない、I6: 互いにブロックしない）。
トレース無効時は現状どおり何もしないこと。

同じ表を **mpv×Gpu でも取る**（mpv は seek 発行と、mpv スレッドからの最初の新フレーム）。
**区間ごとの差**（a→b、b→c、c→d、d→e）を出し、どこで時間が消えているかを示す。

## 2. 実機の取り方

- VB-CABLE の精度ハーネス（`SyncAccuracyE2ETests`）。**シーク位相のみ 20 回以上**になるよう回す
  （フェーズ構成を変えるか、同じ run を複数回まわして集める。方法は任せる）。
- `TIMECODE_ACCURACY_REPORT_DIR` は run ごとに**空のディレクトリ**（`phases.jsonl` が `FileMode.CreateNew`）。
- バックエンドはそのディレクトリに `settings.json` を先に置いて指定（例 `{"backend":1,"outputBackend":1}`）。

## 3. 表が出たら、次の 2 点を切り分ける（**この順番で**）

**どちらも「製品挙動を変える前に、計測用の切替で数字を出す」こと。採用は親が判断する。**

### (a) 黒ギャップ中の `PauseForGap`

現状方式と、**GStreamer ではパイプラインを PAUSED にせず再生を続け、黒は合成層で出す**方式
（I7 の Held/Black は合成の責務）を比較し、**ギャップ再入の回復時間**を出す。

### (b) シーク方式

現状の `FLUSH|ACCURATE` と、**TS 用に実装済みの `KEY_UNIT|SNAP_BEFORE` 着地＋目標まで高速復号**の方式を比較し、
**シーク回復時間**を出す。素材は **GOP 1 秒（`artifacts/media/v1`）と GOP 0.25 秒**を作って両方。

## 報告

1 の時系列表、3 の (a)(b) の比較表。**合否は書かない。** 設計差異と未検証も従来どおり。
