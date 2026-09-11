# GStreamer -> D3D11 -> Spout2 試作 (tcs-gst-proto)

独立試作。本体とは分離して `filesrc -> qtdemux -> h264parse -> d3d11h264dec ->
d3d11colorconvert(BGRA) -> appsink(D3D11Memory) -> spoutDX::SendTexture` の
GPU 内完結経路を検証する。

## 依存と取得物 (バージョン固定)

| 物 | 版 | 取得元 / 場所 |
| --- | --- | --- |
| GStreamer Runtime+SDK (MSVC x64) | 1.28.2 | システム導入済み `C:\Program Files\gstreamer\1.0\msvc_x86_64` |
| Spout2 SDK (ソース) | tag 2.007.017, commit f49e2f4 | https://github.com/leadedge/Spout2 → `vendor/Spout2` (git 管理外) |
| gst-plugins-bad ソース (読み取り参照用) | 1.28.2 | https://gstreamer.freedesktop.org/src/gst-plugins-bad/ → `vendor/gst-src` |

`vendor/gst-src/.../gst-libs/gst/d3d11` の d3d11 ライブラリへリンクする
(gstd3d11-1.0)。GStreamer の d3d11 系ヘッダは 1.28 では **gst-plugins-bad** 側にあり、
ランタイム SDK のインクルードツリーに生成ヘッダ `gst/d3d11/gstd3d11config.h` が
同梱されない。本ディレクトリの `include/gst/d3d11/gstd3d11config.h` がそのスタブ
(公式ビルドで無効の 2 マクロを undefined のまま置くだけ)。

## ビルド

```powershell
powershell -File native/gst-shim/proto/build-proto.ps1 -Config Debug
# 出力: native/gst-shim/proto/build-debug/tcs-gst-proto.exe
```

要件: VS Build Tools (vcvars64 経由で cl/ninja を発見)、CMake、上記 GStreamer SDK。

## 実行

```powershell
$env:PATH = "C:\Program Files\gstreamer\1.0\msvc_x86_64\bin;" + $env:PATH
# 送信 (12 秒・5 秒目にシーク)
tcs-gst-proto.exe send <file.mp4> <sec> [senderName] [seek_at_sec]
# 受信 (別プロセス。受けたフレームを BMP 保存しハッシュで変化を確認)
tcs-gst-proto.exe recv [senderName] <sec> <bmp_prefix>
```

テスト素材は artifacts/media/ に生成 (git 管理外):

```powershell
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60000/1001:duration=12 `
  -c:v libx264 -profile:v high -pix_fmt yuv420p -crf 18 artifacts/media/test_1080p60.mp4
```

## 検証結果 (2026-09-11, RTX 3070, GStreamer 1.28.2, Debug ビルド)

- appsink に届く全サンプルが `gst_is_d3d11_memory() == TRUE`、
  `ID3D11Texture2D` の `GetDevice()` が **試作が作った ID3D11Device と一致**
  (GStreamer 側へ `gst_d3d11_device_new_wrapped` +
  `GST_D3D11_DEVICE_HANDLE_CONTEXT_TYPE` context で注入)。
  出力は DXGI_FORMAT 87 (B8G8R8A8_UNORM)、ArraySize=1、subresource 0。
- `spoutDX::SendTexture` は内部 `CopyResource`(GPU 内コピー)+Flush+フレーム通知。
  **appsink→Spout 間で CPU 読み戻しなし** (CPU map は診断用に 1 回行うが経路上不要)。
- 1080p60 を sync 再生で **平均 58.8〜60.5 fps 継続**、送信 706/706 回成功。
  非同期 (sync=false) では 720 フレームを約 2 秒で解码しきった (GPU 余力大)。
- 別プロセス受信で 1920x1080 BGRA フレームが継続到達。BMP 化した画素は
  全サンプル非ゼロ・フレーム間でハッシュ変化 (映像内容の実在を確認)。
- FLUSH+ACCURATE シーク: 発行後最初のフレーム pts=3.033s (意図 3.0s、正確)。
- EOS 検出・状態 NULL 遷移後のクリーン終了を確認。
- 既知: この試作は h264 固定パイプライン。HEVC 素材は "Internal data stream
  error" で失敗 (h265 用 parse/dec の選択が未実装のため。統合版で codec 選択する)。

## 次の段階への申し送り

- qtdemux の src パッドは Sometimes 要求パッド (`video_%u`)。`pad-added` で
  受けて `gst_pad_link_full(..., CHECK_NOTHING)` する必要がある。
- デコード出力テクスチャはプール由来で寿命がバッファ参照に紐づく。SendTexture の
  CopyResource が終るまで sample を unreff しないこと (単一 device/context 上は
  コマンドが投入順で実行されるため、追加のフェンス待ちなしで順序は保たれる。
  複数コンテキスト化・deferred 化する場合の再検討が必要)。
- WPF プレビュー用に CPU ピクセルが必要なときは、本経路とは別に縮小後の
  `d3d11download` 分岐を作る (フル解像度の常時読み戻しを避ける)。
