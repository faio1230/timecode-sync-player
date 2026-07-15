# テストカバレッジ拡充ロードマップ

作成: 2026-07-15（コミット d136386 時点）
計測: `dotnet test ... --collect:"XPlat Code Coverage"`（非E2E、行カバレッジ 53.2%）

## 経緯

- PixelBufferManager / LtcDecoder ラウンドトリップ / CI カバレッジ計測は導入済み（テスト 756件・全グリーン）
- MainWindow の同期パス（Single/Continue/Gap）は SingleModeSyncCoordinator / ContinueOnTrackCoordinator / GapEnterCoordinator に抽出済み・テスト済み
- 残る未カバー領域は「WPF描画・ネイティブ境界・MainWindow の残り配線」に集中している

## 候補タスク一覧（優先度順）

| # | 対象 | 実測(抽出前) | 種別 | 難易度 | Codex適性 |
|---|------|------|------|--------|-----------|
| T1 | PlayerViewModel / SyncViewModel / AppSettingsManager の隙間 | 100% / 96.9% / 93.2% | テスト追加のみ | 低 | ◎ |
| T2 | FrameRenderer | 166行・100% | テスト追加のみ | 中 | ○ |
| T3 | TimelineDrawingSurface | 抽出クラス100% / Surface 8.4% | ロジック抽出＋テスト | 高 | △（制約厳守前提） |
| T4 | SpoutOutput | 160行・100% | シーム導入＋テスト | 中 | ○ |
| T5 | TimelinePanel.xaml.cs | 抽出クラス100% / Panel 0% | ロジック抽出＋テスト | 中 | △ |
| T6 | LtcAudioMonitor | 抽出クラス100% / Monitor 0% | 部分抽出＋テスト | 中 | △ |
| T7 | MainWindow 残り配線（1700行） | 指定3抽出クラス100% / MainWindow 0% | 継続抽出 | 高 | ✕（対話セッション推奨） |

**見送り:** MediaDurationReader（65%、残りは ffmpeg 依存パスで SkippableFact 済み）、Mpv/MpvApi/SpoutNative（P/Invoke 宣言のみ）、MainViewModel（8行）。

## Codex 実行時の共通前提（全 goal に適用）

> **重要:** このプロジェクトは `net8.0-windows`（WPF）のため **Linux のクラウド実行環境ではビルド不可**。Windows ローカルの Codex CLI で実行すること。

各 goal プロンプトには以下の共通制約ブロックを含める（プロンプト内に埋め込み済み）:

```
- 検証コマンド（コミット前に全グリーン・ビルド警告0必須）:
  dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj -c Debug --filter "FullyQualifiedName!~E2ETests" -v minimal
- コミットメッセージは日本語（test: / refactor: プレフィックス）
- テストは xUnit + FluentAssertions。テストクラスは対象クラス名+Tests で tests/TimecodeSyncPlayer.Tests/ 直下
- 乱数は固定シード。ffmpeg・実オーディオデバイス・mpv・Spout実機に依存するテストを追加しない
- Path/File/Directory を使うファイルには using System.IO; を明示（グローバルusing対象外）
- リファクタを伴うタスクでは挙動を変えない: 判定条件・実行順序・早期return・ログメッセージ
 （テンプレート文字列・レベル・プロパティ名）を一切変更しない。ログは本番運用の診断に使用されている
- E2E テスト（--filter "FullyQualifiedName~E2ETests"、25件・約4分・実ウィンドウ起動）は
  リファクタを伴うタスクの完了前に1回実行して全グリーンを確認する
```

---

## T1: ViewModel / 設定系の隙間埋め（テスト追加のみ）

**対象:** `PlayerViewModel`（55.2%）、`SyncViewModel`（70.1%）、`AppSettingsManager`（46.6%）
**期待効果:** 純C#クラスの未カバー分岐を確実に潰す。リスクゼロで着手できる肩慣らし。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: 以下3クラスの行カバレッジを85%以上に引き上げるユニットテストを追加する。
プロダクションコード（src/ 配下）は一切変更禁止。テストを通すために本体修正が
必要に見えた場合は作業を止めて報告する。

