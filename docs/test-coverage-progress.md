# テストカバレッジ拡充進捗

- T1 完了 (コミット 0355c11, 非E2E 771件, PlayerViewModel 100% / SyncViewModel 96.9% / AppSettingsManager 93.2%, E2E対象外)
- T2 完了 (コミット 589cc53, 非E2E 783件, FrameRenderer 100%, E2E対象外)
- T4 完了 (コミット c89aa56, 非E2E 800件, SpoutOutput 100%, E2E 25/25合格)
- T3 完了 (コミット 907832b, 非E2E 831件, TimelineLayoutCalculator 100% / TimelineDrawingSurface 8.4%, E2E 25/25合格)
- T5 完了 (コミット 9de32eb, 非E2E 838件, TimelineInputInterpreter 100% / TimelinePanel 0%, E2E 25/25合格)
- T6 完了 (コミット 6cd5a36, 非E2E 842件, LtcAudioSampleProcessor 100% / LtcAudioMonitor 0%, E2E 25/25合格)
- T7 完了・特則範囲で停止 (コミット d8308e3 / b92071d / 65efb73, 非E2E 861件, ProjectFileCoordinator / PlaylistDragDropCoordinator / PlaylistDurationBackfillCoordinator 各100%・MainWindow 0%, 各抽出後E2E 25/25合格)

## 最終サマリ

| タスク | 結果 | コミット | 非E2E | 対象カバレッジ | E2E |
|---|---|---|---:|---|---|
| T1 | 完了 | `0355c11` | 771件合格 | PlayerViewModel 100% / SyncViewModel 96.9% / AppSettingsManager 93.2% | 対象外 |
| T2 | 完了 | `589cc53` | 783件合格 | FrameRenderer 100% | 対象外 |
| T4 | 完了 | `c89aa56` | 800件合格 | SpoutOutput 100% | 25/25合格 |
| T3 | 完了 | `907832b` | 831件合格 | TimelineLayoutCalculator 100% / TimelineDrawingSurface 8.4% | 25/25合格 |
| T5 | 完了 | `9de32eb` | 838件合格 | TimelineInputInterpreter 100% / TimelinePanel 0% | 25/25合格 |
| T6 | 完了 | `6cd5a36` | 842件合格 | LtcAudioSampleProcessor 100% / LtcAudioMonitor 0% | 25/25合格 |
| T7.1 | 完了 | `d8308e3` | 846件合格 | ProjectFileCoordinator 100% | 25/25合格 |
| T7.2 | 完了 | `b92071d` | 857件合格 | PlaylistDragDropCoordinator 100% | 25/25合格 |
| T7.3 | 完了 | `65efb73` | 861件合格 | PlaylistDurationBackfillCoordinator 100% | 25/25合格 |

全体の非E2E行カバレッジは 53.2% から 62.69% へ 9.49ポイント増加した。最終ゲートは非E2E 861/861件、E2E 25/25件、ビルド警告0。

### 未着手・スキップ

- `LoadFile` / `SeekTo` / `StopPlayback` の再生操作コア: T7特則で mpv ハンドルとUIスレッド制約が絡むため着手禁止。
- `Window_Loaded` / `CreateRenderContext` の起動系: T7特則で着手禁止。
- 実オーディオデバイス、mpv、Spout実機へ依存する新規テスト: 共通制約に従い追加していない。

### 人間がレビューすべきポイント

- `TimelineDrawingSurface` から抽出した座標式が、DPI倍率・境界比較・演算順序を含めて従来描画と一致していること。
- `SpoutOutput` の native API シームが、実DLLでの呼出し順序・例外処理・senderライフサイクルを変えていないこと。
- `LtcAudioSampleProcessor` 導入後も、オーディオコールバック上のイベント sender、イベント順序、統計ログ値が従来どおりであること。
- MainWindow の3 Coordinator Effects 配線が、UI Dispatcher、ダイアログ、D&DのHandled設定を従来と同じ順序で実行すること。

## 実機 LTC ループ E2E（2026-07-15）

- H1 完了（コミット `2535c14`）: `LtcSignalPlayer` を追加し、WASAPI Shared で
  `CABLE Input` へ連続 LTC フレームを送出する処理と、例外安全な停止・破棄を実装。
- H2 / H3 実装完了（コミット `ae8a5e2`）: `CABLE Output` を選択する表示追従・停止時リセット・
  信号断・20秒動画の同期シーク E2E を追加。録音デバイス不在時、および同期シークの
  ffmpeg 不在時は規約どおりスキップする。
- H4 完了（コミット `216b716`）: README に目的、VB-CABLE の導入先、再起動の注意、
  実行コマンド、自動スキップを追記。
- 非E2Eゲート: 861/861件合格、スキップ0、ビルド警告0。
- 全E2Eゲート: 既存25件合格、実機LTC 3件スキップ、失敗0（合計28件、4分15秒）。
- 実機LTC抽出実行: 0件合格、3件スキップ。理由は、Windows Core Audio の有効な
  録音エンドポイントに `CABLE Output` が見つからないため。
- PnP 上では `VB-Audio Virtual Cable`（`ROOT\\MEDIA\\0008`）は正常だが、現在の
  実行環境は RDP セッション（Session 1）で、Present な AudioEndpoint は
  `リモート オーディオ` のみ。VB-CABLE の `CABLE Input` / `CABLE Output` 子エンドポイントは
  このセッションに公開されていない。デバイス再起動・再スキャンも管理者権限不足で実行できなかった。
- **停止**: 「VB-CABLE がある本機ではスキップされず実行」の完了ゲートに未達。
  ローカルコンソールの音声セッションで両エンドポイントを有効にし、必要なら Windows を
  再起動した後、次を再実行すること。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~LtcHardwareLoop" --logger "console;verbosity=detailed"
```

プロダクションコード（`src/`）は変更しておらず、push も実施していない。
