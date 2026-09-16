# V4: ギャップ 3 種を、親が構成した代替プロジェクトで実施する

作成: 2026-09-17 03:20、親。担当: 同期担当（`w5:p3`）。**V5 の後、V6（60 分）の前に実施**（約 3 分）。
決定: 現場のプロジェクトファイルが未提供のため、代替プロジェクトで先に判定し、実素材が来たら追試する（`docs/release-0.4-plan.md` 4 節 5、親の仮決定）。

## 1. 使うもの

- `tests/TimecodeSyncPlayer.Tests/E2E/RealProjectGapE2ETests.cs`（opt-in: `TIMECODE_REAL_PROJECT_PATH`）。期待値はプロジェクトの内容から導くので、実素材でなくても動く。
  ギャップ境界での切替、Black ⇄ Freeze（LTC 保持中）、Continue ⇄ Single、Sync ON/OFF の再評価、信号復帰、trimmed Freeze（テスト内で生成）を確認する
- 素材: 自分の作業ツリーの `artifacts/media`（`make-e2e-media.ps1` で生成済み）。**新しく作るなら作業ツリー内に**

## 2. 代替プロジェクトの構成（`.tsp` は `ProjectSerializer` の形式。テスト内の `visible-freeze.tsp` の作り方を参考に、小さなスクリプトか一時テストで生成してよい）

| トラック | タイムライン開始 | 素材 | 備考 |
| --- | --- | --- | --- |
| A | 0:00:10 | `test_1080p60.mp4`（30 秒） | MediaIn 0、SyncOffset 0 |
| （ギャップ 1） | 0:00:40〜0:00:50 | — | Black のギャップ 10 秒 |
| B | 0:00:50 | `test_720p25.mkv`（尺は素材どおり） | **MediaIn 2 秒、SyncOffset +0.5 秒**（非ゼロの経路を通す） |
| （ギャップ 2） | B の終端〜+8 秒 | — | |
| C | ギャップ 2 の後 | `test_1080p60.mp4` | **IsEnabled=false のトラック D を C の前に 1 本入れる**（無効トラックが期待値から除かれること） |

- 同期モード Continue、ギャップ動作 Black（テストが Freeze へ切り替える）
- 解像度が違う切替（1080p60 → 720p25）を含める（D8 の経路）

## 3. 実行と報告

- `TIMECODE_REAL_PROJECT_PATH=<作業ツリー内の .tsp>` で `RealProjectGapE2ETests` を 1 回（実機。**一報のうえ合図待ち**）
- 報告: 合格/失敗の件数ではなく、テストのジャーナル（`boundary-start` / 各 Wait の結果）とスクリーンショットの黒判定、失敗があればその Wait の名前と実測値。
  代替プロジェクトの .tsp は `tests/TimecodeSyncPlayer.Tests/Fixtures/` 等に **相対パスの素材参照で**コミットしてよい（ローカルの絶対パスを含めない）
- 合否は書かない
