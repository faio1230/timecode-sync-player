# D16: ハイブリッド GPU（内蔵 AMD + NVIDIA）で AV1 の GPU デコードがクラッシュする（v0.4.2）

作成: 2026-09-17 07:10、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: **main の最新（`ed6ad47` 以降）** を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
背景: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` 末尾「D16」。検証機（Windows 11 Home、AMD Radeon 内蔵 = DXGI adapter 0、NVIDIA RTX 3080 Laptop = adapter 1）で v0.4.1 の setup 版が AV1 4K の `--open` 起動から約 13.5 秒（`TCS_NO_AUDIO=1` では 4.0 秒、リング作成の約 3.5 秒後）で 0xC0000005、Faulting module は **AMD の atidxx64.dll**。`GST_DEBUG=2`（GStreamer の d3d11 がデバッグレイヤーを有効にする）だと落ちない。開発機（GPU 1 枚）では再現できない。

## 1. 事実（検証機の tcs-gst ログと DXGI 一覧）

- アプリの `OutputEngine` と shim の external-luid デバイスは **adapter 0 = AMD**（`adapterLuid=74727`）
- `d3d11av1dec` は **NVIDIA デバイスにだけ登録**され（AMD は「device does not support AV1 decoding」）、GST ログに `d3d11av1dec0 Different device, will create new one`
- video-chain は `queue ! av1parse ! d3d11av1dec ! d3d11colorconvert ! capsfilter ! appsink`。`ring: created 3840x2160 BGRA slots=3 epoch=1` の直後に途切れ、`load.attempt result` も bus error も出ない
- ProRes（`prores-cpu`、`avdec_prores` → `d3d11upload`）は同じ機で正常（CPU デコード → shim デバイスへアップロードの経路は安全）
- 仮説: **デコーダが別アダプタ（NVIDIA）のデバイスで動き、その d3d11 メモリが shim のデバイス（AMD）の `CopySubresourceRegion`（`tcs_gstreamer.cpp` 1098 行付近 / 2541 行付近の `gst_d3d11_memory_get_resource_handle` → `p->context->Copy…`）に渡っている**。別デバイスのリソースを渡すのは未定義でドライバがクラッシュする。GStreamer 側の `d3d11colorconvert` が別デバイス入力を CPU 経由で写すかどうかは要確認

## 2. 調べること（コードを読む。実機はまだ使わない）

1. `on_new_sample` / `lease` の経路で、appsink から来た `GstD3D11Memory` の **デバイスが `p->gst_dev` と同じか**を検査しているか（`gst_d3d11_memory_get_device` 等）。していなければ、どこで別デバイスのリソースが `p->context` に渡るか
2. GPU プロファイルの chain で `d3d11colorconvert` と `capsfilter` がどのデバイスコンテキストを使うか（`GST_D3D11_DEVICE_HANDLE_CONTEXT_TYPE` を shim が流しているか、`NEED_CONTEXT` の応答 596 行付近）。デコーダが別デバイスを作ったとき、`d3d11colorconvert` は shim のデバイスで動くのか、デコーダのデバイスで動くのか
3. GStreamer 1.28 の d3d11 で、**別デバイス間のバッファコピー**（`gst_d3d11_buffer_copy_into` 相当）が CPU ステージング経由で安全に行われる条件

## 3. 直し方の方針（親の案。調査で覆してよい。設計差異は報告に書く）

- **案 A（推奨）: プロファイル選択の時点で、ring デバイスのアダプタにそのコーデックの d3d11 デコーダが登録されているかを調べ、無ければ GPU プロファイルを飛ばして CPU プロファイルへ**（`dav1d` → `d3d11upload`。既存の prores-cpu と同じ経路）。判定は `gst_element_factory_make` の後にデコーダの `adapter-luid` プロパティ（d3d11 デコーダは持っている）を shim のアダプタ LUID と比べる、または要素の登録一覧から該当アダプタの要素を選ぶ
- **案 B: 別デバイスのメモリを検出したら、appsink 直前に `d3d11download ! d3d11upload`（shim デバイス）を入れる**。転送コストが増える（4K24 なら許容範囲かもしれないが、要計測）
- どちらでも、**別デバイスのリソースを `p->context` に渡さない検査**を `on_new_sample` の入口に入れ、検出したら `set_error` して落ちずに失敗させる（防御。ログに両デバイスの LUID を出す）
- 不変条件 I1〜I13、H-3、shim の C ABI は変えない。D13 の queue はそのまま
- **バージョンは上げない**（csproj の `Version` は 0.4.1 のまま。検証機で直ったと確認できてから親が上げる。利用者の方針）

## 4. D17（同時に設計だけ。実装は親の合図後）

不一致プロファイル 1 件あたり検証機では約 3.0 秒（開発機では 0.1〜0.2 秒）。ProRes 4K は 8 件不一致で 24.8 秒、AV1 は 3 件で 9.6 秒。案: **最初の試行で demux の pad caps（`video/x-av1` 等）を読んだら、以後の試行は caps に合うプロファイルだけに絞る**（既存の `profile_matches_caps` を試行順の前段で使う）。なぜ検証機では 3 秒かかるか（`capsMismatch` の早期打ち切りが効いていない理由）も併せて調べる

## 5. 検証

- 開発機（GPU 1 枚）では D16 は再現しない。単体: `FakeGstNative` ではなく shim 側のテスト（`tcs-shim-test`）に「デコーダのアダプタ不一致 → CPU プロファイルへ」の分岐が通ることを、環境変数（例: `TCS_FORCE_DECODER_ADAPTER_MISMATCH=1`、テスト専用）で強制して確認
- 回帰: 既存の shim 実素材テスト 10 本、E2E 一部（GStreamerBackend / SystemScenario）、V5 を 1 回、V3 を 1 本（実機は一報のうえ親の合図）
- 検証機での確認は親が行う（Release ビルドを Tailscale で渡す）

## 6. 報告

調査結果（2 節の 1〜3 の答え、根拠の行番号）、選んだ案と理由、コミット、テスト結果、証跡パス。**合否は書かない。ローカルの絶対パスは書かない。**
