# 機能プラン: LTC 信号断時の再生動作モード（Run-through / Stop）

作成: 2026-07-15（グリルセッションで仕様確定済み）。着手順: H5（劣化信号E2E）完了後。

## 背景

現状、Sync ON 中に LTC 信号が途絶えると、シーク指示が来なくなるだけで mpv の再生は
そのまま続く（実質ランスルー固定）。ライブ運用では「信号が死んだら映像も止めたい」
現場と「回し続けたい」現場の両方があるため、モードとして切替可能にする。

## 確定仕様 v1

| 項目 | 仕様 |
|---|---|
| モード | **ランスルー（既定・現行互換）/ 停止** の2値 |
| 検出 | 最後の有効 LTC フレーム受信から **250ms**（既定）無受信で信号断と判定。AppSettings `LtcSignalLossTimeoutMs`（int、既定250、100〜5000にクランプ） |
| 発動条件 | **Sync ON かつ LTC 監視中**のみ。Single / Continue 両モード共通。GapFreezeHandler がアクティブ（ギャップ演出中）のときは介入しない。LTC STOP ボタン押下（手動停止）では発動しない（監視終了であり信号断ではない） |
| 停止動作 | **pause（最終フレーム表示のまま静止）**。黒画面にはしない（異常系で黒を出すと事故に見える） |
| トリガー方式 | **エッジトリガー**: 「受信中→信号断」への遷移時に1回だけ pause する。停止中にオペレーターが手動で Play した場合は手動が勝ち、ポリシーは再介入しない（次の「復帰→断」サイクルまで） |
| 復帰 | **自動復帰**。連続 **5フレーム**の正常受信（AppSettings `LtcSignalResumeFrames`、既定5）で信号復帰と判定 → 再生再開 + その時点の LTC 位置へ同期シーク（既存同期エンジンに委ねる）。短いしきい値による停止↔再生のバタつきはこのヒステリシスで抑止する |
| UI | Sync セクションの `GapBehaviorCombo` の隣に ComboBox「信号断時」（選択肢: ランスルー / 停止）。AutomationId 付与（E2E 用）。**Sync OFF でも設定操作は常に可能**（発動しないだけ） |
| 既定値 | ランスルー（既存ユーザーの挙動を変えない） |
| 永続化 | **AppSettings のみ**（GapBehavior と同じ流儀）。プロジェクトファイルには保存しない |

## 実装方針

- 検出はフレーム受信時刻の監視 + 既存の 100ms UI タイマー（`OnTick`）またはそれに準ずる
  定期処理で判定（フレーム駆動だけでは「来ないこと」を検出できない）
- 判定ロジックは純C#クラス **`LtcSignalLossPolicy`**（仮）に切り出す:
  状態（Receiving / Lost）、しきい値、ヒステリシスカウンタ、エッジ検出を持ち、
  「今何をすべきか（None / Pause / ResumeAndSync）」を返す。ユニットテストで時刻を注入して検証
- pause / 再開の実行は既存 `PlaybackOperationsCoordinator.ApplyPauseState` 系と
  mpv pause プロパティ書き込みを再利用。新しい mpv 呼び出しパターンを作らない
- SyncViewModel にモードプロパティ（ComboBox バインド・AppSettings 連動）。
  `GapBehaviorIndex` の実装パターンを踏襲
- AppSettings に3項目追加（モード・タイムアウトms・復帰フレーム数）。
  後方互換: 既存 settings.json に無いキーは既定値

## テスト

- ユニット: `LtcSignalLossPolicy` の全遷移（受信中→断のエッジで1回だけ Pause /
  断中の手動 Play 後に再介入しない / 連続Nフレームで ResumeAndSync / N未満で復帰しない /
  Sync OFF・監視外・GapFreezeアクティブ時は常に None）
- ユニット: SyncViewModel のモードプロパティと AppSettings 永続化
- 実機 E2E（VB-CABLE、既存 LtcHardwareLoopE2ETests の基盤流用）:
  1. Stop モード + 信号断 → 再生が pause される（時間表示が止まる）
  2. Stop モード + 信号復帰 → 再生再開し LTC 位置へ追従
  3. ランスルーモード + 信号断 → 再生が継続する（時間表示が進み続ける）
- 機能追加なので「挙動保存」ではなく本仕様への準拠が正。ただし**既定値（ランスルー）では
  既存の全テストが無変更で通ること**（現行互換の検証）

## 制約

- docs/test-coverage-roadmap.md の共通前提（コミット日本語・警告0・固定シード等）と
  一括実行ルール（検証ゲート・停止条件・progress 記録）に従う
- E2E ゲートは全件（実機 LTC 含む・Skip なし）
- ブランチ: feature/ltc-signal-loss-mode。push しない
