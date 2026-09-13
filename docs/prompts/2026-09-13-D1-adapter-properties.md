# D1: GStreamer アダプターに `seeking` / `pause` プロパティを実装する

worktree は現行のまま。main から新ブランチ `codex/d1-adapter-properties-20260913` を切る。
不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。**C# のみ。shim には触れない**（`/code-review ultra` の結果待ちのため）。

## 事実（`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の「V3 の原因特定」）

`MainWindow.IsNativeSeeking()` は `GetPropertyString(mpv, "seeking") != "no"` で判定するが、
`GstMpvApiAdapter.GetPropertyString` は `path` / `width` / `height` / `video-codec` 以外を
`default: return string.Empty` で返す。空文字は `"no"` と一致しないため**常に「シーク中」**になり、
**`SyncDecisionEngine` が一度も走らない**（実測 `seek.decide` 6 run 計 0 件、`player.seeking` 46 件すべて `<empty>`/true）。

同じ理由で `pause`（2 箇所で使用）も空文字を返し、`IsNativeGapFreezeTargetReady()` が常に false になる。

## 依頼

1. `GstMpvApiAdapter.GetPropertyString` に **`seeking`** と **`pause`** を実装する。
   - `seeking`: shim がシーク処理中かどうかを `"yes"` / `"no"` で返す。mpv の意味論に合わせること
     （シーク発行から新位置のフレームが出るまでが `"yes"`）。
   - `pause`: 一時停止中かどうかを `"yes"` / `"no"` で返す。
   - `audio-codec` も空文字のままなので、返せるなら併せて実装してよい（軽微、任意）。
2. shim 側に新しい問い合わせが要るなら、**C ABI の追加は親に提案してから**。
   既存の状態（`tcs_player_*` の戻り値や C# 側で保持している状態）で表現できるならそれで良い。
3. **同期の挙動そのものは変えない。** 直すのは「状態を正しく報告する」ことだけ。
   `SyncDecisionEngine` や `IsNativeSeeking()` の判定式には手を入れない。

## 合格条件

- `player.seeking` の計測で、**GStreamer が再生中 `no`/false、シーク中のみ `yes`/true** になること
  （mpv と同じ形）。生値をログに出して示す。
- **`seek.decide` が GStreamer でも発火すること**。再同期ハーネス（`SyncSeekResyncE2ETests`）で
  mpv と同程度の件数（20 件以上）が出ること。
- 非E2E 全件成功、全 E2E 58 成功・0 失敗・4 スキップ。
- 通常再生 50 秒の実機指標が現状と同等（実フレーム 60/秒、合成 p99 1ms 前後、生成→走査 4〜6ms、error 0）。
- 1 コミット。

## この後の予定（参考）

D1 が入ったら親が V3 を測り直す。**その数字が本来の GStreamer の同期性能**になる。
D2（フリーズ・ギャップのシーク後に約 2 秒フレームが来ない、18/18 で再現）は shim 側の可能性があり、
`/code-review ultra` の結果を見てから着手する。

実機の前に一声かけること。報告は コミット／変更ファイル／非E2E／E2E／計測（`player.seeking` の生値と `seek.decide` 件数）／設計差異／未検証。
