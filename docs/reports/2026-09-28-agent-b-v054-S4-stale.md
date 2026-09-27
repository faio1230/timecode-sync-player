# S-4 の media-ready-stale と失敗の調査（agent-b-v054、2026-09-28）

作業ツリー `timecode-sync-player-v054b`（`git merge --no-ff v0.5.4` の後の `12124ca`）。読むだけの調査で、コードは変えていない。
実機・単体・ハーネスは回していない。ログは `timecode-sync-player-v05` の `artifacts/analysis-data/`（以下の相対パスはそこから）。
事実だけを書く。合否は書かない。時刻はアプリログの現地時刻。

## 見たログ

| 回 | 版 | S-4 の場所 | アプリログ |
| --- | --- | --- | --- |
| 失敗 | B7（`c300560`） | `b7-c300560/std-1/scenarios/S-4-20260928-050724/` | `b7-c300560/std-1/app-logs/`（05:07:24〜） |
| 合格（stale あり） | B6b（`915c35b`） | `b6b-915c35b/std-1/scenarios/S-4-20260928-025454/` | `b6b-915c35b/std-1/app-logs/`（02:54:54〜） |
| B6b の前（既定の素材） | K3 の取り込み（`k3merge-std`） | `k3merge-std/scenarios/S-4-20260928-004919/` | `k3merge-std/app-logs/`（00:49:19〜） |

補足:
- 親の文の「K3 の回（k3-f414-2ed23cd/allb-1・2）」は実素材のプロジェクト（`isDefaultProject:false`）で、読み込む素材が違う。
  このため B6b の前の比較には、既定の素材の `k3merge-std` を使った。
- `analysis-data` の S-4 の全回で media-ready-stale の行を数えた（`harness.jsonl` の `media-ready-stale`）。

| 回（`analysis-data` の下） | S-4 の stale |
| --- | --- |
| stageA・u7ab・b2ab・b3ab・b4ab（段 A の候補）、`k3merge-std`、k3-f414（実素材）、計 17 回 | 0 |
| b6b-915c35b | 3/4（heavy-1・heavy-2・std-1。std-2 は 0） |
| b7-c300560 | 2/2（heavy-1・std-1。std-1 は失敗） |

## 1. C の読み込みの後に UI の尺が 0 のままになる理由

### S-4 の場面

- Single、ランスルー。LTC は 35.000 で保持（Duplicate の連続）。
- A・B・C はどれも尺 20 秒、MediaOut 00:00:20。LTC 35 はどのトラックでもクリップの外なので、終端（最後のコマの頭 19.967）で境界ホールドになる場面。
- 試験は保持の中で B → C の順に読み込む。

### 失敗の回（B7）の C の読み込み

`b7-c300560/std-1/app-logs`、05:07:39:

```
39.700 Load ... ltc_c.mp4 start=none
39.700 Sync lifecycle: "FileLoad" / "BoundaryHoldReleased"（file load）
39.702 FetchMetadata: 1280x720 30.000fps
39.724 SeekTo target=20.000
39.724 sync.gate relocate reason=hold-entry target=20.000
39.724 LTC timecode held (run-through): entry alignment seek issued target=20.000 ltc=35.000 position=0.025
39.730 sync.gate new-landing target=20.000 delivered=20.000
39.774 Single mode: clip boundary hold ltc=35.000 playback=20.000
39.780 GstSeekingTracker: EOF（Ended）を観測したためシーク保留を解除しました
```

- 以後、`harness.jsonl` の media-ready-sample は 20.19 秒から 25.2 秒まで、ずっと `timeLabel "0:00:20:00 / 0:00:00:00"`・`duration 0` だった。
- 同じ回の B の読み込み（05:07:38.576）では、入口の合わせの目標は **19.967** で、stale は出ていない。

### 合格の回（B6b）

`b6b-915c35b/std-1/app-logs`、02:55:09:
- B の読み込み（09.707）の直後に、入口の合わせが **target=20.000**（09.741）→ EOF。この B で stale になり、nudge の後に 19.967 へ戻った。
- C の読み込み（16.189）の直後の入口の合わせは **target=19.967**（16.230）で、stale は出ていない。

### B6b の前（k3merge-std）

`00:49:31〜34`:
- B の読み込みの直後はシークが出ていない。
- 3 秒後に「file load released (timeout)」→ 最後の受理値の再適用で、同期の判定（Engine）がシークを出し、目標は **19.967**（00:49:34.489）。

### 読み（コードとの対応）

