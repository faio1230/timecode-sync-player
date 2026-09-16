# S4: 段 4（型付き再生 API）の設計調査（実装はしない）

作成: 2026-09-16 22:15、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（U1 統合後）** を `agent-a` へ通常マージしてから始める。**コードは変えない。成果は文書 1 本。**

## 1. 背景（`docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` 1-5、2 節「段 4」、3 節「判断 2」）

- 利用者の決定（2026-09-15）: 型名にランタイム名を入れない。親の推奨は**案 B（型付きの操作 API に置き換える）**
- 現状: アプリは mpv の文法（`no-osd loadfile "<path>" replace -1 start=...`、`seek 12.346 absolute+exact`、`pause=yes`）で文字列を組み立て、
  `GstCommandTranslator` が解析し直して shim を呼ぶ。`IMpvApi` 越しの文字列呼び出しは約 60 か所（MainWindow 32、`MpvStartupPropertyApplier` 10、
  `PlaybackOperationsCoordinator` 8 ほか）。知らないコマンドは黙って 0 を返す。相対シークの秒数書式に `InvariantCulture` が無い
- 段 2 で mpv 実装は消えた。段 3（CPU 合成の除去）が別担当で進行中。段 4 は段 3 の後

## 2. 依頼: 置き換えの設計材料を揃える

1. **呼び出しの棚卸し**: `IMpvApi` / `IMpvRenderApi` 越しの呼び出しを全部列挙する（ファイル、メソッド、渡している文字列の形、戻り値の使い方）。
   `GstCommandTranslator` が解釈しているコマンド・プロパティの一覧と、**解釈されずに 0 を返しているもの**（`osd-bar`、`osd-msg3`、`hwdec` など）を分ける
2. **型付き API の案**: 上の棚卸しから、必要なメソッドを最小限で定義する（例: `Load(path, startSeconds, paused)`、`Seek(seconds, exact)`、`SeekRelative(delta)`、
   `SetPaused(bool)`、`SetRate(double)`、`SetVolume` / `SetMute`、`TryGetTimePos`、`TryGetDuration`、`Stop`、イベント）。
   各メソッドについて「今どの文字列呼び出しがそれに対応するか」「戻り値と失敗の表現（例外か結果型か）」「スレッド（UI／音声／shim コールバック）」を表にする
3. **消えるもの**: `MpvPlaybackCommandBuilder`、`GstCommandTranslator`、`MpvStartupPropertyApplier`、mpv 用語の定数、`MpvSessionInitializer` の役割の行き先
4. **残る mpv 名**: `IMpvApi` / `IMpvRenderApi` / `GstMpvApiAdapter` / `GstMpvRenderApiAdapter` / `MpvRenderFrameExecutor` の**中立名の案**（役割で命名。ランタイム名を入れない）
5. **順序とリスク**: 一度に置き換えるか、呼び出し側から段階的に移すか。E2E 全件と V3 1 本で守れる範囲と、守れない箇所（シーク書式の `InvariantCulture` など）
6. **段 3 との衝突**: 段 3 が触る `RenderSession` / `GstBackendState` / `GstMpvRenderApiAdapter` と重なる箇所を明示する（段 4 は段 3 の後に始めるため）

## 3. 成果物

`docs/V04-STAGE4-TYPED-API-SURVEY-2026-09-16.md` 1 本。表を中心に、判断は書かず材料と選択肢＋根拠を書く。**コードは変えない。**
実機は使わない。コミットは `agent-a`、日本語。ローカルの絶対パスを書かない。
