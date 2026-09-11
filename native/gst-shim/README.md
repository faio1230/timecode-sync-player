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

## リース API のセマンティクス

合成層から見た各 API の規則。対応する契約仕様は session-refactor 側
`docs/GPU-SOURCE-CONTRACT-SPEC.md`（規則 1〜7 と「GStreamer／HAP 実装が満たす条件」、
読み取り参照）。一致しない点は末尾に理由付きで列挙する。

### `tcs_player_acquire(player, generation, out info)` → 1 = リース成立 / 0 = なし

- **現在世代のフレームだけ**を返す。`latest` の世代が `generation` と一致しない場合は 0。
  load / seek / step は世代を進め、seek は `latest` を破棄するため、旧世代の
  デコード済みフレームは返らない。
- 準備できないときは 0（=なし）を返す。**黒・前フレーム・エラー画像で代用しない**
  （保持は合成層の責務）。
- **非ブロッキング**。デコードを待たない（内部 `frame_lock` を短時間取るだけ）。
- リース保持中の再呼び出し: 同じ世代なら同じリースを返す（新フレームは渡さない）。
  別世代を要求した場合は 0 で、新しいフレームを取るには先に `release` が必要。
- `info` = generation / seq / pts_ns / width / height / is_gpu。seq は shim 内の
  単調増加番号（プロセス内識別用）、pts_ns はサンプル PTS（無ければセグメント位置）。

### `tcs_player_leased_texture(player, out texture, out subresource, out dxgi_format)` → 0 = 成功

- リース保持中のみ有効。`ID3D11Texture2D*` を返す（player のデバイス上）。
- `dxgi_format` は **87 = B8G8R8A8_UNORM (BGRA)**。NV12 は現状返さない（相違点参照）。
- `subresource` は **0**。デコーダ配列テクスチャ等は shim 所有の単一サブリソース
  テクスチャへ平坦化してから返す（pool 由来テクスチャのポインタはリース中のみ有効、
  平坦化テクスチャは player と同時に破棄）。
- 返したポインタは **release まで有効**。release 後・destroy 後に使用してはならない。

### `tcs_player_leased_cpu_copy(player, dst, dst_stride)` → 0 = 成功

- リース中のフレームを CPU BGRA へ行コピーする。staging + Map の **GPU 読み戻し**を
  含むためプレビュー/デバッグ専用。Spout へは `tcs_player_publish_spout`
  （GPU テクスチャ送信、検証層）を使う。

### `tcs_player_release(player)`

- リースを返却し、プールのテクスチャを再利用可能にする。リースが無ければ no-op（冪等）。
- **リース中テクスチャへの書き込み（上書き）は行わない**。返却は合成層が GPU 使用
  完了を確認してから（既存 LatestPool と同じ規則）。

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

- shim が参照を保持するサンプルは **最大 2 枚**: `latest`（未リースの最新）と
  `leased`（リース中）。加えて appsink 内部キューが最大 4（`max-buffers=4, drop=FALSE`）。
- 新フレーム到着時は `latest` を置換し、**置換前の latest（未リースの旧フレーム）を
  捨てる**。`leased` は決して置換・上書きしない。
- リース中に新フレームが来ても acquire は 0（release 後に最新へ進む）。
- プール実体は GStreamer のデコーダ/コンバータのバッファプール（有限・可変）。
  リースを長時間保持した場合はプール拡張 → appsink キュー → バックプレッシャの順で
  対応し、acquire 自体はブロックしない。

### デバイスと同期

- **外部 `ID3D11Device` を Adopt した場合**: デコード・色変換・リーステクスチャ・Spout
  送信はすべてそのデバイス上。同一デバイスの読者（合成層）はそのまま読める。
  **共有フェンス等の追加同期は不要** — 同一 immediate context への投入順で書き込み
  順序が保たれる（MultithreadProtected は shim が有効化）。
- **external_device=NULL（shim 所有デバイス）の場合**: リーステクスチャはアプリから
  直接読めない。`leased_cpu_copy`（CPU コピー）か shim 内の Spout 送信を使う。
  別デバイス/別プロセスへテクスチャを直接渡すことは**未対応**（共有 NT ハンドル＋
  フェンス/keyed mutex が必要。Spout 経路は spoutDX の共有で別途検証済み）。

### 契約仕様（規則 1〜7）との対応と相違

