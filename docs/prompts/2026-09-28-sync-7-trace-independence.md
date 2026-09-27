# 指示書: #7（relocate の判定の誤差を配信 PTS に）と、試験の道具で製品の判断が変わる箇所の洗い出し

作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v054`（親が v0.5.4 を取り込んだ後の HEAD から）。

## 背景（読むもの）

1. `docs/release-0.5-plan.md` の末尾「試験と本番の差」: relocate の粗い判定の位置が、出力トレースの有無で変わっていた
   （試験 = 評価位置、本番 = 照会位置）。これが #7 の中身で、TSP-Fable の判定（利用者承認の代理）で v0.5.4 で直す
2. `docs/design/v0.5.4-k3-a1.md` §13 の #7 の行と、末尾の判定の行
3. `docs/design/v0.5.4-gate-unification.md` §10-0（規則 0 と「試験の道具は製品の判断を変えない」）、§10-1 の規則 2
4. `CLAUDE.md`（ビルド・テストのコマンド、既知のクセ）

## やること（この順、区切りごとにコミット）

1. **赤いテストを先に**: 出力トレースが無効のとき（`OutputTrace.Current.IsEnabled == false`）でも、relocate の粗い判定の誤差が
   評価位置（配信 PTS 基準）で計られることを、Continue と Single の両方で固定する。照会位置と配信 PTS がずれた場面
   （例: 照会 20.033、配信 19.983、目標 19.983 → relocate しない）を使う。今のコードで赤になることを確かめてからコミット
2. **#7 の修正**: `ContinueOnTrackCoordinator`・`SingleModeSyncCoordinator` で、位置のサンプルをトレースと関係なく常に
   `EvaluateDecision` に渡す（`read.Sample` は同じ照会で取れていて、追加の照会は無い）。
   配信が無い間（評価の基準が Pipeline か None）は、B4b と同じく**評価しない**（relocate も varispeed も判定しない）。
   `TCS_SYNC_POSITION_FEEDBACK` のフラグと着地待ちの枝（`TimecodeSyncService.EvaluateDecision` の `IsWaitingForLanding` の条件）は触らない。
   shadow の記録（`RecordShadow`、trace の行）は、今どおりトレースが有効なときだけでよい（記録だけに効く分岐）
3. **洗い出し**: 出力トレース（`OutputTrace.Current.IsEnabled`、`traceEnabled`）・計測の環境変数（`TCS_*_LOG`、`TCS_SEEK_DIAG`、`TIMECODE_SYNC_PLAYER_OUTPUT_TRACE` など）・
   試験用の口の有無で、**製品の判断や状態**が変わる箇所を grep で全部挙げる。`docs/design/v0.5.4-gate-unification.md` §10-0 の下に表で残す
   （ファイル:行、分岐の条件、判断に効くか記録だけか、案）。判断に効くものは #7 と同じく「常に同じ経路」に直す（1 件 1 コミット、赤いテストから）。
   記録だけに効くものは残す。直すと挙動が大きく変わるものは直さずに表に書いて止める
4. **赤くなった既存テスト**は 3 区分（(a) 仕組み → 書き換え／(b) 再発 → 新しい状態で緑／(c) 再発 → 赤のまま調査）で数える。
   (b) の期待を変えるときは、何が起きなくなったかを先に調べて報告に書く

## 規則

- 定数・仕組みを新しく足さない。shim は触らない
- してよい: このツリーでの編集・ビルド・非E2E（`dotnet test ... --filter "Category!=E2E"`）・日本語のコミット
- してはいけない: push、main と v0.5.4 への書き込み、実機の試験（LTC シナリオ・E2E）、git stash/reset/clean、素材名・ローカルの絶対パスを書くこと
- 完了の条件: ビルド成功、非E2E の失敗 0（(c) が残るなら一覧つきで止める）、赤いテストが緑に変わったこと、洗い出しの表
- 報告: `docs/reports/2026-09-28-agent-b-v054-sync-7.md` に事実だけ（変更の要約、テストの件数、洗い出しの表、区分表、未解決の疑問）。合否は書かない。報告もコミットする
