# Native Dependencies

このフォルダは、ソースからビルドするときに必要なネイティブDLLと、動画再生に使う
GStreamerランタイムの案内です。
`native/*.dll` は `.gitignore` 対象であり、リポジトリにはDLL本体を含めません。

## GStreamer 1.28.2 ランタイム（必須）

配布パッケージには、実際に使う DLL・プラグインとライセンス文書を `gstreamer\` に同梱しています。
**ソースからビルドして動かす場合のみ**、公式の**MSVC x64ランタイム 1.28.2**を導入してください
（[GStreamer公式ダウンロード](https://gstreamer.freedesktop.org/download/)）。

- 既定の探索先: `C:\Program Files\gstreamer\1.0\msvc_x86_64`
- 別の場所へインストールした場合: 環境変数`GSTREAMER_1_0_ROOT_MSVC_X86_64`にルート
  （`bin`の親ディレクトリ）を設定します。

## erytheia_gstreamer.dll（必須）

GStreamerのデコード結果をD3D11テクスチャのリースAPIとして合成層へ公開するshimです。
ビルドにはVisual Studio Build ToolsのC++ x64ツール、CMake、Ninja、GStreamerの開発用SDKが必要です。ランタイムだけではビルドできません。リポジトリのルートからPowerShell 7で実行します。

```powershell
$env:GSTREAMER_1_0_ROOT_MSVC_X86_64 = 'C:\Program Files\gstreamer\1.0\msvc_x86_64'
# include\gstreamer-1.0\gst\gst.hがある開発用SDKを指定する
# Spout2 (tag 2.007.017) を vendor/Spout2 へ取得（git管理外）
pwsh -File native\gst-shim\get-spout.ps1
if ($LASTEXITCODE -ne 0) { throw 'Spout2の取得に失敗しました。' }
# Debugビルド
pwsh -File native\gst-shim\build-shim.ps1 -Config Debug
if ($LASTEXITCODE -ne 0) { throw 'shimのビルドに失敗しました。' }
Copy-Item -LiteralPath native\gst-shim\build-debug\erytheia_gstreamer.dll -Destination native\erytheia_gstreamer.dll -Force -ErrorAction Stop
Get-FileHash -Algorithm SHA256 -LiteralPath native\gst-shim\build-debug\erytheia_gstreamer.dll, native\erytheia_gstreamer.dll
```

`native\erytheia_gstreamer.dll`へ置くか、`build-debug`に出力したままにすると、本体のビルド時に
`src\Erytheia\bin\Debug\net8.0-windows\`へ自動コピーされます。

標準のインストール先でも、shimのビルド時には上の環境変数が必要です。`native\`にDLLが残っている場合はそちらが優先されます。`TCS0001`で止まった場合も現在のソースから再ビルドし、配置先のSHA-256が一致することを確認してください。Debug／Releaseの切替とテスト出力先の確認は[セットアップ手順](../docs/SETUP.md)にまとめています。

## SpoutDX.dll

配布zip／インストーラーには`SpoutDX.dll`を同梱します。zip版の利用者が別途用意する必要はありません。

ソースからビルドする場合のみ、[Spout2](https://github.com/leadedge/Spout2)のSDKから
x64版`SpoutDX.dll`を用意し、このフォルダへ配置してください。DLLが無い場合、Spout を有効にすると
送信 worker の起動に失敗します。有効にしなければ動画再生やLTC同期は通常どおり動作します。

## 配置例

```text
timecode-sync-player/
└── native/
    ├── erytheia_gstreamer.dll  # 必須（ソースビルド時）
    └── SpoutDX.dll        # Spout出力を使う場合
```

配置後は[セットアップ手順](../docs/SETUP.md)に従ってビルドしてください。