1. src/TimecodeSyncPlayer/ViewModels/PlayerViewModel.cs（現55%）
2. src/TimecodeSyncPlayer/ViewModels/SyncViewModel.cs（現70%）
3. src/TimecodeSyncPlayer/AppSettings.cs 内 AppSettingsManager（現47%）

手順:
- まず対象クラスと既存の PlayerViewModelTests.cs / SyncViewModelTests.cs /
  AppSettingsTests.cs を読み、未カバーの public 分岐を特定（既存テストと重複させない）
- AppSettingsManager のファイルI/OテストはTempディレクトリ+後始末必須
- カバレッジ確認: dotnet test ... --collect:"XPlat Code Coverage" --results-directory TestResults
  で cobertura XML の line-rate を確認

[共通制約ブロックをここに貼る]

完了条件: 3クラスとも85%以上、全テストグリーン、警告0、日本語コミット。
```

---

## T2: FrameRenderer のテスト追加

**対象:** `FrameRenderer`（166行・6%、既存テスト1件のみ）
**期待効果:** RenderBlack / RenderFrozen / RenderGapFreeze / RenderBuffered / UpdateFromPixelBuffer の描画バッファ操作を検証。Gap freeze 演出の回帰網。
**注意:** WriteableBitmap は WPF オブジェクト（STA スレッド要）。既存 FrameRendererTests.cs がどう扱っているかを必ず先に読んで同じ方式に従う。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: src/TimecodeSyncPlayer/FrameRenderer.cs（現6%カバレッジ）のユニットテストを拡充し
80%以上にする。プロダクションコードは一切変更禁止。

前提知識:
- FrameRenderer は PixelBufferManager（テスト済み・純C#）と ISpoutOutput を
  コンストラクタ注入で受ける。ISpoutOutput はフェイク実装を作って注入する
- WriteableBitmap は STA スレッドが必要。既存 tests/.../FrameRendererTests.cs の
  方式を必ず先に確認し、同じスレッド戦略を踏襲する（xUnitはデフォルトMTA）
- 検証観点: RenderBlack が全ピクセル0を書く / RenderFrozen・RenderGapFreeze が
  対応バッファの内容をビットマップに転写する / RenderBuffered の引数バッファ反映 /
  UpdateFromPixelBuffer のサイズ変更時の Bitmap 再生成と BitmapChanged 発火 /
  ISpoutOutput.SendFrame への転送有無と引数
- ピクセル検証は WriteableBitmap.CopyPixels で読み出して既知パターンと比較する

[共通制約ブロックをここに貼る]

完了条件: FrameRenderer 80%以上、全テストグリーン、警告0、日本語コミット。
```

---

## T3: TimelineDrawingSurface のロジック抽出＋テスト

**対象:** `TimelineDrawingSurface`（604行・9%）
**期待効果:** タイムライン描画の座標計算（ズーム・スクロール・クリップ配置・時間軸目盛）を純ロジック化。UI 上のタイムライン表示崩れの回帰網。
**方針:** DrawingContext への描画呼び出し自体はテストせず、「何をどこに描くか」の計算を `TimelineLayoutCalculator`（仮）等に抽出してテストする。ZoomIn/ZoomOut/ScrollHorizontal/ScrollVertical/SetTrackHeight の状態遷移も抽出対象。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: src/TimecodeSyncPlayer/TimelineDrawingSurface.cs（604行・9%）から描画の
座標・レイアウト計算を純C#クラスに抽出し、ユニットテストで80%以上カバーする。

これは挙動保存リファクタである。描画結果（ピクセル座標・目盛位置・クリップ矩形）を
1pxたりとも変えないこと。抽出は「計算」のみ。DrawingContext への draw 呼び出し、
FrameworkElement/DrawingVisual の管理、DPIスケール取得は TimelineDrawingSurface に残す。

