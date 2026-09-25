# v0.5.3 D38 の調査: ランスルーで保持後の同じトラック内のジャンプの着地が約 2 秒遅れる（w5:p3 向け、製品コードは変えない）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`。背景は `docs/release-0.5-plan.md` の「D38」の節（先に読む）。
**実装はしない。** 親が設計を見てから直す。

## 事実（親が確かめたログ）

```
14:37:21.791 Continue mode: sync seek ltc=20.000 playback=5.863 target=15.000 ... success=true   ← 保持中の着地シーク（15 へ）
14:37:22.734 Sync lifecycle: "SignalRecovered" source=held-jump / applying the first Jump frame once ltc=8.000
14:37:22.735〜23.76 sync.apply result="Deferred" ltc=8.045（約 110ms ごとに 11 回）
14:37:23.867 Timecode sync pending "TimedOut" playback=17.075
14:37:24.413 Timecode sync seek gated medianMs=-14576.6 consecutive=1 samples=1
14:37:24.633 Continue mode: sync seek ltc=8.045 playback=17.841 target=3.045 ... success=true   ← Jump の到着から 1.9 秒後
```
（`timecode-sync-player-v05` の tests の bin の logs、2026-09-25 のファイル。E2E の C-1）

## やること

1. **原因をコードで特定する**: Deferred がどこから返るか（`TimecodeSyncService.ShouldSuppressSeek` / `TimecodeSyncSeekState` / `ContinueOnTrackCoordinator` のどれか）、
   ランスルーで LTC が保持されているとき保留が Settled にならない理由、`TimecodeSyncSeekState` の置き換えの規則（距離が 4×tolerance を超えたら置き換える）が 12 秒の距離で効かない理由、
   時間切れの後のシークのゲート（`SyncDecisionEngine` の中央値・連続）で待つ理由
2. **いつ入ったかを二分探索する**: 開発機で、E2E の C-1 だけ（`--filter "FullyQualifiedName~C1_Continue_RepeatedJumps"`、1 回約 1 分）を、`git log` の 2026-09-17 〜 09-20 の間のコミットで回し、
   ジャーナル（`artifacts/ltc-scenarios/C-1-*/harness.jsonl` の `hold-landing` の `elapsedSeconds`）の 2 回目以降が 0.6 秒以下か 2 秒前後かで判定する。
   古いコミットの確かめ方: 別の作業ツリーを `git worktree add --detach <path> <sha>` で作り（`native/` の DLL を写す）、そこでビルドしてアプリの exe を `TIMECODE_SYNC_PLAYER_EXE` などで指す（今の E2E のアプリの指し方をコードで確かめる）。
   **E2E は 1 本ずつ直列で回す**。終わった作業ツリーは `git worktree remove` で消す
3. **直し方の案を 2〜3 個**: 例「ランスルーで保持中は、着地の直後に位置が合ったら保留を Settled にする」「距離が 4×tolerance を超える Jump は保留を無条件に置き換える」。
   それぞれ、ほかのシナリオ（S-2 の停止モード、C-2 の別トラック、D37 の着地窓）に何が起きうるかを書く
4. 書き出し: `docs/design/v0.5.3-d38-landing-delay.md`（新規）に 1〜3 を書く

## 規則

- **製品コードは変えない。git の書き込み禁止**（md を作るのと、一時的な作業ツリーを作って消すだけ）
- 終わったら、原因・入ったコミット・案の要約をこの画面に書く
