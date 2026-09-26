# v0.5.3 段 3g-1: ギャップの読み込み 2 経路の「別の口」の設計案（w5:p3 向け、コードは変えない）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`480502c` 以降。v0.5.3 と同じ先頭）。**コードは変えない。md を 1 つ書くだけ。**

## 背景

段 3a の表 `docs/design/v0.5.3-load-paths.md` の #3（ギャップの `LoadPausedAt`）と #4（Freeze の取り込みの読み直し `GapFreezePathGuard`）。
そのまま `BeginFileLoad` に通すと、一時停止のままロード解除が最大 5 秒遅れ、その間すべてのシークが止まり、着地窓がギャップ明けと重なって、Freeze の取り込みが壊れる。
利用者の決定（2026-09-26）: **「ロード中の印を立てずに、必要なものだけを切る別の口」を作って v0.5.3 で直す。**

## やること

`docs/design/v0.5.3-gap-load-entry.md`（新規）に、別の口（仮の名前 `TimecodeSyncService.BeginGapFreezeLoad(...)` など）の案を書く:

1. 段 0 の表の `FileLoadWithoutBegin` で「意図 = 消える」の 9 ラッチ（3a の表 §1）と、`loadingFile`・`seekLandingActive`・`followStartLanding`・`rateRestorePending` について、
   1 つずつ「別の口で消す／残す／立てない」と、その理由を表にする。**判断の基準は「Freeze の取り込みとギャップ明けの処理が、今と同じに動くこと」と「前のファイルの状態を次へ持ち越さないこと」**
   - 例: 読み込み番号は進める（前のファイルの端へのシークの記録を持ち越さない）、ロード中の印は立てない（シークを止めない）、着地窓は開かない（ギャップ明けの NotifyLanding に任せる）、位置の信頼は…（3a の所見 3 の指摘を踏まえて判断）
2. その口を呼ぶ場所（#3 の `GapEnterCoordinator` の 2 か所、#4 の `GapFreezePathGuard`）と、#4 の 1 秒ごとの読み直しで繰り返し呼ばれても害がないか
3. できごと（`SyncLifecycleEvent`）を足すか: 足すなら名前と source（例 `GapFreezeLoad`、source `load-paused-at` / `path-guard`）と、ログが 1 秒ごとに出てよいか
4. 段 0 の表で「現状」が変わる行の一覧（`FileLoadWithoutBegin` の行はハーネスの `LoadCurrentFile` で起こしている。その手順がどの経路に当たるかも確かめて書く）
5. 危険と確かめ方: どの既存のテスト・E2E（F-1〜F-5、G-1〜G-6 など）がこの変更で影響を受けうるか

## 規則

- **コードは変えない。git の書き込み禁止**（md を 1 つ作るだけ）
- 迷った項目は「保留」として理由つきで分けて書く
- 終わったら、表の行数・保留の数・所見 5 行以内をこの画面に書く
