# E1: `GStreamerBackend_SurvivesRepeatedTrackSwitches` の判定が競合状態

worktree `C:\Users\<user>\Documents\timecode-sync-player-wt-output-engine-20260911-1247`（cwd 固定）。
main から新ブランチ `codex/e1-switch-test-20260912` を切る（基点は下記「基点」）。
不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。**製品コードは変更しない**（テストのみ）。

## 事実（親の計測、2026-09-12 23:24〜23:28 JST）

E2E 素材（`scripts/make-e2e-media.ps1` で生成）と受信ツール（`native/gst-shim/proto/build-proto.ps1`）を揃えたところ、
スキップされていた GStreamer E2E 3 件が動くようになり、**57 成功・1 失敗・4 スキップ**になった。
失敗は `GStreamerBackend_SurvivesRepeatedTrackSwitches`（`tests/.../E2E/GStreamerBackendE2ETests.cs:149` の `WaitUntil`）。

アプリのログを見ると**製品は正しく動いている**。当該 run のロード列（すべて `loadRc=0`、`loadfile 失敗` は 0 件）:

```
23:27:30.858  test_1080p60.mp4  index=0   <- --open による初回ロード
23:27:33.431  test_1080p60.mp4  index=0   <- プレイリスト初期化による 2 回目
23:27:33.672  test_720p25.mkv   index=1   <- next 1
23:27:34.356  test_720p25.avi   index=2   <- next 2
23:27:35.039  test_720p50.ts    index=3   <- next 3
23:27:35.740  test_720p25.avi   index=2   <- prev 1
23:27:36.442  test_720p25.mkv   index=1   <- prev 2
23:27:37.141  test_1080p60.mp4  index=0   <- prev 3
```

mkv（matroskademux）・avi（avidemux）・ts（tsdemux）すべてが GStreamer 経路で読めており、切替も全部成功、
アプリも生存している。**新しい被覆として良好。**

失敗の理由は判定側にある。テストは `E2EAppRunner.Start` の直後に `loadsBefore` を数え、
その後 `loadsBefore + 7` 以上になるのを待つ。しかし初回ロードは**2 回**起き（`--open` とプレイリスト初期化）、
2 回目は切替開始のわずか 0.24 秒前。`loadsBefore` がこの 2 件を数え終えた後に採られると、
切替 6 回では `+6` にしかならず `+7` に届かない。採取タイミング次第で成否が変わる。

## 依頼

1. 判定を**決定的**にする。`LoadFile path=` の総数ではなく、切替で読まれた**トラックの並び**で判定するのが良い
   （ログの `Playlist track loaded index=` は index と名前が出るので、`0,1,2,3,2,1,0` の順で index が現れることを確認できる）。
   方法は任せるが、初回ロードが 1 回でも 2 回でも成否が変わらないこと。
2. 失敗時に原因が分かるよう、期待と実際のロード列をメッセージに出す。
3. `loadfile 失敗` が 0 件であることの確認は残す。
4. **製品コードは変えない**。初回ロードが 2 回起きる点は別途 E2 として記録済みなので、ここでは触らない。

## 合格条件

- `dotnet test --filter "Category=E2E"` が **58 成功・0 失敗・4 スキップ**（残る 4 件は環境変数で有効化する既存のもの）。
- 同テストを 3 回連続で実行して安定して通ること（競合状態が消えたことの確認）。
- 非E2E 全件成功（現状 1712 件）。
- 1 コミット（テストのみ）。

## 基点

main の最新（`git log --oneline -1 main` で確認する）。E2E 素材は `scripts\make-e2e-media.ps1` で作る
（`-OutDir <worktree>\artifacts\media`）。受信ツールは `native\gst-shim\proto\build-proto.ps1`。
どちらも実行しないとテストは Skip される。

報告は コミット／変更ファイル／非E2E 件数／E2E の結果（3 回分）／設計差異／未検証。
