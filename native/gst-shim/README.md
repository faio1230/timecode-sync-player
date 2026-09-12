# tcs_gstreamer — GPU フレームソース shim（段階 6b）

GStreamer によるデコードを「合成層へ GPU 画像を供給するソース」として
公開するための C ABI ネイティブ DLL。出力契約の対応は
`docs/OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md`（合成層側設計・他作業系統の
文書を読み取り参照）に合わせる。

## 出力側の位置づけ（確定契約との対応）

- この shim の出口は **Spout ではない**。出口は `tcs_player_acquire()` の
  **リース API**（D3D11 テクスチャ = BGRA）。全画面・Spout・プレビューは
  合成後の画像を合成層が各出力へ配る。
- `tcs_player_publish_spout()` / `tcs_player_send_image()` /
  proto の Spout 経路は **検証専用**（受信箱の実在確認用）。
- **別デバイス + 共有リング（段階 6b）**: `tcs_player_create(sender,
  external_device, ...)` は external device を **Adopt しない**。`IDXGIDevice`
  からアダプター LUID を読むだけで、同じ LUID 上に **shim 自身の
  `ID3D11Device` + immediate context** を作り（`ID3D11Multithread` の
  `SetMultithreadProtected(TRUE)` を明示）、d3d11 デコーダ・変換・
  `GstD3D11Device` はすべてその自前デバイスで動かす。合成側の device/context
  には一切触れない。external=NULL のときは既定アダプターに自前デバイスを作る。
  受け渡しは **NT 共有ハンドルのリング（BGRA・3 枚）+ 共有フェンス（値＝`seq`）**
  で行い、同一 LUID・同一解像度なので追加のコピー無しで合成側が開ける。

## ソース契約

| 契約 | 実装 |
| --- | --- |
| 素材・シーク世代 | `generation` を player が保持。load / 手動 seek / step 時に +1 して戻り値で通知。オーナーは `tcs_player_set_generation()` で管理も可。フレームには世代・seq・pts が刻まれる |
| 古い世代を渡さない | `tcs_player_acquire(gen)` は現世代のフレームだけを返す。先頭の別世代フレームは破棄し、一致するものが無ければ 0（=なし）または `TCS_ERR_ENDED`。黒やエラー画像は作らない（保持は合成層の責務） |
| リース返却まで再利用しない | acquire は GstSample 参照をそのまま所持。GStreamer のデコーダ/コンバータのプール（有限）は参照解放までそのテクスチャを再使用しない。`release()` で返却 |
| GPU 完了順序 | 到着時に shim の context でリングへ `CopyResource` し、共有フェンスへ `seq` を Signal して `Flush` する（完了待ちはしない）。合成側は描画前に `ID3D11DeviceContext4::Wait(fence, seq)` を自分の GPU キューへ積む。CPU 側の受け渡しはリースで保護し、slot は release まで上書きしない |

## リース API のセマンティクス

合成層から見た各 API の規則。対応する契約仕様は session-refactor 側
`docs/GPU-SOURCE-CONTRACT-SPEC.md`（規則 1〜7 と「GStreamer／HAP 実装が満たす条件」、
読み取り参照）。一致しない点は末尾に理由付きで列挙する。

### `tcs_player_acquire(player, generation, out info)` → 1 = リース成立 / 0 = なし / -6 = Ended

- **現在世代のフレームだけ**を返す。load / seek / step は世代を進め、seek はキューを破棄するため、
  旧世代のデコード済みフレームは返らない。
- **滞留を有界にして返す**（問題 H/H-2/H-3 修正、2026-09-11）: 未配信数を n とし、純関数
  `tcs_delivery_plan`（`include/tcs_delivery_policy.h`）で返し方を決める。
  - n=0: 0（なし）。EOS 済みなら `TCS_ERR_ENDED`。
  - n≤2: 最古を返す（1 tick に 2 到着しても滞留は最大 1 枚）。n=2 で最古の到着から
    21ms（60fps の 1.25 フレーム）以上経過したら最古を 1 枚破棄してから返す（H-3）。
  - n>2: 最古の n−2 枚を破棄してから残り 2 枚の古い方を返す（合成停止後は 1 tick で最新へ追い付く）。
  - 破棄は `TcsDeliveryStats.latest_replaced` と配信トレースの flags bit0 に記録する。
    判断は shim 単体テスト（`--policy-only`、12 ケース）で固定する。
