# テスト強化プラン: リスク優先のテストマトリクスと不足範囲

作成: 2026-07-16（main = 9d88673、テスト948 + E2E 37）。
方針: 総当たりではなく、**データ破損 > 主要機能停止 > 状態遷移 > 境界値 > 低頻度高影響** の順で
不足を潰す。カバレッジ率は完了判定に使わない。

## 共通ルール（全タスク拘束）

- 既存基盤を使う: xUnit + FluentAssertions、実クラス優先（過剰モック禁止）、
  固定シード、Temp ディレクトリは後始末、`SkippableFact` の新規追加禁止
- **ミューテーション確認必須**: 各タスクで代表テスト最低2件について、対象実装を意図的に
  壊し（条件反転・ガード削除等）テストが失敗することを確認し、壊した内容と失敗出力を
  progress 報告に記載してから戻す。コミットに壊した状態を含めない
- テストを通すための本体変更は禁止。**テストが実装の実バグを暴いた場合は該当タスクを
  停止して報告**（勝手に直さない）。唯一の例外は V1 の承認済みアトミック書き込み修正
- 検証: `dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj -c Debug --filter "FullyQualifiedName!~E2ETests" -v minimal` 全グリーン・警告0。
  本体変更を含む V1 のみ E2E 全件も実行
- ブランチ: test/hardening-v1。push しない。progress 更新。コミットは日本語・タスク単位

## テストマトリクス（主要機能 × 観点）

凡例: ◎=十分 ○=部分的 ✗=不足 —=対象外/低優先

| 機能 \ 観点 | 正常系 | 異常系 | 境界値 | 状態遷移 | 空/大量/不正 | 途中失敗 | 二重実行 | 同時操作 | 外部依存失敗 | 復旧 | 回帰 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| プロジェクト保存/読込 | ◎ | ○(不正JSON一部) | ✗ | — | ✗(欠落キー/巨大) | **✗(部分書込)** | ✗(二重保存) | ✗(保存中変更) | ✗(ロック/権限) | ✗(破損後起動) | ○ |
| AppSettings | ◎ | ○ | ◎(クランプ) | — | ✗(型不一致/切詰め) | **✗(部分書込)** | ✗(並行Update) | ✗ | ○(E2E隔離) | ○(catch→既定 未固定) | ○ |
| LTCデコード | ◎ | ◎(ノイズ/無効) | ○ | ◎ | ○(長時間✗) | — | — | — | — | ◎(瞬断) | ◎ |
| PCM変換/SampleProcessor | ◎ | ✗(不正WaveFormat) | ✗(0ch/24bit等) | — | ○ | — | — | — | — | — | ○ |
| 同期判定(Engine/Service) | ◎ | ○ | ○(**TC日跨ぎ✗/逆走✗**) | ◎ | — | — | ◎(debounce) | ✗(シーク中LTC) | — | ○ | ◎ |
| プレイリスト/タイムライン | ◎ | ○ | ✗(負/巨大offset) | ◎ | ✗(0件/1000件/重複区間) | — | — | ✗ | — | — | ◎ |
| GapFreeze/信号断/Exit | ◎ | ◎ | ○(timeout境界) | ◎ | — | — | ◎ | ✗(同tick競合) | ◎(デバイス死) | ◎ | ◎ |
| レンダー/PixelBuffer | ◎ | ○ | **✗(0/負/巨大寸法)** | ◎ | ✗(intオーバーフロー) | ✗(mpv rc<0続落) | ◎(Dispose) | — | ○ | ○ | ◎ |
| mpvロード/リゾルバ | ◎ | ◎ | — | — | — | — | — | — | ◎(DLL欠落) | ◎ | ◎ |
| Spout | ◎ | ◎ | — | ◎ | — | ✗(送信中失敗→再init) | ◎ | — | ◎ | ✗ | ○ |
| フルスクリーン | ◎ | ◎(切断) | — | ◎ | ✗(0ディスプレイ) | — | ✗(二重オープン) | — | ◎ | ◎ | ◎ |

**実装確認済みの事実（分析時に検証）:**
- `ProjectSerializer.SaveAsync` / `AppSettingsManager.UpdateAsync` は `WriteAllTextAsync` 直書きで
  **アトミックでない**（書き込み中クラッシュで破損 → データ破損リスクの筆頭）
- `AppSettingsManager.LoadAsync` は破損 JSON を catch して既定値へ復旧する（テスト未固定）
- `PixelBufferManager.EnsureXxx` は `width * height * 4` を int で計算（巨大値でオーバーフロー可能性）

---

## V1: 永続化のデータ破損防止（最優先・承認済み本体修正を含む）

**対象:** `ProjectSerializer` / `AppSettingsManager` の書き込み経路と破損時読み込み
**保証内容:** 書き込みが途中で失敗しても既存ファイルは無傷で残る。破損ファイルを読んでも
クラッシュせず、プロジェクトは明確なエラー、設定は既定値で起動できる。
**承認済み本体修正:** 両者の保存を「一時ファイルへ書き込み → `File.Replace`（既存なし時は
`File.Move`）」のアトミック方式へ変更する（挙動仕様: 成功時の内容は従来と同一バイト列）。
**テストケース:**
1. 破損 JSON（切詰め/ゴミバイト/空ファイル/BOMのみ）のプロジェクト読込 → 例外が呼び出し元へ
   期待どおり伝播 or 失敗結果（現行挙動を先に確認しピン留め）。アプリ起動時の `--open` 経由でも
   クラッシュしないこと（WindowLoadedCoordinator 経由のエラーパス）
