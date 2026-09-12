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
