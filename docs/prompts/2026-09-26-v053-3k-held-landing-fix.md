# v0.5.3 段 3k: Jump による即時復帰で、保持着地の値と保持値を下ろす（§6 の 4）（w5:p3 向け、振る舞いを変える）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`3a670be` 以降）。
背景: 段 3j で再現した（`docs/design/v0.5.3-held-landing-repro.md`、`HeldLandingAfterJumpReproTests`）。
保持損失中に値の動いた Jump で即時復帰した後、Normal を挟まずに無音で再び損失すると、Jump 前の古い保持値へ着地する。

## 直す行（段 0 の表）

`HeldLossLanding` と `LastHeldEffective` × `JumpRecovery` の 2 行（今は「現状 Keeps・意図 Clear」）

## やること

1. 先に 2 行を `Clears` に書き換えて赤を確かめる
2. 直す: 損失中の Jump による即時復帰（`LtcSyncController` の受信経路で `_signalLoss.ObserveJumpFrame` の後に損失が解けたとき。今は `_input.ClearJumpApplied()` だけ）で、
   `_input.ClearHeldLossLanding()` と `_input.ClearHeldEffective()` も呼ぶ。確認済み Jump の経路（`ApplyConfirmedJump`）で同じ復帰をするところも同様に揃える（そこはすでに保持着地の値を下ろしている。保持値の扱いを確かめて、必要なら揃える）
3. `HeldLandingAfterJumpReproTests` の Skip を外し、緑になることを確かめる
4. **既存のテストの期待値は変えない。** 変えないと通らないものが出たら、変えずに名前と理由をこの画面に書いて止まる
5. 判定: ビルドの警告 0、非 E2E 全件合格（trx に書き出す）、E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- シェルのコマンドはこの作業ツリーで実行する。**git の書き込み禁止**
- 終わったら、表で書き換えた行（連動した行も）・赤→緑・変えたファイル・テストの件数をこの画面に書く