2. 破損 settings.json → 既定値で復旧（既存 catch のピン留め）+ 直後の UpdateAsync で正常再生成
3. アトミック性: 書き込み先を検査するフェイク/フックで一時ファイル経由を検証 +
   一時ファイル書き込みが例外を投げる状況（読み取り専用ディレクトリ等）で既存ファイルが
   変更されないこと
4. 二重保存: 同一パスへの `SaveAsync` 並行呼び出しで最終ファイルが必ずどちらか一方の
   完全な内容になる（混在しない）
5. 欠落キー/未知キー/型不一致（数値に文字列）を含むプロジェクト・設定の読込
6. 保存先ロック（別ハンドルで開いた状態）→ 明確な失敗、既存ファイル無傷
**対象ファイル:** `src/TimecodeSyncPlayer/ProjectSerializer.cs`、`src/TimecodeSyncPlayer/AppSettings.cs`、
`tests/.../ProjectSerializerTests.cs`（追記）、`tests/.../AppSettingsTests.cs`（追記）
**実行コマンド:** 共通ルールの非E2E + **E2E全件**（本体変更を含むため）
**完了条件:** 上記6分類が全て実挙動で検証され、ミューテーション確認（例: File.Replace を
直書きに戻す→アトミック性テストが赤）が記録されていること

## V2: 同期判定の時間軸境界（日跨ぎ・逆走・境界値）

**対象:** `SyncDecisionEngine` / `TimecodeSyncService` / `LtcFrameProcessor`
**保証内容:** ライブで起こり得る時間軸の異常（23:59:59→00:00:00 ラップ、TC 逆走、
duration ちょうど/0/極小、tolerance/debounce 境界）で誤シーク・無限シークループ・例外が起きない。
**テストケース:**
1. LTC 23:59:59:24 → 00:00:00:00 ラップ時の判定（診断 Jump 扱いか、シーク暴発しないか —
   まず現行挙動を確認しピン留め）
2. TC 逆走（DAW 巻き戻し再生）: 連続後退フレームで診断が追従し、tolerance 内外それぞれで
   期待どおりの Seek/None
3. `DurationSeconds` = 0 / 0.04(1フレーム) / ltcSeconds と完全一致 / ltc が duration+ε
4. tolerance ちょうど±1ms、debounce 満了ちょうどの境界
5. シークバードラッグ中（IsSeeking=true）に LTC 判定が完全に抑止されること（同時操作）
**対象ファイル:** `tests/.../SyncDecisionEngineTests.cs`、`tests/.../TimecodeSyncServiceTests.cs`、
`tests/.../LtcFrameProcessorTests.cs`（いずれも追記。新規クラス不可 — 実装は変更しない）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証 + ミューテーション確認（例: tolerance 比較の >= を > に変えて赤）

## V3: プレイリスト/タイムラインの構造的境界

**対象:** `PlaylistState` / `PlaylistTimelineOffsetEditor` / タイムラインクエリ / `PlaylistDurationBackfillService`
**保証内容:** 空・大量・重複・極端なオフセットでもクエリと編集が正しく、UI 層へ渡る結果が壊れない。
**テストケース:**
1. 0トラックでの全公開操作（Select/Move/クエリ/オフセット編集）が安全
2. 1000トラックでのタイムラインクエリ正当性とアロケーション爆発がないこと（時間アサートは禁止、
   結果の正当性のみ）
3. タイムライン上で区間が重複する2トラック（オフセット操作で作れる場合）のクエリ結果の
   決定性（現行仕様を確認しピン留め。作れない場合はその防御をテスト）
4. オフセット負値/`double.MaxValue` 近傍/NaN・Infinity 入力の拒否またはクランプ
5. duration 未取得(null)トラック混在時の境界（gap 判定・末尾判定）
**対象ファイル:** `tests/.../PlaylistStateTests.cs`、`tests/.../PlaylistTimelineTests.cs`、
`tests/.../TimelineQueryTests.cs`、`tests/.../PlaylistTimelineOffsetEditorTests.cs`（追記）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証 + ミューテーション確認

## V4: オーディオ入力の不正フォーマット・長時間

**対象:** `PcmSampleConverter` / `LtcAudioSampleProcessor`
**保証内容:** 想定外の WaveFormat やゼロ長・巨大バッファで例外を漏らさず、統計値が破綻しない。
**テストケース:**
1. 0チャンネル/3チャンネル/8チャンネル、8bit/24bit/64bit float、サンプルレート 8kHz/192kHz の
   変換挙動（対応外は明確な失敗、対応内は正しいモノラル化 — 現行仕様を確認しピン留め）