- 準備できないときは 0（=なし）を返す。**黒・前フレーム・エラー画像で代用しない**
  （保持は合成層の責務）。**非ブロッキング**で、デコードを待たない（内部 `frame_lock` を
  短時間取るだけ）。
- **Ended**: 現世代の未配信が無く、EOS に到達済みなら `TCS_ERR_ENDED`（-6）。合成層は
  `SourceStatus.Ended` として受け取り、保持（直前画像の表示）は合成層が行う。
- リース保持中の再呼び出し: 同じ世代なら同じリースを返す（新フレームは渡さない）。
  別世代を要求した場合は 0 で、新しいフレームを取るには先に `release` が必要。
- `info` = generation / seq / pts_ns / width / height / is_gpu / **slot**。seq は
  shim 内の単調増加番号（プロセス内識別用）、pts_ns はサンプル PTS（無ければ
  セグメント位置）。slot は **0..2 = 共有リングの面**、**-1 = 旧サンプルリース
  経路**（解像度/形式不一致など。CPU デコードも `d3d11upload` を通すため通常は
  slot >= 0）。slot >= 0 のフレームは到着時に `CopyResource` 済みで、共有フェンスに
  `seq` が Signal されている。

### `tcs_player_leased_texture(player, out texture, out subresource, out dxgi_format)` → 0 = 成功

- リース保持中のみ有効。`ID3D11Texture2D*` を返す（**shim のデバイス上**。
  段階 6b 以降、合成側デバイスとは別）。
- `dxgi_format` は **87 = B8G8R8A8_UNORM (BGRA)**。NV12 は現状返さない（相違点参照）。
- `subresource` は **0**。デコーダ配列テクスチャ等は shim 所有の単一サブリソース
  テクスチャへ平坦化してから返す（pool 由来テクスチャのポインタはリース中のみ有効、
  平坦化テクスチャは player と同時に破棄）。
- 返したポインタは **借用**（AddRef しない）。release 後・destroy 後に使用してはならない。
- 共有リング経路の合成側はこの API を使わず `tcs_player_ring_info` の NT ハンドルを
  `OpenSharedResource1` する（per-frame の Surface を作らない）。

### `tcs_player_ring_info(player, out_handles, capacity, out_count, out_fence, out_width, out_height)` → 0 = 成功

- 段階 6b の共有リング。最初の GPU sample 到着時に一度だけ作成される。
- `out_handles` は BGRA テクスチャ 3 枚の **NT 共有ハンドル**、`out_fence` は
  共有フェンスの NT ハンドル。**すべて shim 所有**で `CloseHandle` 禁止。
  `tcs_player_destroy` まで有効。
- 合成側は `ID3D11Device1::OpenSharedResource1` と
  `ID3D11Device5::OpenSharedFence` で一度だけ開き、acquire の `seq` を
  `ID3D11DeviceContext4::Wait(fence, seq)` で **GPU キュー待ち**してから描く
  （CPU はポーリングしない）。
- リング未作成（load 前）は `TCS_ERR_NO_FRAME`、`capacity < 3` は
  `TCS_ERR_SIZE`。解像度**または形式**がリングと一致しない GPU フレームは旧サンプル
  経路（slot=-1）へ落ちる。

### `tcs_player_leased_cpu_copy(player, dst, dst_stride)` → 0 = 成功

- リース中のフレームを CPU BGRA へ行コピーする。staging + Map の **GPU 読み戻し**を
  含むためプレビュー/デバッグ専用。Spout へは `tcs_player_publish_spout`
  （GPU テクスチャ送信、検証層）を使う。

### `tcs_player_release(player)`

- リースを返却し、プールのテクスチャを再利用可能にする。リースが無ければ no-op（冪等）。
- **リース中テクスチャへの書き込み（上書き）は行わない**。返却は合成層が GPU 使用
  完了を確認してから（合成側 LatestPool と同じ規則）。

### `tcs_player_set_generation` / `tcs_player_get_generation`

