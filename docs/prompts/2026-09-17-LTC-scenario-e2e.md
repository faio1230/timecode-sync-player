# LTC 同期シナリオの E2E（利用者の検証項目 26 件）

作成: 2026-09-17 09:30、親。担当: 除去担当（`w5:p6`、作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
基点: **main の最新（`9087633` 以降）** を `agent-b` へ通常マージし、shim を自分のツリーでビルドしてから。
仕様: `docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md`（行列、素材、判定、新テスト 19 件の一覧）。**この文書のとおりに作る。判定基準を勝手に緩めない。**
製品コードには触らない（挙動が期待と違えば、観測値をジャーナルに残してテストを失敗させ、報告する。仕様か欠陥かは親が判断する）。

## 1. 順序（各段でビルド・非E2E・当該テストを通し、コミットを分ける）

1. **素材**: `scripts/make-e2e-media.ps1` に行列 2 節の A/B/C（色 + 冒頭/末尾の識別色 + 秒番号）を追加。既存素材の生成は変えない
2. **プロジェクト**: `tests/TimecodeSyncPlayer.Tests/Fixtures/ltc-scenario.tsp`（相対パス、`ProjectSerializer` の形式。`v4-substitute.tsp` を参考）。先頭オフセット 5 秒、A/B/C とギャップ 5 秒
3. **ヘルパー**: 画面の読み戻しから中央 60% の平均色と黒画素率を返す関数（`RealProjectGapE2ETests.CaptureImage` の経路を共用化して `Helpers/` へ）。色の同定は行列 2 節の閾値。単体テストを 1 本（合成画像で閾値を確認）
4. **テスト**: `tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs`。行列 3 節の ID をテスト名に含める（例: `S2_Single_RepeatedLtcJumps_LandWithinTolerance`）。VB-CABLE が無ければ Skip。`[Collection("E2E")]`、`Category=E2E`。ストレスの周回数は環境変数 `TIMECODE_LTC_SCENARIO_CYCLES` で上書き可（既定は行列の値）
5. 各テストは `MonkeyJournal`（既存）で観測値（LTC、位置、平均色、黒率、時刻）を残し、失敗時にどの観測で落ちたかが分かるようにする

- 同期担当が並行して `scripts/run-ltc-scenarios.ps1` と D18（`E2EAppRunner.ResolvePrereqs`）を作る。**`E2EAppRunner.cs` と `scripts/run-ltc-scenarios.ps1` には触らない**（衝突するため）

## 2. 守ること

- 実機（E2E の実行）は一報のうえ親の合図。同期担当と重ねない
- ローカルの絶対パスを書かない。main へ書かない。stash / reset / clean を使わない。コミットは日本語
- 素材は作業ツリー内に生成（gitignore 対象）。プロジェクトは相対パス

## 3. 報告

コミット、新テスト 19 件の結果（合格 / 失敗 / スキップと、失敗のジャーナル抜粋）、非E2E の件数、証跡パス。**合否は書かない。** 期待と違う挙動は「観測」として書く（例: 「S-4 の C 選択で画面は橙 = C の終端だった」）。