2. bytesRecorded がフォーマット境界と不整合（奇数バイト等）
3. 長時間相当: 累積カウンタ（フレーム数・サンプル数）が int 上限付近でも破綻しない
   （直接大きい値を注入できる設計ならそれで、できなければ対象外と報告）
4. 全サンプル ±Infinity / NaN を含むバッファでピーク/RMS が NaN 伝播しないこと
**対象ファイル:** `tests/.../PcmSampleConverterTests.cs`、`tests/.../PcmSampleConverterEdgeCaseTests.cs`、
`tests/.../LtcAudioSampleProcessorTests.cs`（追記）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証 + ミューテーション確認

## V5: レンダーバッファの寸法境界

**対象:** `PixelBufferManager` / `RenderFrameSizePolicy` / `RenderFrameParameterBuilder` / `FrameRenderer`
**保証内容:** 0・負・巨大な幅高さがネイティブ境界（ピン留めバッファ/mpv パラメータ）へ
渡る前に安全に扱われる。int オーバーフローで小さいバッファが確保される事故がない。
**テストケース:**
1. width/height = 0 / 負 / 1 / 32768 / `int.MaxValue` 平方根近傍（`w*h*4` が int を跨ぐ組合せ）
   での Ensure 系・Policy 系の挙動 — オーバーフローで正の小さい値になる組合せ
   （例: 65536×65536×4 → 0）を必ず含める。**実バグが出たら停止して報告**
2. サイズ変更の連続（拡大→縮小→拡大）でのバッファ参照とピン留めの整合
3. FrameRenderer の 0 寸法呼び出し（RenderBlack(0,0) 等）が安全
**対象ファイル:** `tests/.../PixelBufferManagerTests.cs`、`tests/.../RenderFrameSizePolicyTests.cs`、
`tests/.../RenderFrameParameterBuilderTests.cs`、`tests/.../FrameRendererTests.cs`（追記）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証（オーバーフロー組合せ含む）+ ミューテーション確認

## V6: 状態機械の相互作用と二重実行

**対象:** `GapFreezeHandler` × `LtcSignalLossPolicy` × `GapStateExitPolicy` × モード/Sync 切替の合成
**保証内容:** 3つの状態源（ギャップ演出・信号断・手動切替）が同一 tick/連続 tick で競合しても、
pause 所有権と描画判定が一貫する。
**テストケース:**
1. ギャップ演出アクティブ中に信号断 → 信号断ポリシーが介入しない（既存ガードの合成検証）→
   ギャップ解除直後の tick で信号断が正しく評価される
2. 信号断 pause 中にモードを Continue→Single（GapStateExitPolicy 発火）→ 信号断所有の pause が
   誤って解除・二重解除されない
3. 信号断復旧（ResumeAndSync）と Gap 進入が同 tick 相当で連続した場合の順序整合
4. `GapFreezeHandler.TimeoutSec` 境界（2.999s/3.001s）でのタイムアウト遷移
5. フルスクリーン: 開いた状態で再度開こうとする二重実行（コンボ変更→即トグル連打相当の
   ロジック層検証）
**対象ファイル:** `tests/.../GapFreezeHandlerTests.cs`、`tests/.../LtcSignalLossPolicyTests.cs`、
`tests/.../GapStateExitPolicyTests.cs`、`tests/.../DisplaySelectionPolicyTests.cs`（追記。
必要なら新規 `GapSignalLossInteractionTests.cs`）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証 + ミューテーション確認（例: GapFreezeアクティブガードを外して赤）

## V7: 外部依存失敗の続落と復旧

**対象:** `SpoutOutput`（送信中失敗→無効化→再初期化）/ `MpvSessionInitializer`・
`RenderContextCreateResult` の失敗続落 / `MediaDurationReader` の異常出力
**保証内容:** ネイティブ層の失敗が一度起きた後も、アプリの状態が矛盾せず再試行・継続できる。
**テストケース:**
1. Spout: SendFrame 中のネイティブ例外/false 応答後に IsAvailable/IsEnabled が正しく遷移し、
   TryInitialize 再実行で復旧する（フェイク native シームで）
2. mpv 初期化失敗の各段階（Create失敗/Initialize負値/RenderContext失敗）後の後始末
   （ハンドル解放・エラー種別）の網羅
3. MediaDurationReader: ffprobe 相当の出力が空/非数値/負値/多行のときの null 返却
   （プロセス起動はモックせず、パーサ部を直接。パーサが分離されていなければ対象外と報告）
**対象ファイル:** `tests/.../SpoutOutputTests.cs`、`tests/.../MpvSessionInitializerTests.cs`、
`tests/.../RenderContextCreateResultTests.cs`、`tests/.../MediaDurationReaderTests.cs`（追記）
**実行コマンド:** 共通ルールの非E2E
**完了条件:** 全ケース実挙動検証 + ミューテーション確認

---

## 実行順

V1（データ破損・本体修正含む）→ V5（オーバーフロー実バグ疑い）→ V2 → V6 → V3 → V4 → V7。
V1 と V5 は実バグを暴く可能性があるため先行し、発見時は該当タスクを止めて報告する
（V1 のアトミック化のみ修正が事前承認済み）。
