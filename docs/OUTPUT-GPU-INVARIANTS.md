# GPU 出力層の不変条件（全段階共通）

状態: 2026-09-11、親が段階 0〜3 の差し戻し（A〜D、F、H、H-2、S3-2）から抽出。実装依頼のプロンプトには本文書のパスを毎回含め、「不変条件に触れる変更は実装側で決めず、必ず質問する」と明記する。根拠は [確定設計](OUTPUT-GPU-DESIGN-CONFIRMED.md)、[製品動作](OUTPUT-PIPELINE-DESIGN.md)、[ソース契約](GPU-SOURCE-CONTRACT-SPEC.md)、[評価記録](OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md)。

## 不変条件（破ったら不合格）

| # | 条件 | 由来 |
| --- | --- | --- |
| I1 | **最新優先**: 合成は取得可能な中で最新の画像を使う。揺れ吸収のための滞留は最大 1 フレーム。停止や周期差で溜まった分は捨てて最新に追い付く（重複より破棄）。 | H（FIFO で遅延固定化） |
| I2 | **immediate context は GPU worker のもの**: 他スレッドが触る場合は `ID3D11Multithread.SetMultithreadProtected(TRUE)` を `GpuDevice` が明示的に有効化し、呼び出しは短く（`Flush`／`GetData` ループを他スレッドで回さない）。「free-threaded」を前提にしない。 | S3-2 |
| I3 | **届く vblank は飛ばさない**: 目標時刻（vblank−margin）を過ぎても vblank まで lead(1ms) 以上あれば即時 Present。合成期限より表示を優先。 | vblank 第2回、S3-2 |
| I4 | **Spout OFF は無コスト**: Spout が無効のとき合成 tick にコピー・待ち・ロックを一切足さない。 | 段階 3 |
| I5 | **GPU 完了は tick の外**: アップロード／コピーの GPU 完了待ちを合成の fence 待ちに含めない（専用クエリで完了確認後に Ready）。 | F |
| I6 | **UI と GPU worker は互いに同期待ちしない**: 受け渡しは不変レコードの mailbox と有限 queue。mpv／GStreamer のコールバックスレッドでもブロックしない。 | C、shim 契約 |
| I7 | **世代排除**: 旧世代の画像は返さず、NotReady/Ended で黒を出さない（Held を使う）。 | ソース契約 |
| I8 | **停止順序**: 新規受付停止 → RenderSession.Stop → OutputEngine.Stop（worker join、lease 返却）→ 全画面閉 → mpv／shim destroy → OutputEngine.Dispose → Spout → バッファ。lease は shim destroy より先に返す。 | 段階 6 |
| I9 | **時間超過は資源解放の理由にしない**: GPU 完了待ちの期限超過は fault として記録し新規処理を止めるが、使用中資源を破棄しない。デバイス消失だけが再作成の根拠。 | 設計 |
| I10 | **lead は上げ急・下げ緩**: p99＋1ms で即時に上げ、下げは 5 秒連続で 0.5ms 刻み。起動後 3 秒は学習しない。 | F、段階 3 |
| I11 | **計測は同じ時計**: 新しい経路は `events.jsonl` に同じ QPC で記録し、`analyze_probe.py`／`gst_delivery_check.py` で親が読める形にする。公開頻度だけで合否を判断しない。 | 全段階 |
| I12 | **Cpu backend は不変**: `OutputBackend=Cpu` の経路（OutputFrame→Bitmap→SendImage）に手を入れない。 | 計画 |
| I13 | **GStreamer の状態変更・シークを、ストリーミングスレッドが要求しうるロックの保持中に呼ばない**: `gst_element_set_state` / `gst_element_seek` / `gst_element_send_event` を `frame_lock` を保持したまま呼ばない。これらはストリーミングスレッドの進行を必要とし、そのスレッドは `on_new_sample` で `frame_lock` を待ちながらシンクのストリームロックを持っているためデッドロックする。**ロックは「何をするか」の決定だけを守り、GStreamer の呼び出しはロックの外で行う。** 検査: `python scripts/check-shim-lock-rule.py` | D2（2026-09-13、TS で約 6% のハング） |

### I13 で `state_mutex` を例外扱いする根拠（2026-09-13 の静的監査）

D2 修正後（`fdbf543`）の shim には、**`state_mutex` を保持したまま `gst_element_set_state` を呼ぶ箇所が
3 つ残っている**（`pump_arm` 1591、`pump_preroll_tick` 1645、`tcs_player_set_paused` 2408）。
これは消し忘れではなく、意図的に残してよいと判断した。根拠は次のとおり。

