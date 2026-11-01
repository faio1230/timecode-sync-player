# 開発前の検証記録（2026-10-14）

文書整理と今後のリファクタリングに向けて、現在の製品・テストコードを変更せずにDebugビルドと非E2E全件を実行した。非E2Eは3,323件合格、失敗0件、スキップ0件だった。Release配布物や実機E2Eの合格を示す記録ではない。

## 対象と結果

| 項目 | 記録 |
|---|---|
| ソース | `main`、`596bb28526eb7564a90862cca1d2a4be162f4943` |
| 製品の版 | v0.6.6 |
| 差分 | 文書のみ。製品コード・テストコード・依存パッケージ定義の変更なし |
| 構成 | Debug、`net8.0-windows`、x64 |
| 本体のビルド | 成功、警告0、エラー0 |
| 非E2E全件 | 合計3,323、実行3,323、合格3,323、失敗0、スキップ0 |
| スキップ理由 | 該当なし |
| テスト実行時刻 | 2026-10-14 02:09:34〜02:09:50 JST。TRXのstart／finish |
| ローカルの記録先 | `artifacts/verification/2026-10-14-development-baseline/` |

記録先の`build.log`、`non-e2e.log`、`non-e2e.trx`はGit管理外で、この開発機に保存している。別の機体で結果を比較する場合は、これらのファイルも渡す。TRXのSHA-256は`5679769bc951a8619533ae27961dcc31952d308ed85a5d2614760612197e0003`。

同日に先行して実行した関連50件と実DLLを使う2件の合格は、対象を絞った確認だった。今回の全件結果に加算しない。`RenderSessionTests`の待ち方について挙げた修正候補も、コードは変更していない。この1回の合格だけで間欠的な失敗を解消したとは判断しない。

## 実行環境

| 項目 | 値 |
|---|---|
| OS | Windows 11 Pro、10.0.26200 |
| GPU | NVIDIA GeForce RTX 3070 |
| GPUドライバ | 32.0.15.9186 |
| PowerShell | 7.6.5 |
| .NET SDK | 10.0.202 |
| .NET 8 Runtime | Microsoft.NETCore.App／Microsoft.WindowsDesktop.App 8.0.21 |
| ネイティブビルド | Visual Studio Build Tools 18.5.0、MSVC 14.50.35717、CMake／Ninja、Debug |
| GStreamer | 1.28.2 MSVC x64、標準の`C:\Program Files\gstreamer\1.0\msvc_x86_64` |
| ffmpeg／ffprobe | `scripts/get-ffmpeg.ps1`で取得した8.0.1 full、`tools/ffmpeg/` |
| ProResプラグイン | 同梱対象のv0.2.3。下表のDLLで確認 |

shimのビルド時は`GSTREAMER_1_0_ROOT_MSVC_X86_64`を上記の場所へ設定した。テスト実行時はこの変数、`TCS_FFMPEG`、`TCS_FFPROBE`、`TCS_FFMPEG_TOOLS_DIR`はいずれも未設定。GStreamerは標準配置、ffmpegはリポジトリ内の固定版を使う条件で実行した。

## DLLとツールの確認

作業開始前は、`native/tcs_gstreamer.dll`と`build-debug`のDLLがともに10月2日のままで、ハッシュも異なっていた。10月7日のshim修正より古いため、既存DLLのコピーだけでは済ませず、現在のソースからDebug版を再ビルドした。ビルド時にSDKの環境変数が未設定で一度停止し、標準配置のSDKを指定して成功した。ネイティブビルドではコードページに関するC4819警告が出たが、エラーはなかった。

再ビルド後に`native/`へ配置し、本体とテストのビルド後に以下の4か所のSHA-256が一致することを確認した。`AllowStaleNativeShim`は使用していない。

- `native/gst-shim/build-debug/tcs_gstreamer.dll`
- `native/tcs_gstreamer.dll`
- `src/TimecodeSyncPlayer/bin/Debug/net8.0-windows/tcs_gstreamer.dll`
- `tests/TimecodeSyncPlayer.Tests/bin/Debug/net8.0-windows/tcs_gstreamer.dll`

| ファイル | SHA-256 |
|---|---|
| 上記4か所のshim | `21f7cde8ce34e5b4043c95434876aee244bdc9dff68f0892ff8e54b6cc1aa817` |
| `native/gst-prores/gstproresd3d11.dll` | `1ded64c41bfe9709e188acc071c654147c17c2b7113d25e0105a6f5f120e2a8f` |
| `native/SpoutDX.dll` | `bbcee6f0031f6bd1a6461585c53f3f8f3cecbf006b486661bcde6413e2addef5` |
| `tools/ffmpeg/ffmpeg.exe` | `74db6c184a03dba2bdfe23e1a1f41cf5a8385bc1de6a7a1b26db1dc541abef93` |
| `tools/ffmpeg/ffprobe.exe` | `55bb6c6289367ae2383efa86b26bf2596f8adb72ac747360eb13df162354161c` |

## 実行コマンド

DLLの準備は[セットアップ手順](../SETUP.md)に記載した手順で行った。以下は全件検証を再現するコマンドで、リポジトリのルートから実行する。今回、NuGetの依存関係は復元済みだったため`--no-restore`を指定した。

```powershell
pwsh -NoProfile -File scripts/get-ffmpeg.ps1
if ($LASTEXITCODE -ne 0) { throw 'ffmpegの取得に失敗しました。' }
$baselineDir = Join-Path $PWD 'artifacts/verification/2026-10-14-development-baseline'
New-Item -ItemType Directory -Path $baselineDir -Force | Out-Null
dotnet build src/TimecodeSyncPlayer/TimecodeSyncPlayer.csproj --configuration Debug --no-restore --verbosity minimal 2>&1 | Tee-Object -FilePath (Join-Path $baselineDir 'build.log')
if ($LASTEXITCODE -ne 0) { throw '本体のビルドに失敗しました。' }
dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj --configuration Debug --no-restore --filter 'FullyQualifiedName!~E2ETests' --logger 'trx;LogFileName=non-e2e.trx' --results-directory $baselineDir --verbosity minimal 2>&1 | Tee-Object -FilePath (Join-Path $baselineDir 'non-e2e.log')
if ($LASTEXITCODE -ne 0) { throw '非E2Eテストが失敗しました。' }
```

初回環境では通常の`dotnet build`で依存関係を復元する。再検証では結果を上書きしないよう、`$baselineDir`を新しい実行名に変える。

## 未実施の範囲

UIを操作するE2E、実音声デバイスを使うLTCループ・長時間シナリオ、外部のSpout受信側と全画面出力の目視確認、Release配布物・新規ユーザー環境での検証は、この回では実施していない。公開前の実機確認には[検証チェックリスト](../verification-checklist.md)を使う。

v0.6.6公開時の検証は[リリースノート](../release-notes/v0.6.6.md)、10月8〜9日の試験基盤の変更と検証は[試験基盤の記録](../design/test-infra-2026-10.md)を参照する。今回のローカルのDebug検証と、配布候補を使った過去の検証は、それぞれの条件で比較する。