参考にすべき既存パターン: このリポジトリでは MainWindow から
SingleModeSyncCoordinator / ContinueOnTrackCoordinator / GapEnterCoordinator を
同じ方針（計算・判定を抽出、副作用は残すか委譲）で抽出済み。それらのクラスと
テストのスタイルを踏襲する。

手順:
1. TimelineDrawingSurface.cs と既存 TimelineDrawingSurfaceTests.cs（8件）を読む
2. Render/DrawClip/DrawTimeAxis/playhead 更新から座標計算を抽出する設計を決める
   （例: ズーム倍率⇔可視秒数、秒⇔X座標、トラック⇔Y座標、目盛間隔決定）
3. 抽出クラス+テストを追加し、TimelineDrawingSurface を委譲に置換
4. 非E2Eスイート全グリーン確認後、E2Eスイート（約4分・実ウィンドウ起動）も
   1回実行し25件全グリーンを確認する

[共通制約ブロックをここに貼る]

完了条件: 抽出クラス80%以上・非E2E/E2E全グリーン・警告0・日本語コミット
（refactor: と test: の2コミット推奨）。
```

---

## T4: SpoutOutput のシーム導入＋テスト

**対象:** `SpoutOutput`（160行・15%）
**期待効果:** Spout 送信のライフサイクル（TryInitialize 失敗系・SendFrame ガード・Dispose）の検証。実機 SpoutDX.dll がない環境での挙動が特に重要（DLL 欠落はライブ現場で実際に起きる）。
**方針:** `SpoutNative`（static P/Invoke）への直接呼び出しをデリゲートまたは内部インターフェースで包み、フェイクで差し替え可能にする。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: src/TimecodeSyncPlayer/SpoutOutput.cs（15%）にテスト用シームを導入し、
ライフサイクルロジックを80%以上カバーする。

これは挙動保存リファクタである。SpoutNative（P/Invoke）呼び出しの順序・引数・
例外ハンドリング・ログを変えないこと。

方針:
- SpoutOutput 内の SpoutNative.* 直接呼び出しを、コンストラクタ注入可能な
  関数群（デリゲート record か internal interface）に置き換える。
  デフォルトは現行の SpoutNative を呼ぶ実装とし、本番動作を不変に保つ
- 既存の Contracts/ISpoutOutput は変更しない（利用側インターフェース）
- テスト観点: TryInitialize 成功/失敗（DllNotFoundException 含む）で
  IsAvailable が正しく遷移 / 未初期化・IsEnabled=false 時の SendFrame が
  ネイティブを呼ばない / Dispose の解放呼び出しと二重 Dispose 安全性
- 既存 SpoutOutputTests.cs（1件）を読み重複を避ける

手順の最後に非E2E全グリーン確認後、E2Eスイートも1回実行して25件全グリーンを確認。

[共通制約ブロックをここに貼る]

完了条件: SpoutOutput 80%以上・非E2E/E2E全グリーン・警告0・日本語コミット。
```

---

## T5: TimelinePanel.xaml.cs のロジック抽出

**対象:** `TimelinePanel.xaml.cs`（170行・0%）
**期待効果:** タイムラインパネルの操作（シーク要求発火・ズーム/スクロール入力の解釈）の検証。
**方針:** T3 完了後に着手推奨（TimelineDrawingSurface との境界が整理されてから）。マウス/キー入力→操作コマンドへの変換ロジックを抽出。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: src/TimecodeSyncPlayer/TimelinePanel.xaml.cs（170行・0%）から入力解釈ロジックを
抽出しテストする。イベントハンドラの「マウス座標・修飾キー→ズーム/スクロール/シーク
要求への変換」を純C#クラス（例: TimelineInputInterpreter）に抽出し、code-behind は
イベント引数の取り出しと委譲のみにする。

