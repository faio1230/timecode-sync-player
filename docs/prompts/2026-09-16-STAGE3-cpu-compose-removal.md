# 段 3: CPU 合成の除去

作成: 2026-09-16 21:10、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`。**新しいセッション**）。
基点: **main の最新（段 2 後半を統合した後）** を `agent-b` へ通常マージしてから始める。
計画: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` の「段 3: CPU 合成の除去」。前提の決定は利用者（2026-09-14）。

段 2 の完了状態: mpv の再生経路・実装・DI は削除済み（`c9d62ee`〜）。出荷構成は GStreamer + GPU 合成のみ。E2E 全件 63/63。
GPU 合成が使えない環境の見せ方（計画の判断 1）は段 1 で実装済み（起動時ダイアログ、再生は無効のままアプリは開く）。

---

## 0. 先に読むもの

- **前任セッションの実装側メモ**: `docs/prompts/2026-09-16-STAGE2-mpv-removal.md` の末尾「段 3 で引っかかりそうな箇所」（D4 のロード安定ゲートの 2 経路、
  `RenderSession.PublishSnapshot` で残すもの、`OutputBackendState` のプレースホルダ、shim の `tcs_player_leased_cpu_copy` の参照箇所、CPU Spout 系、`ForceExit` E2E の前提）
- `scripts/run-v3-accuracy.ps1` が settings.json に `"backend":1` を書いて廃止キー警告を出している件は、この段で `backend` キーを落としてよい（`outputBackend` は残す）

## 1. 親の決定

1. **`OutputBackend.Cpu` は消す。** `OutputBackendState` の CPU フォールバックと「初期化前プレースホルダが `Effective=Cpu`」も消す。
   GPU が使えないときは段 1 のダイアログ経路だけが残る（`Faulted` の扱いは今のまま）
2. **ギャップの Freeze は GPU 側で決める。** 今は `GetGapRenderDecision` が CPU 側のフリーズバッファの有無で `GapFreeze` を判定し、GPU 構成では
   バッファが埋まらないため `Hold` に落ちている（計画 1-3）。CPU バッファを消すのに合わせ、**判定は `ComposeLayer` の frozen（`SaveFreeze`）を根拠にする**。
   Freeze 指定なら GPU 構成で必ず `GapFreeze` になり、`ComposeLayer` が進入時のソース画像を保存して描く。**これは挙動の変更なので、非E2E で固定し、実機で確認する**
3. **shim の `tcs_player_leased_cpu_copy` は C ABI ごと消す**（I13 の管轄。`frame_lock` の規則は変えない）。`GstMpvRenderApiAdapter.Render*` と
   `GstBackendState.RenderInto`（`LeasedCpuCopy`）も消す
4. **GPU 構成のプレビュー（合成層の 960×540 読み戻し）が使う型は残す。** `FrameRenderer` / `PreviewFramePresenter` / `RenderFramePublish*` / `PixelBufferManager` /
   `StartupBufferInitializer` のどれを読み戻し経路が使っているかを**消す前に grep して対応表で報告**し、CPU 合成専用のものだけ消す
5. `RenderSession` はファイルごと消さない。スナップショット経路（`RenderFrameWorker`、`RenderFrameParameterBuilder`、`MpvRenderFrameExecutor`、
   `RenderedFrameSnapshot`、`LatestRenderedFrameMailbox`、フリーズバッファ）だけ消し、**フレーム通知の駆動と寿命管理は残す**。
   `RenderSessionTests` は分割し、`NativeCallback_*` / `Callback_*` / `Dispose_*` を残す
6. 参照されていない `RenderWorkerShutdownWaiter` とそのテストは消す
7. 設定互換: `outputBackend:0` は段 2 の互換処理（無視して警告 1 行）のままでよい。**enum から `Cpu` を消しても、その処理が壊れないこと**をテストで固定

## 2. 検証（計画 4 節）

| 項目 | 内容 |
| --- | --- |
| ビルド | shim Debug、アプリ、テスト。警告 0・エラー 0 |
| shim テスト | `--policy-only` と実素材。failures=0 |
| ロック規則 | `check-shim-lock-rule.py` PASS |
| 非E2E | 全件（減った件数と名前、増えた件数を報告） |
| grep | `OutputBackend.Cpu`、`FallbackApplied`、`FrameRenderer`（読み戻しに残す場合はその旨）、`RenderFrameWorker`、`RenderedFrameSnapshot`、`LeasedCpuCopy`、`tcs_player_leased_cpu_copy` → 0 件（`docs/` の過去記録は除く） |
| E2E | **全件**。基準は段 2 後半の 63 実行・63 合格 |
| 実機 1 | V3 を Smooth で 1 本（`run-v3-accuracy.ps1 -Backends gst -Label s3-<SHA>`）。定常誤差が段 2 後半（sample 平均 -28.5、p95-p5 35.0）から大きく外れないこと。**freeze-sweep で正しい絵が保持されること**（ハーネスが判定する。ここが決定 2 の実機確認） |
| 実機 2 | 1080p 1 本 `Invoke-AppGpuTrial.ps1 -MediaPath <artifacts\media\test_1080p60.mp4> -Label s3-<SHA> -Seconds 30 -PlayerBackend Gstreamer -TestCardOnAtSeconds 10 -TestCardOffAtSeconds 20`。実フレーム 60/秒、error 0、exit 0 |

## 3. 守ること

- 不変条件 `docs/OUTPUT-GPU-INVARIANTS.md` I1〜I13。**I12（Cpu backend は不変）はこの段で失効**（親が文書を直す）
- 型名にランタイム名を入れない（新しく作る型だけ。既存の改名は段 4）
- **公開リポジトリ**: 文書・コミット・コメントにローカルの絶対パスを書かない
- 実機を使う前に一報。同期担当（U1）と重ねない。順番は親が決める
- main への書き込みはしない。コミットは `agent-b`、日本語。**3〜4 コミットに分けてよい**（例: OutputBackend と状態機械 / RenderSession のスナップショット経路 / CPU の描画・Spout・全画面 / shim の ABI）
- 報告: コミット、削除ファイル一覧と対応表（決定 4）、非E2E の増減、grep、E2E 全件の件数（失敗の名前）、実機 1・2 の数字、設計差異、未検証。合否は書かない