**デッドロックが成立する条件は「状態変更を待つ側が持つロックを、ストリーミングスレッドが要求すること」**。
`frame_lock` は `on_new_sample` が取るので条件を満たす。`state_mutex` は満たさない:

- `state_mutex` を取るのは上記 3 関数だけ。`pump_arm` と `tcs_player_set_paused` は API（UI）スレッド、
  `pump_preroll_tick` は `bus_loop` の**専用バススレッド**（`gst_bus_timed_pop_filtered`）から呼ばれる。
- 投稿スレッド上で走る `sync_bus_handler` は `NEED_CONTEXT` を処理するだけで**ロックを一切取らない**。
- `tcs_player_seek` は `pump_arm` を `frame_lock` の**外**で呼ぶ。ここが内側だと
  「UI が frame_lock 保持 → state_mutex 待ち／バススレッドが state_mutex 保持 → set_state がストリーミング
  スレッド待ち／ストリーミングスレッドが frame_lock 待ち」の循環が閉じる。**この 1 点は今後も崩してはならない。**

つまり安全性は**ミューテックスの性質ではなくコードの性質**であり、`state_mutex` を取る関数が増えた瞬間に
前提が崩れる。そこで `scripts/check-shim-lock-rule.py` に `state_mutex` を取る関数の集合を固定し、
**増えたら検査を失敗させて人間に再検討を強制する**ようにした。
負の対照として、D2 修正前の main に対しては既知の 2 箇所を検出して FAIL することを確認済み。

### I13 の補足（同じ罠を 3 回踏んだ経緯）

この規則は 2026-09-12 の時点で `tcs_player_set_paused` のコメントに書かれていた:

```
/* Do not hold frame_lock across the state change: the streaming thread may
 * be inside on_new_sample waiting for frame_lock while holding the sink's
 * stream lock, and the state change needs that stream lock (deadlock). */
```

**それでも新しいコードを書くたびに破られた。**

| 箇所 | 保持していたロック | 呼んでいた GStreamer API |
| --- | --- | --- |
| `pump_arm` / `pump_preroll_tick` / `tcs_player_set_paused`（D2 で新設） | `state_mutex` | `gst_element_set_state` |
| `build_pipeline`（paused load） | `frame_lock` | `gst_element_set_state(PAUSED)` |
| `seek_locked`（EOS 再開、シーク発行） | `frame_lock` | `gst_element_set_state(PLAYING)` / `gst_element_seek` / `gst_element_send_event` |

症状は TS 素材で約 6%（31 回中 2 回）のハング。全スレッド待機（37 本、Wait/UserRequest 33、
Wait/Unknown 3、Wait/EventPairLow 1）で CPU を消費せず、5 例中 4 例が完全に同じ構成だった。

**教訓 1: コメントに書いた規則は新しいロックには引き継がれない。** `frame_lock` については回避していたのに、
新設した `state_mutex` で同じことをした。**規則は不変条件として一箇所に書き、変更のたびに照合する。**

**教訓 2: 確率的なハングは決定的に再現させてから直す。** `TCS_TEST_HOLD_FRAME_LOCK_MS`（`frame_lock` を
意図的に保持するテストフック）を入れたことで、6% の事象が 100% 再現するようになり、
修正前 2/2 ハング・修正後 2/2 完走という明確な証拠が取れた。**自然実行の反復だけでは因果を示せない。**

## 実装側が単独で決めてはいけないこと（必ず質問）

- デバイス構成（別デバイス／同一デバイス）、スレッド構成、context の共有方法。
- バッファリング方針（FIFO／latest／滞留上限）、破棄規則。
- 表示ゲートや lead 制御の規則変更。
- 保存形式（`ProjectData`／`AppSettings`）の項目追加・意味変更。
- 終了順序・デバイス消失時の挙動。
- 合格条件の緩和や「設計差異として記録して先へ進む」判断。

質問は「選択肢＋根拠＋計測値」で。親が答えるまで該当部分は実装しない（他の部分は進めてよい）。

## 依頼プロンプトの型

```
基点 <SHA>、worktree <path>（cwd 固定）。不変条件: docs/OUTPUT-GPU-INVARIANTS.md（I1〜I12 を守る。触れる変更は質問）。
仕様: <段階の仕様文書>。変更ファイル・契約・管理テスト・実機の合格条件: <列挙>。
実機は 1 プロセスずつ、自分の PID のみ、開始前に時刻を報告。報告は コミット／変更ファイル／非E2E 件数／実機の指標／設計差異／未検証。
```