**20.000 と 19.967 の分かれ目**: RunThrough の入口の合わせ（B6b 追補 1 で追加）の目標は `LtcSyncController.TryGetHeldLandingTarget`（`LtcSyncController.cs:1332-1334`）。
1. `SyncDecisionEngine.ClampToClip(held, MediaIn, MediaOut, state.DurationSeconds, state.VideoFps)` で決まる。
2. `SeekableOut`（`SyncDecisionEngine.cs:397-403`）は、尺か fps が使えない（0 以下）と `clipOut`（MediaOut = 20.000）をそのまま返す。
   尺と fps が分かっていれば、最後のコマの頭（20 − 1/30 = 19.967）。
3. `state.DurationSeconds` と `state.VideoFps` の源は `MainWindow` の `_duration`・`_fps`（`MainWindow.xaml.cs:1083`）。
   - 読み込みで 0 に戻す（`ResetPlayerStateForNewTrack`、`:2995`）。
   - 尺は 100ms の UI タイマーが `TryGetDuration` で取れたときに入れる（`:2097-2099`）。
4. 入口の合わせは読み込みから 24〜41ms 後（保持の 2 枚目の Duplicate）に出る。
   そのとき UI タイマーがまだ尺を入れていなければ 20.000、入れていれば 19.967 になる（**時機の競争**）。

**Engine の経路との違い**: Engine の経路（B6b の前はこれだけ）は、尺が使えない間は判定しない（`bad-duration`）。
このため、尺が分かった後で 19.967 を狙っていた。停止モードの保持の着地（`ReapplyHeldValueOnPause`）も同じ `TryGetHeldLandingTarget` を使う（B6b 以前からの経路）。

**尺 0 のまま**:
- 素材の終わりちょうど（20.000）へのシークの後、EOF（Ended）になる。
- 以後 UI の尺が入らない（`TryGetDuration` が成り立たない）まま、nudge の手動シーク（0.2・0.0）で終わりから離れた直後に尺が入った（`harness.jsonl` 39 行: `0:00:00:09 / 0:00:20:00`）。
- shim の `tcs_player_get_duration`（`tcs_gstreamer.cpp:3911-3927`）は、キャッシュが 0 なら `gst_element_query_duration` を問う。
  EOF の間にこの問い合わせが成り立たないか（なぜか）は、ログからは分からない（shim のログに尺の問い合わせの行が無い）。

**関わる変更**: B6b 追補 1 の RunThrough の入口の合わせ（`AlignOnRunThroughHoldEntry`）。
- 読み込みの直後の保持（LTC の保持 + 範囲外）で、尺が分かる前に relocate を出すようになった。
- 予測ロケートと relocate の発行で rate を戻す変更は、この目標（先行量 0。保持中はマスター停止なので c = 0）と尺には関わっていない。
  ログの `lookaheadMs=0.0` がそれを示す。
- B7（Jump の確認）は、この場面では Jump が無いので関わっていない。

## 2. RunThrough の入口の 1 回合わせと手動シーク（門 22）の順番

### 失敗の回（B7）の nudge

`05:07:45`:

```
45.745 ManualSeek → SeekTo 0.200（1 回目）
45.748 new-landing target=0.200
45.749 BoundaryHoldReleased（playback left the boundary, playback=0.202）
45.749 SeekTo 19.967 / relocate reason=hold-entry / entry alignment seek issued position=0.202
45.752 ManualSeek → SeekTo 0.000（2 回目、入口の合わせの 3ms 後）
45.757 new-landing target=0.000
（以後、入口の合わせも同期の relocate も出ない。0 から 1.0 で走り、10 秒後に position=10.433 で失敗）
```

### 合格の回（B6b）

`02:55:15`:

```
15.675 ManualSeek → 0.200
15.681 ManualSeek → 0.000
15.723 BoundaryHoldReleased（playback=0.001）
15.774 entry alignment seek issued target=19.967 → 終端に戻り、境界ホールド
```

### 順番で結果が分かれる仕組み（コード）

- **入口の合わせが 1 保持に 1 回だけな理由**: 「この保持で合わせた値」（`HeldLossLandingSeconds`）を `TryGetHeldLandingTarget` が記録する。
  入口の判定（`LtcSyncController.cs:607`・`:621` の `holdEntry`）は、記録が無いときだけ真。
- **手動シーク（`OnLifecycle` の `ManualSeek`、`LtcSyncController.cs:245-255`）** は次を下ろす。
  - 下ろす: 保留の同期・未確認の Jump・`HeldReapplied`
  - **下ろさない**: `HeldLossLandingSeconds`

  このため手動シークの後は、保持が続いても入口の合わせは出ない（手動が勝つ）。
