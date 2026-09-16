# v0.4.1 のバージョン・CHANGELOG・リリースノート

作成: 2026-09-17 06:35、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（`061419f` 以降。D12〜D15 統合済み）** を `agent-a` へ通常マージしてから。**コードには触らない**（文書とバージョンだけ）。

## 1. やること

| # | 内容 |
| --- | --- |
| バージョン | `src/TimecodeSyncPlayer/TimecodeSyncPlayer.csproj` の `Version` を `0.4.1` に。`ApplicationVersionTests` のピンも `0.4.1` に。起動ログが `v0.4.1` になることは統合後に親が確認する |
| CHANGELOG | `CHANGELOG.md` に `## 0.4.1 - 2026-09-17`（英語、既存の 0.4.0 節と同じ体裁・CRLF）。Fixed: D12（audioresample、44.1kHz 音声の素材がロードできない）、D13（映像分岐の queue、映像トラック先頭の音声付き MP4 で preroll が止まる）、D14（不一致プロファイルの待ちを bus エラーで即打ち切り）、D15（同梱に gstaudioresample / gsttypefindfunctions / gio-2.0-0 を追加）。Added: `TCS_LOG_FILE`（shim のログを logs\tcs-gst-YYYYMMDD.log へ、7 日で清掃）、44.1kHz / 映像先頭の E2E 素材とテスト。Known limitations は 0.4.0 のまま |
| リリースノート | `docs/release-notes/v0.4.1.md` を**日本語**で（0.4.0 の `docs/release-notes/v0.4.0.md` と同じ構成: 冒頭の要約、修正、追加、検証の概要、動作要件）。冒頭に「v0.4.0 は 44.1kHz 音声付き素材が再生できない欠陥があり、この版で修正」と明記。検証の概要は親が後で埋めるので、開発機の項目（修正前後の 4 本、RMS、V5、V3、E2E）を箇条書きにし、検証機の項目は「（親が記入）」と置く |
| 計画文書 | `docs/release-0.4-plan.md` に「v0.4.1 の完了の定義」の小さな表を追加: (1) 開発機の修正前後 4 本、(2) E2E 全件（除去担当）、(3) 検証機で AV1 実素材 3 本 + 複製 2 本がロード・再生できる、(4) 配布物の起動確認（44.1kHz 素材を含む）。結果欄は空で |

## 2. 検証

- ビルド 0/0、非E2E 全件（`ApplicationVersionTests` を含む）
- `git grep` でローカルの絶対パスが 0 件、CHANGELOG が CRLF のまま

## 3. 報告

コミット、変更ファイル一覧、非E2E の件数。**合否は書かない。ローカルの絶対パスは書かない。** 実機は使わない。
