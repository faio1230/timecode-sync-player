# Native Dependencies

このフォルダは、ソースからビルドするときに必要なネイティブDLLと、動画再生に使う
GStreamerランタイムの案内です。
`native/*.dll` は `.gitignore` 対象であり、リポジトリにはDLL本体を含めません。

## GStreamer 1.28.2 ランタイム（必須）

動画再生には公式の**MSVC x64ランタイム 1.28.2**が必要です。本リポジトリには同梱しません。
[GStreamer公式ダウンロード](https://gstreamer.freedesktop.org/download/)から導入してください。

- 既定の探索先: `C:\Program Files\gstreamer\1.0\msvc_x86_64`
- 別の場所へインストールした場合: 環境変数`GSTREAMER_1_0_ROOT_MSVC_X86_64`にルート
  （`bin`の親ディレクトリ）を設定します。

## tcs_gstreamer.dll（必須）

GStreamerのデコード結果をD3D11テクスチャのリースAPIとして合成層へ公開するshimです。
次の手順でビルドします（Visual Studio Build Tools + CMake + Ninja が必要です）。

```powershell
# Spout2 (tag 2.007.017) を vendor/Spout2 へ取得（git管理外）
powershell -File native\gst-shim\get-spout.ps1
# Debugビルド
powershell -File native\gst-shim\build-shim.ps1 -Config Debug
# 出力: native\gst-shim\build-debug\tcs_gstreamer.dll
```

`native\tcs_gstreamer.dll`へ置くか、`build-debug`に出力したままにすると、本体のビルド時に
`src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\`へ自動コピーされます。

## SpoutDX.dll

配布zip／インストーラーには`SpoutDX.dll`を同梱します。zip版の利用者が別途用意する必要はありません。

ソースからビルドする場合のみ、[Spout2](https://github.com/leadedge/Spout2)のSDKから
x64版`SpoutDX.dll`を用意し、このフォルダへ配置してください。DLLが無い場合は
Spout出力ボタンが無効になるだけで、動画再生やLTC同期は利用できます。

## 配置例

```text
timecode-sync-player/
└── native/
    ├── tcs_gstreamer.dll  # 必須（ソースビルド時）
    └── SpoutDX.dll        # Spout出力を使う場合
```

配置後は[セットアップ手順](../docs/SETUP.md)に従ってビルドしてください。