これは挙動保存リファクタである。座標→秒の変換式・ズーム係数・スクロール量・
TimelineSeekEventArgs の発火条件を変えないこと。

既存パターン踏襲: SingleModeSyncCoordinator 等の抽出クラス+Effects record と
そのテスト（デリゲート呼び出しの記録・順序・引数で検証）。

非E2E全グリーン確認後、E2Eスイート（25件・約4分）も1回実行して全グリーン確認。

[共通制約ブロックをここに貼る]

完了条件: 抽出クラス80%以上・非E2E/E2E全グリーン・警告0・日本語コミット。
```

---

## T6: LtcAudioMonitor の部分抽出

**対象:** `LtcAudioMonitor`（234行・0%）
**期待効果:** WASAPI コールバック内のサンプル処理（PCM変換→デコーダ供給→フレームイベント発火・レート推定）の検証。デバイス列挙・キャプチャ開始停止は実機依存のため対象外。
**注意:** 統合テスト `LtcMonitorIntegrationTests` が既にあるので必ず先に読み、重複と役割分担を確認する。

### Codex goal プロンプト

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows。Linuxではビルド不可）

goal: src/TimecodeSyncPlayer/LtcAudioMonitor.cs（234行・0%）のうち、オーディオ
デバイス非依存のサンプル処理パス（NAudio DataAvailable 相当の入力→PcmSampleConverter→
LtcDecoder→FrameReceived イベント発火）を抽出してテストする。

制約:
- WASAPI デバイス列挙・Start/Stop の実デバイス操作はテスト対象外（抽出もしない）。
  実デバイス依存テストを追加しないこと
- 既存 tests/.../LtcMonitorIntegrationTests.cs と LtcAudioMonitorTests が
  あれば先に読み、重複させない
- 挙動保存: イベント発火条件・引数（LtcFrameReceivedEventArgs の各フィールド）・
  ログを変えない
- テストは既存の LtcTestSignalGenerator（tests/Helpers/）で生成した波形バイト列を
  入力に使える

非E2E全グリーン確認後、E2Eスイートも1回実行して全グリーン確認。

[共通制約ブロックをここに貼る]

完了条件: 抽出した処理クラス85%以上・非E2E/E2E全グリーン・警告0・日本語コミット。
```

---

## T7: MainWindow 残り配線の継続抽出（Codex 非推奨）

**対象:** `MainWindow.xaml.cs`（現1700行・0%）
**残っている主な塊:** LoadFile / SeekTo / StopPlayback の再生操作コア、プロジェクト保存・読込フロー（BtnSaveProject/BtnLoadProject）、プレイリスト D&D ハンドラ群、Window_Loaded / CreateRenderContext の起動系、ReadDurationsInBackground。
**推奨:** 同期パス抽出と同じく、対話セッション（サブエージェント駆動 + タスク毎レビュー + E2E 基線比較）で1塊ずつ。自律エージェントに丸ごと投げるにはリスクが高い（mpv ハンドルのライフサイクルと UI スレッド制約が絡む）。着手時はこのファイルの Global Constraints と `docs/ARCHITECTURE.md`、`.superpowers/plans/mainwindow-sync-extraction-plan.md`（前回プランの書式）を参照。

---

## 実行順の推奨

1. **T1**（リスクゼロ・即効）→ 2. **T2**（テストのみ・STA注意）→ 3. **T4**（小さい挙動保存リファクタ）→ 4. **T3**（大物・T5 の前提）→ 5. **T5** → 6. **T6** → 7. **T7**（対話セッションで）

各タスク完了ごとにカバレッジを再計測し、この表の実測値を更新すること。

---

## 一括実行ルール（T1〜T7 自律実行時に適用）

T1〜T7 を一括で自律実行するエージェントは、以下のルールに従うこと。

### 実行プロセス（全タスク共通・厳守）

