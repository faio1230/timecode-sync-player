# 現在地（2026-09-26）

- **公開**: v0.5.3 (beta, Pre-release) 2026-09-26 20:38、main = タグ `v0.5.3` = `78ac486`（2026-09-26 23:50 の履歴の書き換えの後の SHA）。zip `734E45D0…728C` / setup.exe `E2088C06…93B4`。Latest（安定版）は v0.5.4 から
  （https://github.com/faio1230/timecode-sync-player/releases/tag/v0.5.3）
- **作業中**: v0.5.4（ブランチ `v0.5.4`、作業ツリー `timecode-sync-player-v05`）。担当 w5:p3 の作業ブランチは v0.5.4 から切る（`agent-a-v054`、作業ツリー `timecode-sync-player-v053` を流用、OpenCode はそのフォルダで起動）
- **履歴の書き換え（2026-09-26、利用者の指示）**: 公開文書に入っていたローカルのパス（利用者名）と検証機のホスト名を全履歴から除いた（filter-repo、5 ブランチと 9 タグを強制 push）。以前の記録にある SHA（`00cec9c` など）は書き換え前のもの。公開文書にはパスを書かず、作業ツリーは名前で書く（手元の pre-commit フックが検査する）
- **ゴール**: [GOAL-v0.6.md](GOAL-v0.6.md)（v0.5.2 → v0.5.3 → v0.5.4（安定版）→ v0.6.0。版の位置づけは 0 節、止まる地点は 3 節）
- **次の 3 手**:
  1. **v0.5.3 を公開**（2026-09-26 20:38、Pre-release。https://github.com/faio1230/timecode-sync-player/releases/tag/v0.5.3）。main = タグ `v0.5.3` = `78ac486`（2026-09-26 23:50 の履歴の書き換えの後の SHA）。
     zip `734E45D0…728C` / setup.exe `E2088C06…93B4`（GitHub の digest と一致）
  2. **v0.5.4 = 安定版（Latest）。既知のエラーを潰す＋シーク経路の門の統合**（ブランチ `v0.5.4`、作業ツリー `timecode-sync-player-v05`、GOAL 0 節）。段 0 を進行中:
     - 潰す一覧 K1〜K8（[release-0.5-plan.md](release-0.5-plan.md) の v0.5.4 節）: 直す K1・K2（D39）、K3（A1）、K5（§6 の 1 の読み込み・6・7・15）、K7（U-1）、K8（門の統合）。K4（C-1 の 4K 終端）は検証で確認して閉じる。既知の制限は K6（長 GOP、推奨の範囲外）だけ
       利用者の決定（2026-09-26）: §6 の 15 は利用者が止めている間は再開しない。U-1 は v0.5.4 で直す
     - 済み: 段 0 の門の基準（`4cd3cd6`、[v0.5.4-gate-baseline.md](design/v0.5.4-gate-baseline.md)）、シナリオ層 C1〜C3（仮想時計・偽の再生 API・LTC の台本）、D39 の修正 K1・K2（`d81c8a0`、長さの更新だけから守る＝利用者の決定）
     - 門の統合の設計書 [v0.5.4-gate-unification.md](design/v0.5.4-gate-unification.md)（24 門 → 15 門の案）。利用者の決定: §8 の即時停止を入れる、デバウンスは測定で決める
     - 進行中: p6 がシナリオ層 C4（門の観測）、p3 が U-1 の原因の測定（K7、指示書 prompts/2026-09-26-v054-u1-measure.md）
     - 残り: K3（A1）、K5（§6 の 1 の読み込み・6・7・15）、K7 の修正、門の統合の実装、K4・K6 の確認 → 候補
  3. v0.6.0 = ProRes の GPU 復号（[release-0.6-plan.md](release-0.6-plan.md)。決定済み: NVIDIA だけ既定で有効、設定キー `proResGpu` と UI の 3 択）
  - 並行して追う既知の間欠: A1 型の最終フレームの取り込みの時間切れ（release-0.5-plan.md の「既知の間欠」）、U-1（高頻度の UI 監査で配信が数秒止まる、v0.5.1 から）
- **利用者の決定（2026-09-26）**: main の作業ツリーの未コミットの文書（README・ARCHITECTURE・ROADMAP・UI-UX-DESIGN・図・画像）は触らずそのまま。
  LTC スクリプト 2 つの -GapSeconds は v0.5.2 側に同じものがあるので捨てた（差分は親の scratchpad に保存）

## 規則と入口

- 設計: [design/v0.5.2-sync-state.md](design/v0.5.2-sync-state.md)（§6 寿命の食い違いの候補、§7 決定事項）、[design/v0.5.3-load-paths.md](design/v0.5.3-load-paths.md)
- 計画: [release-0.5-plan.md](release-0.5-plan.md)、[release-0.6-plan.md](release-0.6-plan.md)、ゴール [GOAL-v0.6.md](GOAL-v0.6.md)
- 引き継ぎ: [PARENT-HANDOVER-2026-09-12.md](PARENT-HANDOVER-2026-09-12.md) の先頭
- 公開の手順: [RELEASE-PROCEDURE-0.4.md](RELEASE-PROCEDURE-0.4.md)（候補は版を据え置き、合格後に版を上げる）
- 合否の一式（固定）: 標準シナリオ 3 通り（MediaIn 5）＋ L-1 6 本 ＋ A 切替、すべて RTX
- 遅れの分類: 着地や追従の遅れが、復号速度から説明できる量（GOP 長 × フレーム時間 + 4K のフレーム時間、実測 100〜300ms）を明らかに超えたら、素材・解像度・デコーダではなく同期経路の時間ベースの門（保留の時間切れ、位置信頼の再確認、シークのゲート、確認窓）の足し算を疑い、製品側の欠陥候補として起票する。失敗の分類で「4K だから」「重い素材だから」と書く前に、ログのシーク発行時刻と LTC 到着時刻の差を復号上限と比べる（利用者の規則、2026-09-26、TSP-Fable 経由）
