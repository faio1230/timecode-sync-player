# 段 5 後半: 文書・配布物・リリースノート（v0.4）

作成: 2026-09-17 02:20、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（段 4 統合後）** を `agent-b` へ通常マージしてから始める。
計画: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` 段 5。完了の定義: `docs/release-0.4-plan.md`。

## 1. 現在地

- 段 1〜4 は main に統合済み。mpv・CPU 合成・mpv 文法の文字列経路はすべて消え、再生は `IPlaybackApi` / `GstPlaybackApi`、出力は GPU 合成のみ
- 段 5 前半（`docs/ARCHITECTURE.md` / `docs/verification-checklist.md` / `docs/SETUP.md` の mpv 前提の除去）は同期担当が実施済み。ただし **CPU 合成の記述は「段 3 で除去予定」の注記付きで残っている**し、段 4 の型名（`IPlaybackApi` など）は反映されていない
- 既知: `GStreamerBackend_ClosesGracefullyDuringPlayback` はテスト側の欠陥（Q3、同期担当が修正中）。`LtcHardwareLoopE2ETests` の 1 件が全件実行でだけ 2 回落ちた（単体は合格。要観察）

## 2. やること

1. **`docs/ARCHITECTURE.md`**: CPU 合成・`WriteableBitmap`・`FrameRenderer`・`RenderSession` のスナップショット経路の記述を削除し、現行（GStreamer shim → 共有リング → GPU 合成 → Spout / プレビュー読み戻し）に書き直す。
   再生 API（`IPlaybackApi` / `PlaybackResult` / `IRenderUpdateSource`）の役割を追加。「段 3 で除去予定」の注記は消す
2. **`docs/SETUP.md`**: `outputBackend` の説明（`0`/Cpu は廃止、v0.3 の設定は無視して警告）、GStreamer ランタイムの導入、`tcs_gstreamer.dll` のビルド、LTC fps モード、SyncOffset の方針が現行と合っているか通読して直す
3. **`docs/verification-checklist.md`**: CPU 合成前提の項目を消し、V1〜V11 の参照を確認
4. **`CLAUDE.md`**: プロジェクト構成の木（消えたファイル・新しいファイル）、データフロー、既知のクセを現行に合わせる（段 2 で一度直したが、段 3・4 の変更を反映）
5. **`native/README.md`** と **`native/gst-shim/README.md`**: `LeasedCpuCopy` / `OutputBackend=Cpu` の記述を削除し、D8（リング epoch）と D10（stream time）を反映
6. **リリースノート草案**: `docs/release-0.4-plan.md` 3 節を更新（Added / Changed / Removed / Fixed / Known issues）。今日までの事実だけ:
   mpv・CPU 合成の除去、型付き再生 API、既定は GStreamer + GPU 合成、v0.3 設定の互換、D8 / D10 / T2（サンプル時計）/ U1 / Q1、V3 の基準（sample）、
   既知の制限（29.97 は fixed 指定、D9 未対応、1 フレーム定数は足さない）。**判断が要る事項は「利用者の判断待ち」に残す**
7. **配布物**: インストーラー／配布スクリプトが `libmpv-2.dll` / `mpv-2.dll` を含んでいないか、`tcs_gstreamer.dll` と GStreamer 同梱（P1 で決定済み）の記述と合っているかを確認して直す。
   `THIRD-PARTY-NOTICES.md` から mpv/libmpv の項を外す（ある場合）
8. `docs/` 直下で現行前提として残る mpv 記述（段 5 前半の報告にある `OUTPUT-PIPELINE-DESIGN.md`、`OUTPUT-GPU-DESIGN-CONFIRMED.md`、`GPU-SOURCE-CONTRACT-SPEC.md`、`OUTPUT-GPU-INVARIANTS.md` の I6・I8、`OUTPUT-FRAME-PIPELINE.md`、`MONKEY-TESTING.md`）を現行に合わせる。
   **過去の記録・指示書・判定文書は書き換えない**

## 3. 守ること

- コードは変えない（配布スクリプトの mpv 除去を除く）。実機は使わない
- **公開リポジトリ**: ローカルの絶対パス（`C:\Users\<ユーザー名>`、外付けドライブ、ホスト名）を書かない
- 過去の記録（`docs/GSTREAMER-GPU-VALIDATION-PLAN-*.md`、`docs/prompts/*`、`*HANDOVER*`、`V04-*`、判定・測定文書）は書き換えない
- main への書き込みはしない。コミットは `agent-b`、日本語。文書ごとにコミットを分けてよい
- 報告: コミット、書き換えた箇所の要約（ファイルごと）、配布物の確認結果、リリースノート草案の要点。合否は書かない
