# GStreamer 移行 最終報告（2026-09-11）

## 1. 作業環境

- worktree: `C:\Users\<user>\Documents\timecode-sync-player-wt-gstreamer-20260911-0141`
- ブランチ: `codex/gstreamer-migration-20260911-0141`
- 基点 SHA: `d3bb1b0b9725201b6e403ec5bfbd00a85c6a4f79`（main、未コミット変更なしを確認して作成）
- 元リポジトリへの変更・他エージェント資産への変更なし（参照のみ: session-refactor worktree の
  `docs/OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md`）

## 2. コミット（日本語）

| commit | 内容 |
| --- | --- |
| 8a7beef | 試作: GStreamer→D3D11→Spout2 の GPU 内経路を単体検証可能に成立 |
| 32c83f6 | shim v3: 合成層向け GPU フレームソース契約（世代付きリース API・外部デバイス共有・明示コーデック分岐） |
| 50b74ba / 0c667d1 | .gitignore 修正（build 出力の誤登録除去） |
| 163de15 | shim: decoder 名・Spout 可否の直接ゲッター |
| c67dfe2 | 統合: GStreamer バックエンドの C# 接続層（既定は mpv 維持） |
| 1b92012 | shim v4: 音声チェーン動的化・世代セマンティクス・ステップ再プル |
| b0ff609 | 検証: GST 実機 E2E 追加と GStreamer ランタイム検出修正 |
| 813ee4f | 検証: 切り替え反復・再生中終了・受信再起動 E2E、コンテナ/メタデータ修正 |
| 543701f | 配布・ライセンス整備（本報告含む） |

## 3. 実装内容と依存バージョン

- `native/gst-shim/tcs_gstreamer.dll`（自作 MIT、C ABI）: GStreamer デコード→GPU 色変換→
  D3D11 テクスチャを「世代付きリース」で合成層へ供給するフレームソース。
  `video/x-hap` は専用分岐の実装まで拒否、未知コーデックは既定拒否。
- C# 接続層（`src/TimecodeSyncPlayer/Gst/` 9 ファイル + SpoutFramePublisher 分岐）:
  既存 `IMpvApi`/`IMpvRenderApi`/`ISpoutOutput` の実装差し替えでバックエンド切替。
  設定 `backend`（0=mpv 既定 / 1=GStreamer）。
- 依存: GStreamer 1.28.2 (MSVC x64, 公式ランタイム **非同梱**・動的リンク)、
  Spout2 SDK tag 2.007.017 (commit f49e2f4、BSD-2、shim へ静的組み込み)、
  .NET 8 / WPF、既存 NuGet は変更なし。

## 4. 実行手順

```powershell
# ビルド（GStreamer バックエンドのネイティブ部）
powershell -File native/gst-shim/get-spout.ps1
powershell -File native/gst-shim/build-shim.ps1 -Config Debug
dotnet build src/TimecodeSyncPlayer/TimecodeSyncPlayer.csproj

# 検証（要 GStreamer ランタイム）
$env:PATH = "C:\Program Files\gstreamer\1.0\msvc_x86_64\bin;" + $env:PATH
native\gst-shim\build-debug\tcs-shim-test.exe artifacts\media\test_1080p60.mp4 2   # 機能
native\gst-shim\build-debug\tcs-shim-test.exe --stress artifacts\media\*.mp4 120   # 反復
# アプリ E2E（backend=Gstreamer で起動〜Spout 受信〜切替〜終了まで）
dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~GStreamerBackendE2ETests"
```

## 5. テスト結果（実測）

- 全スイート **1253/1253 合格**（非 E2E 1200 + mpv E2E 42 + GStreamer E2E 3）。
  条件: RTX 3070 / GStreamer 1.28.2 / Debug / Windows、他エージェントの GPU 試験とは
  時間帯を分離（実施可否を確認の上で実行）。
- shim 機能テスト: mp4 1080p60 連続 5 回 failures=0、コンテナ別（mkv/avi/ts/hevc）も
  GPU 経路で failures=0。外部デバイス Adopt はリーステクスチャの `GetDevice()` 一致で確認。
- 切り替え反復: 4 素材×120 回で load 失敗 0、作業セットは warmup 後 ~160MB で飽和。
- アプリ E2E: 1080p60 GPU 経路の実フレームを受信側プロセスで確認（受信側再起動後も
  接続・フレームイベント・アプリ描画が継続）、4 コンテナの next/prev 反復、再生中クローズ
  終了コード 0。
- 性能参考値（720p60/15s/Spout OFF/Debug、他プロセス GPU 使用あり）:
  mpv CPU 73.1%（1 コア換算）/ WS 平均 251.5MB → GStreamer 46.7% / 230.2MB。
  GStreamer の Spout 公開は GPU テクスチャ送信で avgSpoutMs≈0.13ms（アプリログ実測）。

