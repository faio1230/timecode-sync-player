# v0.5.3 段 3e: Jump と保持値の 1 回適用のラッチを、読み込み・モード切替・同期の有効化・手動シークで下ろす（§6 の 5）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`f787c79` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 5（`_jumpAppliedOnce` / `_heldReapplyDone` が消えず、次の Normal フレームまで Jump／保持値の適用が 1 回抑えられる）と §3 段 3。

## 直す行（段 0 の表）

`JumpAppliedOnce` と `HeldReapplyDone` × `FileLoad` / `SyncModeChanged` / `SyncEnabledOn` / `ManualSeek` の 8 行（今は「現状 Keeps・意図 Clear」）。
`FileLoadWithoutBegin` の 2 行は §6 の 2 の判断待ちなので変えない。

## やること

1. **先に赤を確かめる**: 8 行の「現状」を `Clears` に書き換え、根拠を「v0.5.3 段 3e で下ろすようにした（…）」に直す。赤になることを確かめる。
   ほかの行が連動して変わる（前提で消えて NotApplicable になる等）ときは、その行も直し、報告に全部書く
2. 直す（親の決め方）:
   - `LtcInputState` の 2 つのラッチ（`ClearJumpApplied` / `ClearHeldReapplied`）を、`LtcSyncController.OnLifecycle` の `SyncModeChanged`・`SyncEnabled`・`ManualSeek`（と `TimelineSeek`）で下ろす
   - `FileLoad` は `TimecodeSyncService.BeginFileLoad` の中で起きるので、コントローラには届いていない。サービスに内部のイベント（例: `internal event Action<SyncLifecycleEvent>? LifecycleRaised`）を足し、
     `BeginFileLoad` の `OnLifecycle(FileLoad)` の後で上げる。コントローラはコンストラクタで購読し（`SeekIssued` と同じやり方）、`FileLoad` のときだけ 2 つのラッチを下ろす。
     **購読のデリゲートはフィールドで持つ必要はない**（マネージドのイベントなので GC の問題はない。CLAUDE.md の注意はネイティブへのコールバックの話）
   - ログは足さない（ラッチを下ろすだけ。できごとの行はすでに出ている）
3. **既存のテストの期待値は変えない。** 変えないと通らないテストが出たら、変えずにその名前と理由をこの画面に書いて止まる
4. テスト: 1 の 8 行が緑。加えて「Jump を 1 回適用した後に手動シークし、次の Jump がまた 1 回適用される」ことを確かめるテストを 1 件
5. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の表は全緑）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行（連動した行も）・赤→緑・変えたファイル・テストの件数をこの画面に書く
