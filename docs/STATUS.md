# 現在地（2026-09-25）

- **公開**: v0.5.1 (beta)、main = タグ `v0.5.1` = `767e326`
- **作業中**: v0.5.2（ブランチ `v0.5.2`、作業ツリー `timecode-sync-player-v05`）。同期の状態を見える形にする作り直し
  - 段 0 完了: ラッチの寿命の特性テスト 425 行（`tests/TimecodeSyncPlayer.Tests/LatchLifetime/`）、全件緑、表は承認済み
  - **次の 3 手**:
    1. 段 1: できごとを `SyncLifecycleEvent` に集め、`Sync lifecycle:` をログに出す（振る舞いは変えない）
    2. 段 2: 軸ごとの状態の型と、軸をまたぐ規則の純関数（振る舞いは変えない）＋ §6-11・§6-12
    3. 候補を作り、開発機で重い素材セット → 検証機で固定の一式を 2 回
- **その次**: v0.5.3 = 段 3（寿命の食い違いを赤いテストから 1 件ずつ直す）

## 規則と入口

- 設計: [design/v0.5.2-sync-state.md](design/v0.5.2-sync-state.md)（§5 版の分け方、§7 決定事項、§8 レビュー）
- 計画: [release-0.5-plan.md](release-0.5-plan.md)
- 引き継ぎ: [PARENT-HANDOVER-2026-09-12.md](PARENT-HANDOVER-2026-09-12.md) の先頭
- 公開の手順: [RELEASE-PROCEDURE-0.4.md](RELEASE-PROCEDURE-0.4.md)（候補は版を据え置き、合格後に版を上げる）
- 合否の一式（固定）: 標準シナリオ 3 通り（MediaIn 5）＋ L-1 6 本 ＋ A 切替、すべて RTX
