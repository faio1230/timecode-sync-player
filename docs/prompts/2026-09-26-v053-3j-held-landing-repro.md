# v0.5.3 段 3j: §6 の 4 の再現テスト（w5:p3 向け、製品コードは変えない）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`（`78dd1ee` 以降）。
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 4 と §7（利用者決定: 「4 は再現テストを書いてから決める」）。

## §6 の 4（推測の症状）

保持（Duplicate）が理由の損失中に、値が動いた Jump 1 枚で即時に復帰する（D27-b）。このとき `_heldLossLandingSeconds` と
`_lastHeldEffectiveSeconds`（`LtcInputState` の `HeldLossLandingSeconds` / `LastHeldEffectiveSeconds`）が下りない。
**Jump の直後に再び無音で損失すると、古い保持値へ着地する**のではないか（段 0 の表: `HeldLossLanding` と `LastHeldEffective` × `JumpRecovery` は「現状 Keeps・意図 Clear」）。

## やること（製品コードは変えない）

1. 停止モード（信号断で一時停止する）で、次の順に起こす再現テストを書く（`SyncScenarioHarness` を使う。既存の `GapSignalLossInteractionTests` や `LtcHeldValueChangeTests` の組み立てを参考に）:
   保持値 A で損失（一時停止、A へ着地）→ 値が動いた Jump（B）で即時復帰 → B から数フレーム進む（Normal を**入れない**場合と**入れる**場合の 2 通り）→ 無音で再び損失 → どこへ着地するか
2. 「古い保持値 A へ着地する（シークが A の位置へ出る）」が起きるかを確かめる。起きる場合はテストを `[Fact(Skip = "v0.5.3 段 3j: §6 の 4 の再現（直すかは親が決める）")]` で残し、Skip を外すと赤になることを報告する。
   起きない場合は、なぜ起きないか（どのコードが守っているか）を報告し、テストは「起きないこと」を確かめる緑のテストとして残す
3. `docs/design/v0.5.3-held-landing-repro.md`（新規）に、再現の手順・結果・（起きる場合）利用者から見た症状を 10 行以内で書く

## 規則

- **製品コードは変えない。git の書き込み禁止**。シェルのコマンドはこの作業ツリーで実行する
- 非 E2E を回して全件合格（Skip の新テストを除く）を確かめる
- 終わったら、再現したか・その理由・テストの件数をこの画面に書く
