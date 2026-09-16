# 段 2 の前半: mpv を消す前の「移設」（振る舞いを変えない）

作成: 2026-09-16 18:50、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main `45534a9`**（D8 統合済み）を `agent-b` へ通常マージしてから始める。
計画: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` の「段 2: mpv の除去」。**この指示書はその前半（先に移設）だけ。削除はまだしない。**

段 1 の状態: 既定は出荷構成（GStreamer + GPU）、D8 は修正済み、R1 の (b) も統合済み。残る失敗は `ProjectRoundTrip`（構成非依存の既存失敗、別件）。
親は段 1 を「完了」と判定する（記録は親が書く）。

---

## 1. やること（計画の「先に移設（振る舞いを変えない）」をそのまま）

1. `MpvLibraryNameResolver` の **GStreamer の DLL 振り分け**を中立な名前のファイルへ移す（例 `NativeLibraryResolver`）。mpv の解決は残す（後半で消す）
2. `MpvRenderNative` の型 `MpvRenderParam` / `MpvRenderUpdateFn` を**中立名**（例 `RenderParam` / `RenderUpdateFn`、置き場は `Contracts/` か `Output/`）で定義し、GStreamer 経路（`IMpvRenderApi`、`GstBackendState`、`RenderSession`）をそちらへ向ける。mpv の P/Invoke 本体は残す（後半で消す）
3. `SpoutOutput.DefaultSenderName` を中立な場所へ移し、GPU 経路の参照を差し替える
4. `MpvSnapshotSourceTests` に同居している GPU 共通テスト 2 件（`TimelineOutputMailbox_KeepsOnlyLatestState`、`ComposeLayerPolicy_HoldsWithoutInsertingBlackForNotReady`）を独立したテストファイルへ移す
5. `OutputEngine` で **GStreamer ソースが未接続の間（起動〜接続、GPU 復旧中）に何を合成するか**を調べて報告する（今は mpv 分岐の合成が走る。計画 2 節）。
   **実装はしない。** 現状の経路と、置き換え案（黒 or Hold）を選択肢＋根拠で書く。親が決める

## 2. 守ること

- **振る舞いを変えない。** 名前と置き場の変更だけ。非E2E の件数は減らさない（移設したテストは同数）
- 型名にランタイム名を入れない（`IMpvApi` などの改名は段 4 でまとめてやる。ここでは新しく作る型だけ中立名にする）
- `docs/OUTPUT-GPU-INVARIANTS.md` I1〜I13。I12（Cpu backend は不変）はまだ有効
- **公開リポジトリ**: 文書・コミット・コメントにローカルの絶対パスを書かない
- 実機は使わない想定。使う場合は一報
- main への書き込みはしない。コミットは `agent-b`、日本語

## 3. 検証と報告

- ビルド 0 警告 0 エラー、非E2E 全件、`check-shim-lock-rule.py` PASS（shim は触らないはず）
- E2E は **`GStreamerBackend_SurvivesRepeatedTrackSwitches` と `ExitDialogE2ETests` の 2 つだけ**（移設で DLL 解決とレンダー契約を触るため。実機を使うので一報）
- 報告: コミット、移設の対応表（旧 → 新）、非E2E 件数、E2E 2 件の結果、5 の調査結果（選択肢＋根拠）。合否は書かない