1タスクずつ直列に進める。各タスクで:
1. ロードマップの該当セクションを読み、対象ソースと既存テストを読む
2. 実装する。「共通前提」セクションの制約ブロックが全タスクに適用される
3. 検証: dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj
   -c Debug --filter "FullyQualifiedName!~E2ETests" -v minimal → 全グリーン・警告0
4. プロダクションコードを変更したタスク（T3/T4/T5/T6/T7）では追加で E2E を実行:
   --filter "FullyQualifiedName~E2ETests"（25件・約4分・実ウィンドウが開く）→ 全グリーン
5. カバレッジ再計測（--collect:"XPlat Code Coverage"）し、目標値達成を確認
6. タスク単位でコミット（日本語メッセージ）。ロードマップの表の実測値も更新して
   同コミットに含める
7. docs/test-coverage-progress.md に1行追記:
   「T# 完了 (コミットSHA, テスト件数, 対象カバレッジ%, E2E結果)」
   ※このファイルが既にあれば続きから再開する（完了済みタスクをやり直さない）

### 停止条件（該当したら作業を止めて状況を報告する。無理に進めない）

- E2E が1件でも落ちた → 直前のタスクのコミットを revert せず、原因分析を書いて停止
- テスト追加のみのタスク（T1/T2）でプロダクションコード変更が必要に見えた → 停止
- 挙動保存の判断に迷った（条件・順序・ログを変えずに抽出できない）→ 停止
- 同一の失敗に3回連続で対処できなかった → 停止

### T7 の特則（MainWindow 残り配線の抽出）

T7 はロードマップ上「対話セッション推奨」だが、一括実行時は以下の縮小ルールで
可能な範囲のみ着手する:
- 1回の抽出は1メソッド群（例: プロジェクト保存・読込フローのみ）に限定し、
  抽出ごとに 非E2E + E2E + コミットのフルサイクルを回す
- 着手順: (1) BtnSaveProject/BtnLoadProject フロー (2) プレイリストD&Dハンドラ群
  (3) ReadDurationsInBackground。ここまでで停止し、残り
  （LoadFile/SeekTo/StopPlayback コア・Window_Loaded/CreateRenderContext 起動系）は
  mpv ハンドルと UI スレッド制約が絡むため着手せず「未着手」と報告する
- 抽出パターンは SingleModeSyncCoordinator / ContinueOnTrackCoordinator /
  GapEnterCoordinator（+ Effects record + テスト）を厳密に踏襲する

### 禁止事項

- git push しない（コミットまで。push は人間がレビュー後に行う）
- ブランチ運用: 開始時に main から test/coverage-roadmap ブランチを切り、全作業を
  そこで行う。main に直接コミットしない
- 追跡外ファイル AGENTS.md が存在する場合、ステージしない（git add -A 禁止、
  常に明示パスで add）
- テストを通すための本体ロジック変更・テストの Skip 化・アサーション弱体化

### 完了報告

全タスク完了（または停止）時に、docs/test-coverage-progress.md へ最終サマリを書く:
タスク毎の結果表 / 全体カバレッジの前後比較 / 未着手・スキップした項目と理由 /
人間がレビューすべきポイント。

---

## マスター goal プロンプト（T1〜T7 一括実行用・そのまま貼る）

```
リポジトリ: timecode-sync-player（Windows WPF / net8.0-windows）。
Linux のクラウド実行環境ではビルド不可のため、Windows ローカルで実行すること。

goal: docs/test-coverage-roadmap.md を全文読み、テストカバレッジ拡充タスクを
T1 → T2 → T4 → T3 → T5 → T6 → T7 の順に実行する。
各タスクの要件は各セクションの goal プロンプトに、進め方・検証ゲート・停止条件・
T7 の特則・禁止事項は「一括実行ルール」セクションに全て書いてある。厳守すること。
完了・停止時は docs/test-coverage-progress.md に報告を書く。
```
