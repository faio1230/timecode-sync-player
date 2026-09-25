# v0.5.3 段 3d: ロード中の印を、同期の無効化と停止で取り消す（§6 の 3）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`e305ba6` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 3（`_isLoadingFile` が停止・同期無効で消えず、何秒も後の無関係な時点で「ロード解除」が起きて着地窓が開く）と §3 段 3。

## 直す行（段 0 の表）

`LoadingFile` × `SyncEnabledOff` / `StopPlayback` の 2 行（今は「現状 Keeps・意図 Clear」）。ほかの行は変えない（`FileLoadWithoutBegin` の行は §6 の 2 の判断待ち）。

## やること

1. **先に赤を確かめる**: 2 行の「現状」を `Clears` に書き換え、根拠を「v0.5.3 段 3d で取り消すようにした（…）」に直す。今のコードで赤になることを確かめる
2. 直す（親の決め方）:
   - `FileLoadState` に「取り消し」を足す: ロード中の印と解除の回収待ちを両方下ろす。**解除はしない**（`ReleaseFileLoad` を通らない。着地窓を開かない、`_lastSyncSeekAt` も変えない）。
     `IsLoadingFile` の `volatile bool` の鏡も一緒に下ろす
   - `TimecodeSyncService.OnLifecycle` の `SyncDisabled` と `PlaybackStopped` で取り消す。`PlaybackStopped` は今サービスへ届いていないので、`LtcSyncController.PlaybackStopped()` から `_syncService.OnLifecycle(PlaybackStopped)` を呼ぶ
   - ロード中だったときだけログを 1 行: `Timecode sync: file load cancelled by {Event}`（既存のログの文言は変えない）
3. **既存のテストの期待値は変えない。** 変えないと通らないテストが出たら、変えずにその名前と理由をこの画面に書いて止まる（段 3c で、期待値の変化が欠陥の証拠だったため）
4. テスト: 1 の 2 行が緑になること。加えて「同期を切った後に何秒待っても、ロード解除（`file load released`）が起きず着地窓も開かない」ことを確かめるテストを 1 件
5. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の表は全緑）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行・赤→緑・変えたファイル・テストの件数をこの画面に書く
