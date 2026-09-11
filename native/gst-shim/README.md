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
# vendor/Spout2 (tag 2.007.017 / commit 固定) を取得（git 管理外）
powershell -File native/gst-shim/get-spout.ps1
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

## 検証状況（2026-09-11 実測）

機材: RTX 3070 / GStreamer 1.28.2 (MSVC x64) / Spout2 2.007.017 / Windows / Debug ビルド。

| 検証 | 結果 |
| --- | --- |
| tcs-shim-test (mp4 1080p60) | failures=0 を連続 5 回 |
| コンテナ/コーデック | mp4(h264) / mkv / avi / ts / hevc(mp4) すべて GPU 経路 (d3d11h264dec / d3d11h265dec) で failures=0 |
| 外部デバイス Adopt | リーステクスチャの `GetDevice()` が呼び出し側 `ID3D11Device*` と一致 |
| 切り替え反復 (--stress, 4 素材×120 回) | load 失敗 0 / フレーム 363。作業セットは warmup 後 ~160MB で飽和（非有界増加なし） |
| アプリ E2E (backend=Gstreamer) | ① 1080p60 GPU 経路の実フレームを別プロセス Spout 受信で確認（受信側の終了→再起動後も接続・フレームイベント継続、アプリは描画継続） ② 4 コンテナの next/prev 反復 7 ロード全成功・クラッシュなし ③ 再生中クローズで終了コード 0 |
| 既存単体テスト | 非 E2E 1200 件合格（mpv 既定経路の退行なし。E2E 含め全件は 1242 件） |
| 性能参考値 (720p60, 15s, Spout OFF) | mpv: CPU 73.1% (1コア換算) / WS 平均 251.5MB → GStreamer: 46.7% / 230.2MB。GPU util は 10–16% で同等（他プロセスの GPU 使用あり・参考値） |

未検証/制約:

- 長時間連続再生（数時間）と、受信側を殺した瞬間の送信継続の厳密な保証は未検証。
- Spout 受信の 2 個目プロセスは SDK のフレーム同期の都合でコピー画像が更新されない
  ことがある（proto 送信では再起動後の内容更新を実測済み。製品側は合成層が受信を担う）。
- WPF プレビューは暫定的に毎フレーム全解像度 CPU コピー（合成層接続までの制約）。
- video/x-hap は専用分岐の実装まで意図的に拒否。TCS_ALLOW_UNKNOWN=1 の decodebin
  フォールバックはデバッグ専用。

## 配布とセットアップ

- **GStreamer は同梱しない**。利用者側で公式 MSVC x64 ランタイム 1.28.2 を導入する
  （`GSTREAMER_1_0_ROOT_MSVC_X86_64` か既定 `C:\Program Files\gstreamer\1.0\msvc_x86_64`）。
- 配布物に含めるのは `tcs_gstreamer.dll`（自作 MIT + Spout2 BSD-2 を静的組み込み）のみ。
  ライセンス表記はリポジトリ直下の `THIRD-PARTY-NOTICES.md` を参照。
- 再現ビルド: `native/gst-shim/get-spout.ps1`（Spout2 をタグ 2.007.017 / コミット固定で取得）
  → `native/gst-shim/build-shim.ps1`。GStreamer SDK は上記ランタイムに同梱の SDK を使用。
- アプリの GStreamer バックエンドは設定 `"backend": 1`
  (`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`)。既定は 0 = mpv。

## mpv 互換アダプタとの関係

C# 側は既存の `IMpvApi` / `IMpvRenderApi` / `ISpoutOutput` の実装を
差し替えるバックエンド（既定は mpv のまま、設定で切替）として接続する。
アダプタはリース API を使った「フレーム公開ゲート」と、互換経路
（WriteableBitmap 用 CPU コピー＝暫定プレビュー経路、制約明記）を併せ持つ。