- 世代の現在値はオーナー（合成層/アプリ）が決める。load / seek / step は自動で +1。
  LTC ジャンプ等を独自世代で表現したい場合は set で上書きでき、以後 acquire は
  その世代一致のみを返す。get は現在値（診断用）。

### フレーム通知コールバック（`tcs_player_set_frame_callback`）

- 新フレーム到着時に **GStreamer のストリーミングスレッド**から
  `fn(user_data, generation, seq)` が呼ばれる。データ転送は無い「起きて確認せよ」の合図。
- コールバック内で acquire しない（合成スレッドで行う）。短時間で戻ること。
- 登録解除は destroy 前（fn=NULL）。コールバック時点の世代を渡すので、合成側で
  現在世代と比較して古ければ無視する。

### 保持枚数と破棄規則

- 未配信キューは容量 4。GPU 経路は最大 3 アイテムが共有リング slot（3 枚のいずれか）を
  占有し、acquire のたびに配信規則（H-3）で滞留を最大 2（通常 1）へ抑える。
  加えて appsink 内部キューが最大 4（`max-buffers=4, drop=FALSE`）。
- 新フレーム到着時に空き slot が無ければ **最古の未配信 GPU アイテムを追い出して**
  その slot を使う（`latest_replaced`、配信トレース flags bit0）。`leased` の slot は
  決して置換・上書きしない。
- リース中に新フレームが来ても acquire は 0（release 後に未配信キュー先頭へ進む）。
- プール実体は GStreamer のデコーダ/コンバータのバッファプール（有限・可変）。
  リースを長時間保持した場合はプール拡張 → appsink キュー → バックプレッシャの順で
  対応し、acquire 自体はブロックしない。

### デバイスと同期（段階 6b）

- shim は external device を Adopt せず、同じアダプター LUID に自前の
  `ID3D11Device` + immediate context を作る（MultithreadProtected を明示）。
- GPU フレームは到着時に shim の context でリングへ `CopyResource`（配列テクスチャは
  box copy）し、続けて共有フェンスへ `seq` を Signal、`Flush` で投入する。
  完了待ちはしない（I5）。
- 合成側はリング slot の surface を一度だけ開き、描画前に
  `Context4::Wait(fence, seq)` を GPU キューへ積む（CPU は待たない）。slot は
  CPU lease で保護され、合成が release するまで shim はその slot へ書かない。
- `leased_cpu_copy` / 検証 Spout 送信はリング経路でも動く（shim デバイス上のリング
  テクスチャを読み戻し/送信する）。外部プロセスへは Spout（spoutDX の共有）を使う。
- D3D11.4（`ID3D11Device5` / `ID3D11DeviceContext4`）が無い環境ではリングを作らず、
  GPU フレームは従来のサンプルリース経路（slot=-1）へフォールバックする。

### 契約仕様（規則 1〜7）との対応と相違

| 仕様 | shim | 備考 |
| --- | --- | --- |
| 規則1 世代排除 | 一致 | seek/load/step で未配信キューを破棄。acquire(gen) は一致時のみ |
| 規則2 位置に基づく最新優先 | **部分一致** | position は受け取らず「現世代の未配信を、H-3 の有界規則で 1 枚」。位置選択は合成層（pts_ns 参照） |
| 規則3 なしを返す | 一致 | 0 / `TCS_ERR_ENDED`。黒・前画像なし |
| 規則4 lease 寿命 | **部分一致** | プールは GStreamer 側（有限・可変）。未配信キューは容量 4、GPU 経路はリング slot 3 枚を占有。Begin/CompleteGpuUse 相当は無く release のみ（冪等） |
| 規則5 デバイス | 一致（別デバイス + 共有リング） | 合成デバイスは LUID のみ参照。NT 共有ハンドル 3 枚 + 共有フェンス（値=seq）で受け渡し |
| 規則6 非ブロッキング | 一致 | acquire/set_generation は短いロックのみ。デコードを待たない |
| 規則7 診断 | **部分一致** | decoder/gpu_path/frames/generation はあり。世代排除・NotReady・置換・最大同時 lease のカウンタは未実装 |
| 条件: NV12 テクスチャ | **相違** | 現状 BGRA のみ（d3d11colorconvert）。NV12 直出しは converter 差し替えで可能だが未実装 |
| 条件: HAP/BC テクスチャ | 未実装 | 専用分岐まで意図的に拒否 |
| 条件: 時計 | 一致 | pts_ns を返すのみで GStreamer の running time は露出しない。世代は set_generation/seek で合成層が制御 |
| リング容量 3 以上のプール | 一致 | BGRA 3 スロット + 共有フェンス。寸法/形式不一致時のみ旧経路へフォールバック |

