# v0.5.3 段 3f: 直前の着地の記録を、読み込みで忘れる（§6 の 10）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`75e3388` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 10（`TimecodeSyncSeekState` の直前の着地の記録 `_lastSettledAt` / `_lastSettledTargetSeconds` が読み込みで消えず、
読み込み直後 0.5 秒、前のファイルの着地目標でシークが抑えられる）と §3 段 3。

## 直す行（段 0 の表）

`LastSettledRecent` × `FileLoad` の 1 行だけ（今は「現状 Keeps・意図 Clear」）。`FileLoadWithoutBegin` の行は §6 の 2 の判断待ちなので変えない。ほかの行（意図は未定）も変えない。

## やること

1. **先に赤を確かめる**: その 1 行の「現状」を `Clears` に書き換え、根拠を「v0.5.3 段 3f で忘れるようにした（…）」に直す。赤になることを確かめる
2. 直す: `TimecodeSyncSeekState`（と `ITimecodeSyncSeekState`）に、直前の着地の記録だけを忘れる口（例: `ForgetLastSettled()`）を足し、
   `TimecodeSyncService.OnLifecycle` の `FileLoad` で `_seekState.Clear()` の後に呼ぶ。`Clear()` そのものの意味は変えない（ほかの呼び出し元に影響させない）
3. **既存のテストの期待値は変えない。** 変えないと通らないテストが出たら、変えずに名前と理由をこの画面に書いて止まる
4. テスト: 1 の行が緑。加えて「着地の直後に読み込むと、新しいファイルでの最初のシークが 0.5 秒待たずに出る」ことを確かめるテストを 1 件（`TimecodeSyncServiceTests` か `TimecodeSyncSeekStateTests`）
5. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の表は全緑）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）。
   **非 E2E は `--logger "trx;LogFileName=non-e2e.trx"` を付けて回し、失敗があれば trx からテスト名を書く**（段 3e で名前の取れない間欠が 2 回出たため）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行・赤→緑・変えたファイル・テストの件数をこの画面に書く
