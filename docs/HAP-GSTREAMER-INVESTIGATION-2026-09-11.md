# HAP を GStreamer で扱えるかの調査（2026-09-11）

読み取り調査と、ローカルの GStreamer 1.28.2（MSVC x64、`C:\Program Files\gstreamer\1.0\msvc_x86_64`）での短い動作確認。本体・試作のコード変更はしていない。テスト素材は ImageMagick 同梱の ffmpeg（`--enable-libsnappy`）で生成し、`C:\Users\codea\AppData\Local\Temp\happrobe\` に置いた。

## 結論

1. **HAP のファイルは標準の GStreamer で読める（デマルチプレクスとCPUデコード）。** qtdemux が `video/x-hap, variant=Hap1|HapY|…` を出し、gst-libav の `avdec_hap` が `video/x-raw, format=RGBx` を返す。GStreamer 1.26 で qtdemux と libav に Hap のマッピングが追加された（[1.26 release notes](https://gstreamer.freedesktop.org/releases/1.26/)）。
2. **ただし標準要素では GPU 圧縮テクスチャのまま扱えない。** `avdec_hap` は FFmpeg の hap デコーダーで、DXT を CPU で展開して RGBA/RGBx を返す（[FFmpeg texturedsp](http://www.ffmpeg.org/doxygen/3.4/texturedsp_8c.html)、[avdec_hap](https://gstreamer.freedesktop.org/documentation/libav/avdec_hap.html)）。この経路では 4K BGRA 33MB/フレームのアップロードが残り、HAP の利点（GPU 直接参照、帯域 1/4〜1/8）が消える。`video/x-hap` を受ける d3d11 要素は存在しない（ローカルの d3d11 プラグインは h264/h265/av1/vp9/mpeg2 デコーダーと convert/upload/download のみ）。
3. **GPU 直接経路は「qtdemux → appsink（`video/x-hap`）→ 自前で Snappy 展開 → BC テクスチャへアップロード」で実現できる。** GStreamer をデマルチプレクス・時計・シークに使い、フレームの展開とアップロードだけを自前（C# または小さなネイティブ）で行う構成。Hap の各セクションの仕様は公開されている（[HapVideoDRAFT.md](https://github.com/Vidvox/hap/blob/master/documentation/HapVideoDRAFT.md)）。Hap1=BC1、Hap5=BC3、HapY=Scaled YCoCg BC3（シェーダーで YCoCg→RGB）、HapM=HapY＋BC4 alpha、HapA=BC4、Hap7=BC7、HapH=BC6H。D3D11 はこれらの BC 形式をそのままサンプリングできる。
4. **GStreamer の d3d11 デコーダー経路（H.264/HEVC）と HAP 経路は別物。** 進行中の GStreamer 移行が D3D11VA を前提にしているなら、HAP は「ソース種別の追加」として後から足す形になる。qtdemux 以降だけが異なる。

## 実測（1080p 120フレーム／4K 60フレーム、testsrc2、CPU デコードのみ、fakesink sync=false）

| 内容 | 値 |
| --- | --- |
| qtdemux 出力 caps | `video/x-hap, variant=(string)Hap1` ／ `variant=(string)HapY`、width/height/framerate 付き |
| avdec_hap 出力 caps | `video/x-raw, format=RGBx`（Hap1・HapQ とも） |
| 1080p Hap1 デコード（120f） | プロセス全体 245ms、demux のみ 55ms → 約 1.6ms/フレーム |
| 1080p HapQ デコード（120f） | 264ms → 約 1.75ms/フレーム |
| 4K HapQ デコード（60f） | 598ms、demux のみ 74ms → 約 8.7ms/フレーム |
| 素材サイズ | 1080p Hap1 11.5MB/120f、HapQ 22.3MB/120f、4K HapQ 42.2MB/60f（testsrc2 は圧縮が効きやすく、実素材はこれより大きい） |

- 4K HapQ の CPU 展開 8.7ms/フレームは、単体では 16.7ms に収まるが、その後の 33MB アップロードと合成を同じ予算内に置くと余裕がない。CPU 展開経路で 4K60 を狙うなら、展開と アップロードを別スレッドで重ねる必要がある。
- GPU 直接経路なら CPU 側は Snappy 展開だけ（BC3 4K で約 8.3MB → Snappy 後は素材次第で数 MB）で、アップロード量も BGRA の 1/4。

## GStreamer 移行との関係

- 進行中の移行プロンプトは D3D11VA デコード→D3D11 色変換→Spout の経路で、HAP は範囲に入っていない。qtdemux が `video/x-hap` を出すので、移行側のパイプラインに「`video/x-hap` のときは appsink で圧縮フレームを受け取り、自前で BC テクスチャ化する」分岐を追加すれば統合できる。デコードデバイスを合成側と共有する方針はそのまま使える。
- 時計・シーク・EOS の扱いは GStreamer 側に任せられる（appsink はバッファの PTS を持つ）。LTC ジャンプの扱いは移行側の設計と同じ。
- ライセンス: gst-libav（`avdec_hap`）は LGPL、FFmpeg の hap デコーダーは Snappy 依存。自前の Snappy 展開は pure C# 実装（例: Snappier、MIT）で済み、GPL/nonfree を持ち込まない。

## 次にやるなら

1. 試作（scripts/GpuOutputProbe）に `--source hap-file` を追加: qtdemux→appsink で `video/x-hap` を受け取り、Snappy 展開→BC1/BC3 テクスチャ（`D3D11_USAGE_DEFAULT`、`UpdateSubresource` または staging）→合成へ。HapY は YCoCg 変換シェーダー。4K60 の展開時間・アップロード時間・合成 60Hz 維持を測る。これで「HAP なら GPU 直接で 4K60 が成立するか」が数値で出る。
2. 実素材（実写・ノイズの多い映像）で Snappy 後のサイズと NVMe の読み出し帯域を確認する（testsrc2 は圧縮が効きすぎる）。
3. GStreamer 移行側と、`video/x-hap` 分岐の置き場所（同じパイプラインか、別ソース種別か）を合わせる。

## 手順の記録

```powershell
# 素材生成（ImageMagick 同梱 ffmpeg、libsnappy あり）
ffmpeg -f lavfi -i "testsrc2=size=1920x1080:rate=60" -frames:v 120 -c:v hap -format hap   hap1_1080p.mov
ffmpeg -f lavfi -i "testsrc2=size=1920x1080:rate=60" -frames:v 120 -c:v hap -format hap_q hapq_1080p.mov
ffmpeg -f lavfi -i "testsrc2=size=3840x2160:rate=60" -frames:v 60  -c:v hap -format hap_q hapq_4k.mov
# caps 確認と CPU デコード時間（gst-launch-1.0 1.28.2）
gst-launch-1.0 -v filesrc location=C:/Users/codea/AppData/Local/Temp/happrobe/hap1_1080p.mov ! qtdemux ! fakesink
gst-launch-1.0 -v filesrc location=... ! qtdemux ! avdec_hap ! fakesink
Measure-Command { gst-launch-1.0 -q filesrc location=... ! qtdemux ! avdec_hap ! fakesink sync=false }
```

注意: gst-launch の `location=` にセッションの長い一時ディレクトリ（`C--Users-…` を含むパス）を渡すと "Resource not found" になったため、短いパスへコピーして実行した。ローカルの gst-libav には `avenc_hap` はない（エンコードは ffmpeg で行った）。