相違はいずれも「最小 ABI で現段階の接続を成立させる」ためのスコープ判断であり、
NV12/BC と診断の拡張は今後の課題。

## 本体 OutputEngine の GStreamerSource（段階 2〜6b、2026-09-12）

本体側 `src/TimecodeSyncPlayer/Output/GStreamerSource.cs` は
`tcs_player_acquire` / `tcs_player_ring_info` / `tcs_player_release` を
`IVideoSource`（docs/GPU-SOURCE-CONTRACT-SPEC.md）へ適合させる薄いアダプター。
管理テスト（`tests/TimecodeSyncPlayer.Tests/GStreamerSourceTests.cs`）は fake で契約規則を検証する。

shim と契約の差はアダプター側で次のように吸収する（契約テストで固定）:

- **位置選択**: shim は position を受け取らず現世代の未配信を H-3 の有界規則で
  1 枚返すため、返却画像の `pts_ns` を `SourceImageStamp.PositionSeconds` として扱う。
  過去行列は持たない。
- **リース**: shim は「リース保持中の acquire は同じ画像を返す」ため、参照カウント付きの
  共有リースとして同一 Stamp を返す。最後の参照が返ると `release` を 1 回だけ呼ぶ。
- **Ended**: `TCS_ERR_ENDED` を `SourceStatus.Ended` として返す。表示の保持は合成層の責務。
- **形式**: `dxgi_format != 87`（BGRA 以外）は `NotReady` として拒否する。
- **世代**: shim 側の値を観測して対応付け（load/seek の自動 +1 をトレース
  `gst.generation:` に記録）。世代変更時は合成層の Held を手放し、古い世代を返さない。

本体配線（段階 6 / 6b、2026-09-11）:

- `PlayerBackend=Gstreamer` かつ `OutputBackend=Gpu` のとき、`OutputEngine` の起動直後に
  `GstBackendState.SetExternalDevice()` で合成デバイスのポインタを渡す。
  **段階 6b 以降 `tcs_player_create` はこれを Adopt せず、アダプター LUID の読み取りに
  のみ使う**（合成デバイス/context は shim から触らない）。`GstMpvRenderApiAdapter` の
  `LeasedCpuCopy` と `GstSpoutOutput` の直接送信はこの組み合わせでは使わない
  （`RenderSession.SuppressFrameSnapshots`）。既定の mpv 経路と GStreamer+Cpu 経路は不変。
- `GStreamerSource` は slot >= 0 のリースで `tcs_player_ring_info` の 3 枚を
  `OpenSharedResource1` / 共有フェンスを `OpenSharedFence` で一度だけ開いて保持する。
  `OutputEngine` は新規 `seq` に対してだけ `Context4.Wait(fence, seq)` を発行する。
  per-frame の Surface は作らない。slot < 0（旧サンプル経路）は従来どおり
  AddRef 付き per-lease Surface を使う。
- R-1: リングのコピー完了（`ID3D11Fence.CompletedValue >= seq`）を CPU 側で確認できない間は
  その tick の取得を見送り、直前の Held を描く（完了待ちを合成 tick の GPU フェンスへ
  含めない）。
- shim は「リース保持中は同じ画像を返す」ため、リースは毎合成 tick 返却する。
  リング slot は CPU lease が保護する（release まで shim は書かない）。
- shim の immediate context（自前デバイス）は GStreamer のストリーミングスレッドが
  使い、合成側 GPU worker は自前デバイスの context だけを操作する。両者はデバイスが
  別なので共有しない。リング上の受け渡しは共有フェンスで同期する。

## コーデック分岐

decodebin に映像を任せない（decodebin は d3d11 pad を sysmem へ
ダウンロードしてから公開するため、GPU 経路が成立しない）。
`typefind → 既定 demux 表 → pad caps で分類 → 明示チェーン`。