| 仕様 | shim | 備考 |
| --- | --- | --- |
| 規則1 世代排除 | 一致 | seek/load/step で latest 破棄。acquire(gen) は一致時のみ |
| 規則2 位置に基づく最新優先 | **部分一致** | position は受け取らず「現世代の最新 1 枚」。位置選択は合成層（pts_ns 参照）。過去行列は持たない |
| 規則3 なしを返す | 一致 | 0 / TCS_ERR_NO_FRAME。黒・前画像なし |
| 規則4 lease 寿命 | **部分一致** | プールは GStreamer 側（有限・可変）。shim 保持は最大 2（latest+leased）で置換は未リースのみ。Begin/CompleteGpuUse 相当は無く release のみ（冪等） |
| 規則5 デバイス | 一致（同一デバイス前提） | 外部デバイス Adopt 対応。別デバイス実装は未対応（フェンス未実装） |
| 規則6 非ブロッキング | 一致 | acquire/set_generation は短いロックのみ。デコードを待たない |
| 規則7 診断 | **部分一致** | decoder/gpu_path/frames/generation はあり。世代排除・NotReady・置換・最大同時 lease のカウンタは未実装 |
| 条件: NV12 テクスチャ | **相違** | 現状 BGRA のみ（d3d11colorconvert）。NV12 直出しは converter 差し替えで可能だが未実装 |
| 条件: HAP/BC テクスチャ | 未実装 | 専用分岐まで意図的に拒否 |
| 条件: 時計 | 一致 | pts_ns を返すのみで GStreamer の running time は露出しない。世代は set_generation/seek で合成層が制御 |
| リング容量 3 以上のプール | 未実装 | 合成層接続時に再検討（現段階は mpv 互換経路で実用上十分） |

相違はいずれも「最小 ABI で現段階の接続（mpv 互換経路）を成立させる」ための
スコープ判断であり、合成層接続時に規則 2/4/7 と NV12/リングを再検討する。

## 本体 OutputEngine の GStreamerSource（段階 2、2026-09-11）

本体側 `src/TimecodeSyncPlayer/Output/GStreamerSource.cs` は
`tcs_player_acquire` / `tcs_player_leased_texture` / `tcs_player_release` を
`IVideoSource`（docs/GPU-SOURCE-CONTRACT-SPEC.md）へ適合させる薄いアダプター。
管理テスト（`tests/TimecodeSyncPlayer.Tests/GStreamerSourceTests.cs`）は fake で契約規則を検証する。

shim と契約の差はアダプター側で次のように吸収する（契約テストで固定）:

- **位置選択**: shim は position を受け取らず現世代の latest 1 枚を返すため、
  返却画像の `pts_ns` を `SourceImageStamp.PositionSeconds` として扱う。過去行列は持たない。
- **リース**: shim は「リース保持中の acquire は同じ画像を返す」ため、参照カウント付きの
  共有リースとして同一 Stamp を返す。最後の参照が返ると `release` を 1 回だけ呼ぶ。
- **Ended**: shim の 0（=なし）から Ended を区別できないため `NotReady` とする。保持は合成層の責務。
- **形式**: `dxgi_format != 87`（BGRA 以外）は `NotReady` として拒否する。

本体配線（段階 6、2026-09-11）:

- `PlayerBackend=Gstreamer` かつ `OutputBackend=Gpu` のとき、`OutputEngine` の起動直後に
  `GstBackendState.SetExternalDevice()` でデバイスポインタを渡し、
  `tcs_player_create` が外部デバイスを Adopt する。`GstMpvRenderApiAdapter` の
  `LeasedCpuCopy` と `GstSpoutOutput` の直接送信はこの組み合わせでは使わない
  （`RenderSession.SuppressFrameSnapshots`）。既定の mpv 経路と GStreamerCpu 経路は不変。
- ソースは `GStreamerSource` のリースを SRV（AddRef 付き所有ラップ）で直接描画し、
  CPU/GPU コピーを挟まない。shim は「リース保持中は同じ画像を返す」ため、リースは
  毎合成 tick 返却し、描画中のテクスチャは AddRef した自前参照で保持する。
- 世代は shim 側の値を観測して対応付ける（load/seek の自動 +1 をトレース
  `gst.generation:` に記録）。リース保持中は新フレームが返らない仕様を吸収している。
- immediate context は合成層の GPU worker だけが操作する。shim 内部（GStreamer の
  ストリーミングスレッド）も同一デバイス/コンテキストを使うが、D3D11 の
  マルチスレッド保護（作成時に SINGLETHREADED を付けない既定）で直列化される。

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
mxf→mxfdemux、avi→avidemux、raw ES→直接チェーン、他→未知扱い（同上）。

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
