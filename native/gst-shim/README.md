# tcs_gstreamer — GPU フレームソース shim (v3)

GStreamer によるデコードを「合成層へ GPU 画像を供給するソース」として
公開するための C ABI ネイティブ DLL。出力契約の対応は
`docs/OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md`（合成層側設計・他作業系統の
文書を読み取り参照）に合わせる。

## 出力側の位置づけ（確定契約との対応）

- この shim の出口は **Spout ではない**。出口は `tcs_player_acquire()` の
  **リース API**（D3D11 テクスチャ = BGRA。将来 NV12 直出しも可）。
  全画面・Spout・プレビューは合成後の画像を合成層が各出力へ配る。
- `tcs_player_publish_spout()` / `tcs_player_send_image()` /
  proto の Spout 経路は **検証専用**（受信箱の実在確認用）。
- **同一デバイス前提**: `tcs_player_create(sender, external_device, ...)` は
  呼び出し側（合成層）の `ID3D11Device` を Adopt して `GstD3D11Device` に
  ラップし、d3d11 デコーダ・変換・出力をすべてそのデバイス上で動かす。
  同一デバイス内なので共有ハンドルや keyed mutex は不要。
  （別デバイス化が必要な場合の理由と同期方法は未採用。採用時は報告。）

## ソース契約（命令 3 対応）

| 契約 | 実装 |
| --- | --- |
| 素材・シーク世代 | `generation` を player が保持。load / 手動 seek / step 時に +1 して戻り値で通知。オーナーは `tcs_player_set_generation()` で管理も可。フレームには世代・seq・pts が刻まれる |
| 古い世代を渡さない | `tcs_player_acquire(gen)` は現 `latest` の世代一致時のみリース化。不一致は 0（=なし）を返す。黒やエラー画像は作らない（保持は合成層の責務） |
| リース返却まで再利用しない | acquire は GstSample 参照をそのまま所持。GStreamer のデコーダ/コンバータのプール（有限）は参照解放までそのテクスチャを再使用しない。`release()` で返却 |
| GPU 完了順序 | 単一 immediate context + MultithreadProtected による投入順保証に依存（同一デバイス・同一コンテキスト）。合成層が読者へ渡す際の共有フェンスは合成側の契約（docs 対応表「共有フェンス」行）。**未実証**: デコーダ→コンバータ間の内部フェンスを跨ぐ順序保証は、実測での破綻未確認のため要追試（既知問題に記録） |

## コーデック分岐（命令 4 対応）

decodebin に映像を任せない（decodebin は d3d11 pad を sysmem へ
ダウンロードしてから公開するため、GPU 経路が成立しない）。
`typefind → 既定 demux 表 → pad caps で分類 → 明示チェーン`。

| stream caps | GPU チェーン | CPU フォールバック |
| --- | --- | --- |
| video/x-h264 | h264parse ! d3d11h264dec ! d3d11colorconvert ! BGRA(d3d11mem) | avdec_h264 ! videoconvert ! BGRA |
| video/x-h265 / x-hevc | hevcparse ! d3d11h265dec ! ... | avdec_h265 |
| video/x-vp9 | vp9parse ! d3d11vp9dec ! ... | avdec_vp9 |
| video/x-av1 | av1parse ! d3d11av1dec ! ... | dav1ddec |
| video/x-raw | videoconvert ! BGRA | 同左 |
| **video/x-hap** | **拒否**（圧縮テクスチャ直受けの専用分岐を追加する予定。avdec_hap による自動展開をさせない。HAP 自体は今回の範囲外） | — |
| 未知 | 拒否（`TCS_ALLOW_UNKNOWN=1` のときだけ decodebin 照合用フォールバック） | — |

コンテナ: mp4/mov/m4v/3gp→qtdemux、mkv→matroskademux、ts→tsdemux、
mxf→mxfdemux、avi→avimux、raw ES→直接チェーン、他→未知扱い（同上）。

音声は decodebin（CPU、この経路に GPU 要件なし）+ volume +
audioconvert + autoaudiosink。

## ビルド・検証

```powershell
# vendor/Spout2 (tag 2.007.017) が必要（proto/PROTO.md 参照・git 管理外）
powershell -File native/gst-shim/build-shim.ps1 -Config Debug
# 出力: build-debug/tcs_gstreamer.dll, build-debug/tcs-shim-test.exe

$env:PATH = "C:\Program Files\gstreamer\1.0\msvc_x86_64\bin;" + $env:PATH
native\gst-shim\build-debug\tcs-shim-test.exe artifacts\media\test_1080p60.mp4 2
```

tcs-shim-test は C ABI のみで以下を検証する:
外部デバイス Adopt（リーステクスチャの GetDevice==呼び出し側デバイス）、
load/pause/再生進行/lease/返却、世代不一致 acquire==0、seek 世代 +1、
step、再ロード、stop 後のクリーン状態、Spout 検証 publish（GPU テクスチャ）。

Spout 受信側の目視検証は proto の recv モード
（`native/gst-shim/proto/build-debug/tcs-gst-proto.exe recv <sender>`）が使える。

## 検証状況

（この節はテスト実施後に更新する。未実施 = 空。）

## mpv 互換アダプタとの関係

C# 側は既存の `IMpvApi` / `IMpvRenderApi` / `ISpoutOutput` の実装を
差し替えるバックエンド（既定は mpv のまま、設定で切替）として接続する。
アダプタはリース API を使った「フレーム公開ゲート」と、互換経路
（WriteableBitmap 用 CPU コピー＝暫定プレビュー経路、制約明記）を併せ持つ。