- **境界ホールドの解除（`BoundaryHoldReleased`、`LtcSyncController.cs:293-300`）** は `HeldLossLandingSeconds` を下ろす。
  - 解除の後の次の保持フレームで、入口の合わせがもう一度出る。
  - Single の境界ホールドは、手動シークで再生位置が端を離れると外れる（`clip boundary hold released (playback left the boundary)`）。
- **結果**: 手動シークの結果は、境界ホールドの解除（負債 L1: 層 3 の境界がコントローラの中）と手動シークの順で決まる。
  - 解除が 2 回の手動シークの後なら、入口の合わせが最後に出て、手動シークを打ち消して終端へ戻る（合格の回）。
  - 解除が 1 回目と 2 回目の間なら、入口の合わせは 2 回目の手動シークに上書きされ、記録が残るので再び出ない（失敗の回）。

### 規則との関係（事実の整理）

- 規則 4 は「マスター停止に**入るとき**、停止した値へ 1 回合わせる」。門 22（手動操作）は外側。
  - この場面では、LTC の保持（マスター停止）は 1 回続いたままで、新しい停止には入っていない。
  - 手動シークの後に入口の合わせが出るのは、境界ホールドの解除が「合わせた記録」を消すためで、規則 4 の「入るとき」ではない。
- 停止モードの D35 の着地（`ReapplyHeldValueOnPause` を呼ぶ 3 つの入口: `LtcSyncController.cs:656`・`:1149` ほか）と手動シークの関係は、ランスルーと同じ。
  - 手動シークは `HeldLossLandingSeconds` を下ろさないので、保持値の変化（D31-b）か境界ホールドの解除が無い限り、手動シークの後に保持値へ着地し直さない。
  - 停止モードの一時停止中に利用者が再生した場合は、`manualResumeSuppressesPause` で方針の一時停止をやめる（`LtcSignalLossPolicy.cs:335-342`）。
  - 境界ホールドの解除があれば、停止モードでも `ShouldLandOnFirstHeldValueDuringPause`（`:812-814`。方針の一時停止中・記録なし・保持値あり）で着地し直しうる。
    この経路は、今回のログには出ていない。
- 手動シークの後に入口の合わせをもう一度出すべきかは、規則（§10-1）には書かれていない。
  今のコードでは「出さない」が既定で、境界ホールドの解除を挟んだときだけ出る。

## 3. 試験の側と製品の側

### 試験の側

- **nudge の手動シークが 2 回ある**: `NudgeSeekBarToZero`（`tests/.../E2E/LtcScenarioE2ETests.cs:1845-1858`）。
  - シークバーを `before` が 0 なら 0.01、ほかなら 0.0 にし、続けて 0.0 にする。1 回目は尺 20 × 0.01 = 0.2 秒。
  - 2 回のシークの間は 3〜8ms。境界ホールドの解除と入口の合わせは、その間に入りうる（失敗の回）。
- **待ちの条件**: media-ready は「尺 > 0」を待つので、尺が 0 のままの状態（素材の終わりの EOF）では nudge に進む。
  S-4 の主張（C の読み込みの後、位置が 20.000 付近）は、nudge の手動シークの後の製品の挙動にも依存する。

### 製品の側

1. **読み込みの直後の RunThrough の入口の合わせ**が、尺と fps が分かる前に目標を決め、素材の終わりちょうど（MediaOut 20.000）へシークする。
   - EOF の後、UI の尺が 0 のまま残る。尺が分かっていれば 19.967（最後のコマの頭）。
   - 同じ目標の計算は、停止モードの保持の着地（`ReapplyHeldValueOnPause`）にもある。
2. **Single の境界ホールドの解除**が、規則 4 の入口の合わせの記録（`HeldLossLandingSeconds`）を下ろす。
   手動シークとの順で、手動シークが打ち消される（合格の回）か、入口の合わせが出ないまま走り続ける（失敗の回）かに分かれる（負債 L1 の場所）。
3. **計測**: 手動シーク（`Seek command sent source=Automation`）の着地も `sync.gate post-landing-residual` に出て、`reason` が直前の relocate の発生元（`hold-entry`）のままになる
   （05:07:45.748・.757）。手動シークは `ReportSeekSent` の発生元を持たないため。連続 relocate の数え方（`chained`）にも同じ記録が入りうる。

## 未解決の疑問

1. 素材の終わりちょうどへのシークの後、UI の尺（`TryGetDuration`）が入らない理由。shim の尺の問い合わせが EOF の間に成り立たないのかは、ログからは確かめられない。
2. 手動シークの後に規則 4 の合わせを出すか（手動を優先して出さないか、境界ホールドの解除で出し直すか）は、規則に書かれていない。
3. B6b の回の std-2 は stale 0 だった。入口の合わせが UI タイマーの尺の更新の後になった回と読めるが、その回のログは見ていない。