CPU フォールバックの最後は全プロファイル共通で `d3d11upload` を通し、
BGRA(D3D11Memory) として共有リング（slot 0..2）へコピーする。合成側は
CPU デコード素材も GPU 素材と同じリース経路で受け取る（変更不要）。

| stream caps | GPU チェーン | CPU フォールバック |
| --- | --- | --- |
| video/x-h264 | h264parse ! d3d11h264dec ! d3d11colorconvert ! BGRA(d3d11mem) | avdec_h264 ! videoconvert ! d3d11upload ! BGRA(d3d11mem) |
| video/x-h265 / x-hevc | hevcparse ! d3d11h265dec ! ... | avdec_h265 ! （同上） |
| video/x-vp9 | vp9parse ! d3d11vp9dec ! ... | avdec_vp9 ! （同上） |
| video/x-av1 | av1parse ! d3d11av1dec ! ... | dav1ddec ! （同上） |
| video/x-prores | —（GPU デコーダなし） | avdec_prores ! videoconvert ! d3d11upload ! BGRA(d3d11mem) |
| video/x-raw | videoconvert ! d3d11upload ! BGRA(d3d11mem) | 同左 |
| **video/x-hap** | **拒否**（圧縮テクスチャ直受けの専用分岐を追加する予定。avdec_hap による自動展開をさせない。HAP 自体は今回の範囲外） | — |
| 一致しない video/* | — | 最終退避 `decodebin(sysmem)` → videoconvert ! d3d11upload（CPU デコード） |

コンテナ: mp4/mov/m4v/3gp→qtdemux、mkv→matroskademux、ts→tsdemux、
mxf→mxfdemux、avi→avidemux、raw ES→直接チェーン、他→decodebin。

音声（CPU、GPU 要件なし）は初回 audio pad で
`decodebin → audioconvert → queue → volume → audioconvert → autoaudiosink` を
構築して即リンクする。先頭の audioconvert はデコーダ出力の
non-interleaved F32LE（volume が拒否する）を受け止めるためのもので、
欠けると qtdemux が not-negotiated で全体を失敗させる（S1）。
autoaudiosink がデバイスを開けない環境では `fakesink sync=true` へ退避する
（`TCS_FAKE_AUDIO=1` で強制）。paused ロードでは音声シンクが最初のバッファを
消費するまで待ってから PAUSED へ落とす（初期化途中の停止で wasapi2 が
復帰しなくなるため）。

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
別デバイス生成（リーステクスチャの `GetDevice()!=呼び出し側デバイス` かつ
アダプター LUID が一致）、リング（`acquire` の slot 0..2、`ring_info` が
ハンドル 3 枚 + フェンス + 寸法を返す）、load/pause/再生進行/lease/返却、
世代不一致 acquire==0、seek 世代 +1、step、再ロード、stop 後のクリーン状態、
Spout 検証 publish（GPU テクスチャ）。

`--policy-only` はメディア無しで配信規則（H-3、12 ケース）とリング slot 割当・
追い出し規則（10 ケース）だけを固定する。

Spout 受信側の目視検証は proto の recv モード
（`native/gst-shim/proto/build-debug/tcs-gst-proto.exe recv <sender>`）が使える。

## 検証状況（2026-09-11 実測）

機材: RTX 3070 / GStreamer 1.28.2 (MSVC x64) / Spout2 2.007.017 / Windows / Debug ビルド。

| 検証 | 結果 |
| --- | --- |
| tcs-shim-test (mp4 1080p60) | failures=0 を連続 5 回 |
| コンテナ/コーデック | mp4(h264) / mkv / avi / ts / hevc(mp4) すべて GPU 経路 (d3d11h264dec / d3d11h265dec) で failures=0 |
| 別デバイス + 共有リング（段階 6b） | リーステクスチャの `GetDevice()` が呼び出し側 `ID3D11Device*` と一致せず、アダプター LUID は一致。`acquire` の slot 0..2 と `ring_info`（ハンドル 3 枚 + フェンス + 寸法）を確認 |
| 切り替え反復 (--stress, 4 素材×120 回) | load 失敗 0 / フレーム 363。作業セットは warmup 後 ~160MB で飽和（非有界増加なし） |
| アプリ E2E (backend=Gstreamer) | ① 1080p60 GPU 経路の実フレームを別プロセス Spout 受信で確認（受信側の終了→再起動後も接続・フレームイベント継続、アプリは描画継続） ② 4 コンテナの next/prev 反復 7 ロード全成功・クラッシュなし ③ 再生中クローズで終了コード 0 |
| 既存単体テスト | 非 E2E 1200 件合格（mpv 既定経路の退行なし。E2E 含め全件は 1242 件） |
| S1/S2 追試（2026-09-12、V1 素材） | H.264+AAC（`v1_h264_1080p60_aac.mp4`）: decoder=d3d11h264dec・60/秒・failures=0（autoaudiosink／`TCS_FAKE_AUDIO=1` の両方）。ProRes 422（`v1_prores422_1080p60.mov`）: decoder=avdec_prores・60/秒・failures=0。回帰 8 素材は decoder と配信レートが既存値のまま（TS の seek/step 2 件はベースラインでも失敗する既知項目） |
| 性能参考値 (720p60, 15s, Spout OFF) | mpv: CPU 73.1% (1コア換算) / WS 平均 251.5MB → GStreamer: 46.7% / 230.2MB。GPU util は 10–16% で同等（他プロセスの GPU 使用あり・参考値） |

未検証/制約:

- 長時間連続再生（数時間）と、受信側を殺した瞬間の送信継続の厳密な保証は未検証。
- Spout 受信の 2 個目プロセスは SDK のフレーム同期の都合でコピー画像が更新されない
  ことがある（proto 送信では再起動後の内容更新を実測済み。製品側は合成層が受信を担う）。
- video/x-hap は専用分岐の実装まで意図的に拒否。一致しない video/* は
  `decodebin(sysmem)` → `d3d11upload` の最終退避で CPU デコードする
  （`.mov` の HAP はプロファイル照合時の caps 検査で拒否）。
- `OutputBackend=Cpu` の GStreamer 経路のプレビュー/表示は互換アダプタの
  `LeasedCpuCopy`（全解像度の CPU コピー）を使う。Gpu 出力では合成層の 960×540
  読み戻しになる。
- 4K/120Hz 表示先、複数画面、実デバイス消失時の復旧は shim 単体では未検証
  （本体側の確認は `docs/verification-checklist.md` の GPU 経路を参照）。

## 配布とセットアップ

- **GStreamer は同梱しない**。利用者側で公式 MSVC x64 ランタイム 1.28.2 を導入する
  （`GSTREAMER_1_0_ROOT_MSVC_X86_64` か既定 `C:\Program Files\gstreamer\1.0\msvc_x86_64`）。
- 配布物に含めるのは `tcs_gstreamer.dll`（自作 MIT + Spout2 BSD-2 を静的組み込み）のみ。
  ライセンス表記はリポジトリ直下の `THIRD-PARTY-NOTICES.md` を参照。
- 再現ビルド: `native/gst-shim/get-spout.ps1`（Spout2 をタグ 2.007.017 / コミット固定で取得）
  → `native/gst-shim/build-shim.ps1`。GStreamer SDK は上記ランタイムに同梱の SDK を使用。
- アプリの設定は `backend`（`0` = mpv / `1` = GStreamer）と
  `outputBackend`（`0` = Cpu / `1` = Gpu）。`outputBackend=1` は D3D11.4 が必要で、
  使えない場合は起動時に Cpu へフォールバックする。既定はどちらも `0`。

## mpv 互換アダプタとの関係

GStreamer バックエンドでは `IMpvApi` / `IMpvRenderApi` / `ISpoutOutput` を
`GstMpvApiAdapter` / `GstMpvRenderApiAdapter` / `GstSpoutOutput` へ差し替える
（`PlayerBackend=Gstreamer`）。同じ組み合わせでの `OutputBackend` によって経路が分かれる:

- `OutputBackend=Cpu`: 互換アダプタの既存経路（`LeasedCpuCopy` による CPU コピーを含む）。
- `OutputBackend=Gpu`: 合成層が `tcs_player_ring_info` の共有リングを直接ソースにし、
  互換アダプタの CPU コピーと Spout 直接送信は使わない。
