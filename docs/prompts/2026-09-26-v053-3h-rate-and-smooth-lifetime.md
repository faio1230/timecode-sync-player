# v0.5.3 段 3h: 倍率と Smooth の状態を、補正モードの変更・手動シーク・監視の停止で戻す（§6 の 8・13・14）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`7fc741d` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 8・13・14 と §7（利用者決定: 8・9・13・14 は「設定や操作の後に状態が残る」型なので同じ流れで直す）、§3 段 3。

**3 件を 1 件ずつ進め、1 件ごとにこの画面へ報告して手番を終える**（親が 1 件 1 コミットで取り込む）。次の件は親の合図で始める。

## 件 A（§6 の 8）: 補正モードの変更で倍率を 1.0 に戻す

- 表の行: `RateNotUnity` × `CorrectionModeChanged`（今は「現状 Keeps・意図 Clear」）
- 直し方: `LtcSyncController.OnLifecycle(CorrectionModeChanged)` で `ResetCorrection()` を呼ぶ（戻せないときは今までどおり「戻し待ち」にする）
- 連動して変わる行（`RateRestorePending`・`SmoothUnavailable` × `CorrectionModeChanged` など）があれば直し、全部書く

## 件 B（§6 の 13）: 手動シークで Smooth 不可を戻す

- 表の行: `SmoothUnavailable` × `ManualSeek`（今は「現状 Keeps・意図 Undecided」）。**意図を `Clear` に直し**、根拠に「利用者決定 2026-09-25（§7）: v0.5.3 で直す」と書く
- 直し方: `OnLifecycle(ManualSeek / TimelineSeek)` で `_rate.ResetSmoothAvailability()` を呼ぶ（コメント「T7: 手動シークは補正状態（Smooth の無効化を含む）も捨てる」のとおりにする）

## 件 C（§6 の 14）: 監視の停止で倍率を 1.0 に戻す

- 表の行: `RateNotUnity` × `MonitoringStopped`（今は「現状 Keeps・意図 Undecided」）。**意図を `Clear` に直し**、根拠に利用者決定を書く
- 直し方: `OnLifecycle(MonitoringStopped / MonitorDeviceStopped)` で `ResetCorrection()` を呼ぶ

## 各件の手順と判定

1. 先に表を書き換えて赤を確かめる → 直す → 緑
2. **既存のテストの期待値は変えない。** 変えないと通らないものが出たら、変えずに名前と理由をこの画面に書いて止まる
3. 判定: ビルドの警告 0、非 E2E 全件合格（trx に書き出す）。E2E（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）は件 C の後に 1 回だけでよい

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 報告: 件の名前、表で書き換えた行（連動した行も）、赤→緑、変えたファイル、テストの件数
