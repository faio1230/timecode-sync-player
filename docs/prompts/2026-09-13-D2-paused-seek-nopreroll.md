# D2: 一時停止中のシーク後、目標位置のフレームが約 2 秒来ない

worktree は現行のまま。main から新ブランチ `codex/d2-paused-seek-20260913` を切る。
不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。

**shim の凍結は解除した**（`/code-review ultra` は起動されていなかったことが判明したため。詳細は下記）。
shim の製品コードを変更してよい。

## 事実（親と実装側の計測、`docs/V3-SEEK-BREAKDOWN-2026-09-13.md`）

フリーズ・ギャップの「最終フレームへシーク」で、**`seek.issue`→`seek.return` は 0.1〜0.2ms で戻るのに、
目標位置のフレームが 1 件も配信されない**まま次の操作（中央値 1981.7ms 後、min 1977 / max 2030）を迎える。
**18 件中 18 件で再現。**

D1 の報告でも「freeze ギャップの seek 後は paused sink が再 preroll せずフレームが来ないため、
`seeking` は次の load まで yes のままになる」と観測されている。**D1 の `seeking` 判定の精度にも影響している。**

## 有力な手がかり（親が確認）

**同じ問題が `tcs_player_step_frame` では既に対処されている**（`native/gst-shim/src/tcs_gstreamer.cpp` の
`wasPaused` の分岐）:

```c
  if (wasPaused) {
    /* PAUSED sinks do not re-preroll after a flush seek: run briefly and
     * stop again once the stepped frame has been delivered. The state changes
     * are outside frame_lock (see tcs_player_set_paused). */
    gst_element_set_state (pipeline, GST_STATE_PLAYING);
    ...
  }
```

**通常のシーク経路（`tcs_player_seek` / `seek_locked`）には同じ対処が無い。** これが原因かを計測で確かめること。

## 依頼

1. **原因を計測で確定する。** 一時停止中のシーク後にパイプラインとシンクがどの状態にあり、
   なぜフレームが出ないのかを示す（状態遷移、preroll の有無、`gst_element_get_state` の結果など）。
   `step_frame` の対処が効いている理由と対比すること。
2. **直す。** 方針は実装側が設計してよいが、**次は親の承認が要る**:
   - 一時停止の意味論を変える場合（PAUSED 中に一時的に PLAYING へ遷移させるなど、`step_frame` と同じ方式でも
     **通常シークに適用するのは新しい判断**）
   - 終了順序・デバイス構成・バッファリング方針・破棄規則に触れる場合
   `step_frame` と同じ方式を通常シークにも適用するなら、**その旨を明記して提案**すること（承認は早く出す）。
3. 副作用を測る。**再生中（一時停止していない）のシークが遅くならないこと。**

## 合格条件

- 一時停止中のシークで、**`seek.issue` から目標位置のフレーム到着までが 250ms 以下**（現状 約 1981ms）。
  フリーズ・ギャップの 18 件相当で再現させて中央値と p95 を出すこと。
- 再生中のシークの所要が現状と同等（D1 の再同期ハーネスの計測で比較）。
- `player.seeking` がシーク区間の外で `no` に戻ること（D1 で残っていた「次の load まで yes」が解消する）。
- 回帰: `tcs-shim-test` v1 全 13 素材 failures=0、非E2E 全件成功、全 E2E 58 成功・0 失敗・5 スキップ。
- 通常再生 50 秒の実機指標が現状と同等（実フレーム 60/秒、合成 p99 1ms 前後、生成→走査 4〜6ms、error 0）。
- 1 コミット。

## 凍結解除の経緯（記録）

親は「`/code-review ultra native/gst-shim/` が起動済み」という前提で shim を凍結していたが、
**この会話でそのコマンドは打たれておらず、ローカルにも痕跡が無い**ことを確認した。
前提の裏を取らずに凍結を続けたのは親の確認不足。利用者の判断で凍結を解除した。
shim のレビューは別途行う（時期は未定）。

実機の前に一声かけること。報告は コミット／変更ファイル／原因の計測／非E2E／E2E／実機の指標／設計差異／未検証。
