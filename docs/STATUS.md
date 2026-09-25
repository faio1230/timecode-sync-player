# 現在地（2026-09-26）

- **公開**: v0.5.2 (beta) 2026-09-26 05:55、main = タグ `v0.5.2` = `b9dc877`。zip `0B27D871…15DE` / setup.exe `67B63300…9D98`
  （https://github.com/faio1230/timecode-sync-player/releases/tag/v0.5.2）
- **作業中**: v0.5.3（ブランチ `v0.5.3`、作業ツリー `timecode-sync-player-v05`）。担当 w5:p3 の作業は `agent-a-v053-3a`（作業ツリー `timecode-sync-player-v053`、OpenCode はそのフォルダで起動）で進め、区切りごとに v0.5.3 へ取り込む
- **ゴール**: [GOAL-v0.6.md](GOAL-v0.6.md)（v0.5.2 → v0.5.3 → v0.6.0。止まる地点は 3 節）
- **次の 3 手**:
  1. v0.5.3 の残り: §6 の 2 のギャップの 2 経路（利用者決定 2026-09-26: 別の口を作って直す）→ 8、9、13、14 → 候補 →
     開発機で段 1 と同じ交互の突き合わせ・重い素材 3 本立て（F-4 の時間切れを数える。15 本中 3 本以上なら回帰候補）→ 検証機で固定の一式 2 回
     - 済み（v0.5.3 に取り込み済み `52798cb`）: 3a 経路の棚卸し、3b GPU 復旧・自動送りを BeginFileLoad に通す、3c 境界ホールドのラッチ（§6 の 1）、
       3d ロード中の印の取り消し（§6 の 3）、3e 1 回適用のラッチ（§6 の 5）、3f 直前の着地の記録（§6 の 10）、ランナーの -TrackSegmentSeconds
     - 進行中: D38（保持後のジャンプの着地が約 2 秒遅れる、原因 `947796d`）。棚卸し [v0.5.3-d38-seek-gates.md](design/v0.5.3-d38-seek-gates.md)。
       直す範囲は §6（(a) 保持中も着地を判定、(b) 未信頼でも置き換え、門 3 はログのみ）。p3 が実装中、指示書 prompts/2026-09-26-v053-d38-fix.md
  2. v0.6.0 = ProRes の GPU 復号（[release-0.6-plan.md](release-0.6-plan.md)。決定済み: NVIDIA だけ既定で有効、設定キー `proResGpu` と UI の 3 択）
  3. 既知の間欠の追跡: A1 型の最終フレームの取り込みの時間切れ（release-0.5-plan.md の「既知の間欠」）、U-1（高頻度の UI 監査で配信が数秒止まる、v0.5.1 から）
- **利用者の決定（2026-09-26）**: main の作業ツリーの未コミットの文書（README・ARCHITECTURE・ROADMAP・UI-UX-DESIGN・図・画像）は触らずそのまま。
  LTC スクリプト 2 つの -GapSeconds は v0.5.2 側に同じものがあるので捨てた（差分は親の scratchpad に保存）

## 規則と入口

- 設計: [design/v0.5.2-sync-state.md](design/v0.5.2-sync-state.md)（§6 寿命の食い違いの候補、§7 決定事項）、[design/v0.5.3-load-paths.md](design/v0.5.3-load-paths.md)
- 計画: [release-0.5-plan.md](release-0.5-plan.md)、[release-0.6-plan.md](release-0.6-plan.md)、ゴール [GOAL-v0.6.md](GOAL-v0.6.md)
- 引き継ぎ: [PARENT-HANDOVER-2026-09-12.md](PARENT-HANDOVER-2026-09-12.md) の先頭
- 公開の手順: [RELEASE-PROCEDURE-0.4.md](RELEASE-PROCEDURE-0.4.md)（候補は版を据え置き、合格後に版を上げる）
- 合否の一式（固定）: 標準シナリオ 3 通り（MediaIn 5）＋ L-1 6 本 ＋ A 切替、すべて RTX
- 遅れの分類: 着地や追従の遅れが、復号速度から説明できる量（GOP 長 × フレーム時間 + 4K のフレーム時間、実測 100〜300ms）を明らかに超えたら、素材・解像度・デコーダではなく同期経路の時間ベースの門（保留の時間切れ、位置信頼の再確認、シークのゲート、確認窓）の足し算を疑い、製品側の欠陥候補として起票する。失敗の分類で「4K だから」「重い素材だから」と書く前に、ログのシーク発行時刻と LTC 到着時刻の差を復号上限と比べる（利用者の規則、2026-09-26、TSP-Fable 経由）
