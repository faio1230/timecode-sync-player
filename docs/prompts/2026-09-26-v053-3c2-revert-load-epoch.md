# v0.5.3 段 3c の手直し: 読み込み番号でホールドを無効にする部分を取り消す（w5:p3 向け）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`。段 3c の未コミットの変更の上で直す。

## 理由（親の判断の誤り）

段 3c の指示で「読み込み番号が変わったらホールドは立っていないとみなす」としたが、これは誤りだった。
元のコードは、読み込みの後の最初の評価でホールドを**解除**していた（`ReleaseBoundaryHold` → `SetEndHold(false)` で再開、
`NotifyClipBoundaryHoldReleased` で保持着地・同期の保留などを片付ける）。番号で黙って無効にすると、この解除が起きない。
S-4 のテストの期待値が `{true, false}` から `{true, true}` に変わったのは、その証拠。
段 3b で GPU 復旧が `BeginFileLoad` を通るようになったので、Single でホールド中に GPU 復旧が起きると、位置つきの読み込みが一時停止を引き継ぎ、
LTC がクリップに戻っても解除が起きず止まったままになる。

## やること

1. 読み込み番号の部分を元に戻す: `BoundaryHoldState` の `_heldEpoch`・`IsHeldAt`・`MarkHeld(epoch)` を取り除き、`SingleModeSyncCoordinator.IsBoundaryHeld` と
   `LatchSnapshot` の `clipBoundaryHeld` を段 3c 前の判定（`_boundary.IsHeld`）に戻す
2. `SingleModeSyncCoordinatorTests` の S-4 のテストの期待値を元（`holdCalls.Should().StartWith(new[] { true, false }, "前のファイルのホールドは解除する")`）に戻す
3. 段 0 の表の `ClipBoundaryHeld` × `FileLoad` の行を「現状 Keeps・意図 Clear」に戻し、根拠に
   「v0.5.3 では直さない: 読み込みの後の最初の評価で解除（再開と片付け）が起きる。番号で無効にすると解除が起きず、GPU 復旧の後に止まったままになる（段 3c の手直し）」と書く
4. モード切替・同期の無効化・停止でラッチを消す部分（`OnLifecycle` と `LtcSyncController` からの呼び出し、ログ、`SyncDisabled_WhileBoundaryHeld_ClearsLatchAndKeepsPause`）は**そのまま残す**
5. 追加のテスト: Single でホールド中に位置つきの読み込み（GPU 復旧の `ReloadCurrentTrackAfterGpuRecovery` 相当）をした後、LTC がクリップの中に戻ったら
   解除されて再生が再開する（止まったままにならない）ことを確かめる。組み立てが難しければ、`SingleModeSyncCoordinator` の単体で
   「`BeginFileLoad` の後の評価で `SetEndHold(false)` が呼ばれる」を確かめる形でよい
6. 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の表は全緑）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、戻したもの・足したテスト・テストの件数をこの画面に書く
