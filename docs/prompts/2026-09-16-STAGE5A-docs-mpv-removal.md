# 段 5 前半: 文書から mpv 前提を外す（CPU 合成の記述は段 3 完了後に改める）

作成: 2026-09-16 22:45、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新** を `agent-a` へ通常マージしてから始める。**コードは変えない。文書だけ。**

## 1. 背景

段 2 で mpv は main から消えた（`0943e2f`）。`CLAUDE.md` は段 2 で直したが、利用者向け・開発者向けの文書には mpv 前提が残っている
（2026-09-16 22:40 時点: `docs/ARCHITECTURE.md` に 30 か所、`docs/verification-checklist.md` に 6 か所。`docs/SETUP.md`、`native/README.md`、`README.md` は 0）。
段 3（CPU 合成の除去）は別担当で進行中なので、**CPU 合成・WriteableBitmap・Spout の CPU 経路の記述は今回は触らず**、段 3 の統合後に改める。

## 2. やること

1. `docs/ARCHITECTURE.md`: mpv（libmpv、`mpv-2.dll`、`vo=libmpv`、mpv の SW render、`MpvSnapshotSource`、`PlayerBackend` など）を GStreamer shim 前提に書き換える。
   データフロー図・主要コンポーネントの役割・スレッドモデルを現状のコード（`Gst/`、`Output/GStreamerSource`、`OutputEngine`、`RenderSession`）に合わせる。
   **CPU 合成の記述は「段 3 で除去予定」と注記して残す**（消さない）
2. `docs/verification-checklist.md`: mpv 前提の項目を GStreamer の項目に置き換えるか削除する。実機で確認する項目は、`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の V1〜V11 と整合させる
3. `docs/SETUP.md`: mpv の記述が本当に 0 かを確認し、`LtcFpsMode=fixed`（29.97 は自動判別できない）と `SyncOffset` の合わせ方（LTC 発生器・映像出力の遅延を含めて現場で合わせる。
   2026-09-16 の利用者の決定: 1 フレーム分の定数は製品では足さない）が書かれているかを確認して、無ければ足す
4. `docs/` 直下で mpv を前提にしている他の文書（`git grep -il mpv docs/*.md` で当たる中から、**過去の記録・指示書・判定文書は除く**）があれば一覧にして報告（書き換えは今回しない）

## 3. 守ること

- **過去の記録（`docs/GSTREAMER-GPU-VALIDATION-PLAN-*.md`、`docs/prompts/*`、`docs/*HANDOVER*`、判定・測定の文書）は書き換えない**。当時の事実の記録
- **公開リポジトリ**: ローカルの絶対パス（`C:\Users\<ユーザー名>`、外付けドライブ、ホスト名）を書かない。プレースホルダー `C:\Users\<user>`、`<repo>`
- 実機は使わない。コードは変えない
- main への書き込みはしない。コミットは `agent-a`、日本語
- 報告: コミット、書き換えた箇所の要約（ファイルごと）、4 の一覧。合否は書かない
