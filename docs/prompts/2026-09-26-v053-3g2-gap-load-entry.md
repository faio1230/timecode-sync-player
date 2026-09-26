# v0.5.3 段 3g-2: ギャップの読み込み 2 経路に「別の口」を作る（§6 の 2 の残り）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`。
設計は `docs/design/v0.5.3-gap-load-entry.md`。**末尾の「親の承認と変更」を先に読むこと**（コントローラの 3 ラッチは下ろさない、保留 3 点は触らない）。

## やること

1. **先に赤を確かめる**: 段 0 の表の `FileLoadWithoutBegin` の 4 行（`fileLoadReleasePending`・`pendingSeek`・`lastSettledRecent`・`boundarySeekTarget`）を `Clears` に、
   `jumpAppliedOnce`・`heldReapplyDone`・`smoothUnavailable` の 3 行は現状 Keeps のまま根拠を「v0.5.3 では直さない（ギャップの読み込みは Jump の適用の途中で起きる。設計 `v0.5.3-gap-load-entry.md` の親の承認）」に直す。
   ハーネスの `LoadCurrentFile`（`SyncScenarioHarness`）を、読み込みの後に新しい口（source `load-paused-at`）を呼ぶ形にし、`LatchLifetimeScenario` の `FileLoadWithoutBegin` のコメントを「ギャップの 2 経路」に直す。赤を確かめる
2. 直す:
   - `SyncLifecycleEvent` に `GapFreezeLoad` を足す
   - `TimecodeSyncService` に `internal void BeginGapFreezeLoad(string source)`: 記録 → 読み込み番号を進める → 解除の回収待ちを下ろす → `_seekState.Clear()` → `_seekState.ForgetLastSettled()`。**それ以外はしない**（設計 §1 の「しない」と承認の変更のとおり）
   - 呼ぶ場所: `GapEnterCoordinator` の 2 か所（`LoadPausedAt` が成功した後、`GapEnterEffects` に既定値つきの `Action<string>? BeginGapFreezeLoad` を足す）と、`MainWindow.IsCurrentPathExpectedForGapFreeze` の読み直しが成功した後（source `path-guard`）
3. テスト:
   - `V053LoadPathTests` の #3・#4 の Skip を外す。**`positionUntrusted` は設計で「残す」ので、その 1 キーの期待は「true のまま」に変える**（この 2 件は段 3a で書いた赤いテストで、回帰テストではない。変えた理由をテストのコメントに書く）
   - `SyncLifecycleLogTests` の `FileLoadWithoutBegin` の期待を `["GapFreezeLoad/load-paused-at"]` に
   - 口の単体テスト（消すもの・立てないもの・開かないもの・繰り返しても害がない）
   - **それ以外の既存のテストの期待値は変えない。** 変えないと通らないものが出たら、変えずに名前と理由をこの画面に書いて止まる
4. 判定: ビルドの警告 0、非 E2E 全件合格（trx に書き出す）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行・赤→緑・変えたファイル・テストの件数をこの画面に書く
