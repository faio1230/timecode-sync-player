# 報告: 出力トレースの保存（OutputTrace.Save）の例外でアプリが落ちる件

作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v054`。始める前に `git merge --no-ff v0.5.4` をした（f2f9c8c）。

## 事象（親から）

資源が尽きた条件（C: の空き 0.6 GB）で、出力トレースが有効なアプリが `OutputTrace.Save` の中の
`System.OutOfMemoryException` で落ちた（未処理、`OutputEngine` の GPU worker の終了処理の `settings.Trace.Save(...)` から）。

## 今あった守りの範囲（コードで確かめたこと）

- `Save` の本体（イベントの並べ替え・manifest.json・events.jsonl・summary.json の書き出し）は、すでに
  `try { ... } catch (Exception ex)` の中にあった。`catch (Exception)` は `OutOfMemoryException` も受ける。
- 守られていなかったのは catch の中の `Log.Warning(ex, ...)`。メモリが尽きた状態では警告の書き出し（例外の文字列化・
  sink への書き出し）でも例外が起き得て、それが catch の外、つまり `Save` の外へ出る。catch の時点ではイベントのキュー
  （既定の上限で最大約 170MB）も残っていた。
- 呼び出し側（`OutputEngine` の finally）では、`Save` の引数の `TryDiagnostics()` は自前の try/catch を持つ。
  `Save` の後に続くのは `gpuDone?.TrySetResult()`。

## 変更の要約

`src/TimecodeSyncPlayer/Output/OutputTrace.cs` の `Save` の catch だけを変えた。

- catch の中で、溜めたイベントのキューを空にして（トレースを破棄し、メモリを返す）から警告を 1 行試みる。
- この 2 つを内側の `try { ... } catch { }` で包み、警告も出せないときは何もせずに戻る（呼び出し側の終了処理が続く）。
- 警告の文言は変えていない（`scripts/ltc-run-report.ps1` の `出力トレースの保存に失敗` の数え方がそのまま当たる）。
- 判断・状態・定数・仕組みには触れていない。`OutputEngine` は変えていない。

## テスト

- 追加: `tests/TimecodeSyncPlayer.Tests/OutputTraceSaveFailureTests.cs`（1 本、collection "Serilog global logger"）。
  - 既に manifest.json がある置き場で保存させて、本体の書き出しを失敗させる（`CreateNew` の IOException）。
  - `Log.Logger` を `AuditTo.Sink` の sink に差し替え、警告の書き出しで `OutOfMemoryException` を投げる（AuditTo の sink の
    例外は呼び出し側へ返る。既存のテストが `Log.Logger` を差し替える作法の範囲。製品に口は足していない）。
  - 判定: `Save` が例外を外へ出さない、イベントが破棄されている（`Snapshot()` が空）、警告の試みがちょうど 1 回。
- 修正前: この 1 本が失敗（`OutOfMemoryException` が `Save` の外へ出た。sink の Emit から）。
- 修正後: この 1 本が成功。非E2E（`--filter "Category!=E2E"`）: 合計 2770、成功 2770、失敗 0、スキップ 0。
- 既存テストの期待は変えていない（赤くなった既存テストは 0）。

## 未解決の疑問

1. 本体の中で `OutOfMemoryException` が起きる形（イベントの並べ替え・直列化の途中）は、製品に口を足さないと単体では作れない
   ので試していない。本体の例外は修正前から catch に入る（変わったのは catch の中の守り）。
2. 実機の落ち方のスタックが、catch の中の `Log.Warning` からだったか（本体の中の例外が catch を抜けたのではない前提）は、
   ログのスタックの行で確かめていない。
3. 資源が尽きた条件では、`Save` の外（GPU worker の終了処理のほかの箇所、ほかのスレッド）でも同じ例外が起き得る。今回の範囲外。
