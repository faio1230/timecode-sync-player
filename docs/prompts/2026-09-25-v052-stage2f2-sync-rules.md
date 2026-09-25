# v0.5.2 段 2f-2: 軸をまたぐ条件を SyncRules（純関数）に集める（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`。**振る舞いは変えない。**
入力は 2f-1 の表 `docs/design/v0.5.2-cross-axis-rules.md`。とくに末尾の「親の承認と 2f-2 の範囲」を読むこと（R1〜R7 だけを扱う）。

## やること

1. `src/TimecodeSyncPlayer/SyncRules.cs` に `internal static class SyncRules` を作り、R1〜R7 を 1 つずつ純関数（副作用なし、引数だけで決まる）として書く。
   関数名は規則の意味で付ける（例: `CanEvaluateCorrection`、`CanApplySync`、`CanReapplyLastAccepted` …）。引数は必要な値だけ（小さな record でも個別の引数でもよい）
2. 各呼び出し元の条件式を、その関数の呼び出しに置き換える。**置き換えの前後で真偽が必ず同じになること**:
   - 条件の項を足さない・減らさない・まとめない（R2 と R3 は IsPlayerReady の有無が違う。別の関数のまま）
   - 早期 return が複数あった所（R1）は、途中に副作用が無いことを確かめてから 1 つにまとめる。副作用があれば、まとめずに報告する
   - 呼び出し元が今と同じ値を読むこと（プロパティの読み取り回数が変わっても副作用が無いことを確かめる）
3. 関数の外に残すもの: 状態の変更（着地値のラッチ、`PollFileLoadRelease` の回収、`_pausedByPolicy = true` など）とログ
4. 設計書 `docs/design/v0.5.2-cross-axis-rules.md` の R1〜R7 の表に「関数名」の列を足す（この md の編集は可）

## テスト

- `tests/TimecodeSyncPlayer.Tests/SyncRulesTests.cs`: 規則ごとに真理値表のテスト（各項を 1 つだけ偽にした場合がすべて期待どおり、を含む）
- 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま）、
  E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`。**LTC シナリオは外す。L-3 は既定 12 時間**）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 迷ったら「今の振る舞いを変えない」側を選び、報告に書く
- 終わったら、変えたファイル・規則ごとの置き換えた場所・2 番で見つけたこと・テストの件数をこの画面に書く