## 6. 未検証事項・既知の問題

1. 長時間連続再生・数十時間運用は未検証（メモリは 120 回切り替えで飽和を確認済み）。
2. Spout 受信の 2 個目プロセスは、SDK のフレーム同期の都合でコピー画像が更新されない
   ことがある（proto 送信では再起動後の内容更新を実測済み。製品では合成層が受信）。
3. WPF プレビューは暫定的に毎フレーム全解像度 CPU コピー（合成層接続までの制約）。
4. LTC 同期は既存ロジックを共有するが、GST バックエンドでの実 LTC 入力試験は未実施
   （ユニット/シナリオテストは合格）。
5. video/x-hap は未実装（意図的拒否）。未知コーデックの decodebin フォールバックは
   デバッグ専用（TCS_ALLOW_UNKNOWN=1）。
6. 音声は autoaudiosink 経由の基本経路のみ（デバイス選択 UI は未接続）。
7. `hwdec` の事実訂正: 本ブランチの基点では `hwdec=auto-copy`
   （MpvStartupPropertyApplier.cs:17）。`hwdec=no` は他エージェント側変更と思われるため
   統合時に照合すること。
8. 履歴注意: 32c83f6 にビルド出力が一度混入し 50b74ba で除去済み（tip はクリーン。
   squash 統合する場合は影響なし）。

## 7. GStreamer を既定にできるか（根拠つき結論）

**現時点では既定にしない（mpv 既定を維持）**。根拠:

- フレーム公開・切替・終了・コンテナ/コーデックの基本動作は実機で成立したが、
  検証機は 1 台 1 GPU、検証時間も限定的（長時間・負荷変動・異常系は部分検証）。
- 製品の出口契約は「合成層へのフレームソース」であり、合成層側の接続（リース消費・
  共有フェンス・出力 worker）が未完。現状のアプリ接続は暫定プレビュー経路を含む。
- HAP 未対応・未知コーデック拒否など、mpv より対応範囲が狭い。
- 受信再起動時に観測した SDK 側の受信画像更新の揺らぎは、合成層実装時に再評価が必要。

**既定化への道**: ①合成層をリース API の消費者として接続 → ②LTC 実入力での同期試験 →
③長時間・高負荷・異常系の実機検証 → ④HAP/未知コーデック方針の決定 → ⑤設定既定を
`backend:1` に変更（切替は設定 1 行で可能な構造済み）。

## 8. 既存ファイルの変更と統合注意点

変更（8 ファイル、いずれも小規模・追加中心）:

| ファイル | 変更 | 統合注意 |
| --- | --- | --- |
| `src/.../App.xaml.cs` | DI をバックエンド別ファクトリへ | session-refactor が App/DI を触る場合は競合注意 |
| `src/.../AppSettings.cs` | `PlayerBackend` enum + `backend` キー + 検証 | 追加のみ。既存キーは不変 |
| `src/.../MpvLibraryNameResolver.cs` | 同一アセンブリ resolver に tcs_gstreamer.dll を委譲 | resolver は 1 本のため二重登録不可。他変更と要マージ |
| `src/.../SpoutFramePublisher.cs` | `IGpuSpoutPublisher` 実装時は GPU 送信を優先 | 出力契約の refactor と衝突しやすい箇所 |
| `src/.../TimecodeSyncPlayer.csproj` | `tcs_gstreamer.dll` の出力コピー条件 | 追加のみ |
| `tests/.../SpoutFramePublisherTests.cs` | GPU 分岐テスト追加 | 追加のみ |
| `THIRD-PARTY-NOTICES.md` | GStreamer 節 + Spout 注記 | 追記。他の追記と要マージ |
| `.gitignore` | vendor/、build 出力 | 追加のみ |

新規: `src/TimecodeSyncPlayer/Gst/*`（9）、`Contracts/IGpuSpoutPublisher.cs`、
`GstSpoutOutput.cs`、`tests/.../Gst/*`、`tests/.../E2E/GStreamerBackendE2ETests.cs`、
`native/gst-shim/**`（shim 本体・proto・スクリプト・README）。

## 9. mpv 経路へ戻す方法

- 既定は mpv のまま（設定未変更ならそのまま）。
- GStreamer を試した後は `settings.json` の `"backend"` を 0 にするか削除
  （`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`、検証時は
  `TIMECODE_SYNC_PLAYER_SETTINGS_PATH` で分離可能）。
- 完全撤去する場合: `Gst*` 実装の DI 登録と csproj の DLL コピーを戻し、
  `SpoutFramePublisher` の 3 行分岐を戻せば元の mpv 専用構成に戻る（他は追加ファイル）。
