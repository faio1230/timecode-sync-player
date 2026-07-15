# 残件処理プラン: レビューMinor一括 + MainWindow コア抽出

作成: 2026-07-15（main = 19d30c5、非E2E 861件 / E2E 28件[実機LTC 3件含む] 全グリーン）

これまでのレビューで延期した Minor 群と、テストカバレッジロードマップ T7 で未着手だった
MainWindow の再生操作コア・起動系の抽出を仕上げる。

**実行ルールは docs/test-coverage-roadmap.md の「一括実行ルール」セクションに全面的に従う**
（検証ゲート・停止条件・禁止事項・progress 記録）。本プランでの上書き事項:
- ブランチ: refactor/mainwindow-core-extraction
- E2E ゲートは**全28件**（実機 LTC 3件を含む。この開発機は VB-CABLE エンドポイント有効）。
  実機3件が Skip された場合はゲート不合格として扱い、原因を確認すること
- 挙動保存リファクタの規律は従来どおり: 判定条件・実行順序・早期return・ログメッセージ
  （テンプレート・レベル・プロパティ名）を一切変えない
- 抽出パターンは既存の Coordinator + Effects record（SingleModeSyncCoordinator /
  ContinueOnTrackCoordinator / GapEnterCoordinator / ProjectFileCoordinator）を厳密に踏襲。
  Coordinator は MainWindow のフィールドとして遅延初期化・再利用し、Effects の各デリゲートは
  呼び出し時点でフィールドを読むこと（構築時の値キャプチャ禁止）

## M0: レビュー Minor の一括解消（ウォームアップ・1コミット可）

テスト/ドキュメントのみの項目:
1. `tests/.../Helpers/LtcSignalPlayer.cs` FindActiveDevice: 非マッチの MMDevice と
   コレクションを Dispose する（COMリーク解消）
2. `tests/.../E2E/LtcHardwareLoopE2ETests.cs` SelectFixed25Fps: 選択が反映されたことを
   WaitUntil で検証（同ファイルの SelectCableCaptureDevice と同じ流儀）
3. 同ファイル TryReadPlaybackPosition: フレーム→秒変換に LTC 用 Fps 定数(25)を使っている
   のはテスト動画の fps と偶然一致しているだけである旨のコメントを追加
4. `tests/.../FrameRendererTests.cs` RunOnSta: 各テスト後に
   `Dispatcher.CurrentDispatcher.InvokeShutdown()` を finally で実行
5. `tests/.../SpoutOutputTests.cs` TryInitialize_UnexpectedExceptionAfterCreateCleansUpObject:
   実態（Open失敗 + Destroy例外）に合う名前へリネーム
6. `docs/test-coverage-roadmap.md` タスク表ヘッダ「実測(抽出前)」→「実測(現在)」

限定的に src/ 変更を許可する項目（それぞれ独立コミット・挙動保存）:
7. `src/TimecodeSyncPlayer/LtcAudioSampleProcessor.cs` Process: WASAPI コールバック毎の
   `new List<LtcFrameReceivedEventArgs>()` を再利用バッファ化（フレーム0件時の割り当てゼロに）。
   イベント発火順序・引数・sender を変えないこと
8. `src/TimecodeSyncPlayer/AppSettings.cs` AppSettingsManager のテスト用 private ctor を
   internal 化し、`tests/.../AppSettingsTests.cs` のリフレクション呼び出しを直接呼び出しに置換
9. `src/TimecodeSyncPlayer/GapEnterCoordinator.cs` StartGapFreezeCapture の target<=0 パスで
   使われない duration/fps 計算（旧コードからの忠実移植）を、`??` の遅延評価を保ったまま
   使用箇所へ移動して整理（挙動不変を確認の上。自信がなければスキップして報告）

## M1: 再生操作コアの抽出（PlaybackOperationsCoordinator）

**対象:** `MainWindow.xaml.cs` の `LoadFile` / `SeekTo` / `StopPlayback` / `ApplyPauseState`
（mpv コマンド発行・再生状態・シーク抑制・OSD 制御の中核）

- 依存の多く（MpvPlaybackCommandBuilder / TimecodeSyncService / PlaybackControlState 等）は
  テスト済み。mpv ハンドル(_mpv)は Effects デリゲート経由で呼び出し時に読むこと
- これらは Coordinator 群（ContinueOnTrack / GapEnter / ProjectFile）からもデリゲートとして
  参照されている。**既存 Coordinator への配線（デリゲートの署名・渡し方）を変えないこと** —
  MainWindow 側の委譲メソッドを残し、その中身だけを新 Coordinator 呼び出しに置換する
- テスト: 新 Coordinator に対し、ロード成功/失敗、シーク送信条件と suppressOsd、
  停止時の状態リセット順序をデリゲート記録で検証
- 完了ゲート: 非E2E 全件 + E2E 全28件（実機3件 Skip なし）

## M2: 起動系の抽出（Window_Loaded / CreateRenderContext まわり）

**対象:** `MainWindow.xaml.cs` の `Window_Loaded` / `CreateRenderContext` の残り配線
（WindowLoadedSessionInitializer / MpvSessionInitializer / RenderContextParameterBuilder /
StartupBufferInitializer 等の既存部品は活かし、未抽出の配線のみを対象とする）

- mpv render context の生成・コールバック登録は CLAUDE.md の既知の問題
  （デリゲートのフィールド保持必須・MpvRenderParam のパディング）に直結する。
  **P/Invoke 呼び出しとデリゲート保持の構造は一切動かさず**、判定・分岐・順序の
  オーケストレーションだけを抽出すること
- 迷ったら抽出範囲を狭める方向で判断し、狭めた理由を progress に記録する
- 完了ゲート: M1 と同じ + アプリが実際に起動して映像が出ることを実機 E2E
  （既存 Playback 系テスト）が保証していることを確認

## 実行順

M0 → M1 → M2。各段階でコミットを分け、E2E ゲートを通過してから次へ。
M2 で停止条件に該当したら、M0/M1 の成果を保持したまま blocked として報告すること。
