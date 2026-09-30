# 指示書: v0.6.0 段 1（確かめる段）ProRes の GPU 復号を shim に仮に通す

担当: サブエージェント A。作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v060`（`v0.6.0` の `03f079b` から）。
設計書: `docs/design/v0.6.0-prores-gpu.md`（2-1・2-4・3-1・3-2・5 節）。先に全体を読むこと。

## 目的

プラグイン gst-prores-d3d11 v0.1.0 の `proresd3d11dec` が、shim の連鎖の中で動くか。動くなら同じアダプタで、共有のデバイスのまま動くか。
これを親が実機で 1 本確かめられる状態にする。**門（ベンダー・設定）はまだ作らない。** `prores-gpu` は固定で有効にする。

## 範囲（触ってよいもの）

1. `scripts/get-prores-plugin.ps1`（新規、`native/gst-shim/get-spout.ps1` と同じ形。**PowerShell 5.1 で動くこと**、ASCII のみ、CRLF）
   - `https://github.com/faio1230/gst-prores-d3d11/releases/download/v0.1.0/gst-prores-d3d11-v0.1.0-win64-gst1.28.2.zip` を `artifacts\cache\` に落とす（あれば使う）
   - zip の SHA-256 `2119a6ede678fe7ec4bfa4db3778b72a1ba57dd08c9d624e80a2cd03f7331e6b` と、展開した `gstproresd3d11.dll` の SHA-256 `77b0776adbd62363e251623077546a2ea168c7dcb7f6cde72f04707433ff72d4` を照合し、違えば止める
   - 展開先は `native\gst-prores\`（DLL と `prores_*.cso` 6 個を同じフォルダ。DLL の名前は変えない）
2. csproj: `native\gst-prores\*` があれば `bin\...\gst-extra-plugins\` へコピー（`native\tcs_gstreamer.dll` のコピーと同じ書き方）
3. `src/TimecodeSyncPlayer/Gst/GstNative.cs` の `GstNativeLibraryResolver`: **同梱でない（Bundled でない）ときだけ**、exe の隣の `gst-extra-plugins` があれば `GST_PLUGIN_PATH` に足す（既存の値があれば `;` で連結）。`GST_PLUGIN_SYSTEM_PATH` と `GST_REGISTRY` は触らない。単体試験を 1 本（フォルダあり／なし、同梱のときは足さない）
4. shim `include/tcs_video_profiles.h`: `prores-gpu = { "video/x-prores", nullptr, parse nullptr, dec "proresd3d11dec", conv "d3d11colorconvert" }` を GPU の群の末尾（av1-gpu の後、index 4）に。`test/shim_test.cpp` の表と復号順の期待を件数 10・CPU は index 5〜9 に直す（**順番の規則は変えない**。試験の説明にそう書く）
5. shim `tcs_gstreamer.cpp`:
   - `prores-gpu` の試行は D16-b（`decoder_matches_shim_adapter`）を通さない。代わりに `build_video_chain_static` で dec の要素を作った直後に `adapter-luid`（gint64）へ `device_luid()` を書く。プロパティが無ければ（`g_object_class_find_property` で確認）この試行を `chain-fail` にする
   - ログ（`LOG`、接頭辞は既存どおり）:
     - `prores-gpu: adapter-luid set=<書いた値> read=<READY 以降に読み返した値>`（試行ごと 1 行）
     - **最初のフレームの受け取りで 1 回だけ**: 出力バッファの先頭のメモリが `gst_is_d3d11_memory` なら、その `GstD3D11Memory` の device（`GstD3D11Device*` と `gst_d3d11_device_get_device_handle` の `ID3D11Device*`）と、shim のデバイス（`gst_dev` と `ID3D11Device*`）を並べて出す: `prores-gpu: out-mem d3d11=<0|1> dev=<ptr> shim-dev=<ptr> same=<0|1>`
     - 最初のフレームで、デコーダの src pad の caps と appsink の caps（colorimetry・range を含む文字列）を出す: `decode.caps profile=<名前> dec-src=<caps> sink=<caps>`。**これは prores-cpu の試行でも出す**（色の比較のため。設計書 8 節）
   - `load.summary` の profile は既存どおり
6. 既存のコメントの食い違い 2 件（設計書 9 節）: `cpp:2275` 付近の HAP のコメントを実装（既定で有効、`TCS_HAP=off` で無効）に合わせる、`GstNative.cs:9-10` の文字化けした XML コメントを直す（意味は前後から復元。分からなければ短い日本語で役割を書く）

## してはいけないこと

- push、`main`・`v0.6.0` への書き込み、stash・reset・clean
- 実機の試験（アプリの起動、E2E、LTC シナリオ）。**ビルドと非E2E と shim の `--policy-only` まで**
- ベンダーの門・設定・UI・パッケージ（段 2 以降）
- 素材の作品名・ローカルの絶対パス（利用者名を含む）を、コミットするファイルに書くこと
- shim のロック規則（`frame_lock` を持ったまま状態変更・シークを呼ばない）を破ること。`python scripts/check-shim-lock-rule.py` を通す

## 完了の条件

- `powershell -File scripts\get-prores-plugin.ps1` で `native\gst-prores\` に DLL と `.cso` 6 個（2 回目はダウンロードせず照合だけ）
- shim の Debug と Release のビルド（`native/gst-shim/build-shim.ps1`、`-Config Release`）が通る。Debug の DLL を `native\tcs_gstreamer.dll` に置く
- `tcs-shim-test --policy-only` が通る
- `dotnet build` と非E2E（`--filter "FullyQualifiedName!~E2ETests"`）が全件合格
- `check-shim-lock-rule.py` が PASS
- 日本語のコミット（1 項目 1 コミットを目安）

## 報告

`docs/reports/2026-09-30-agent-a-v060-stage1.md` に事実だけ（合否は書かない）: コミットの一覧、各項目で変えたファイルと要点、試験の件数、ビルドの結果、気づいた点（プラグインの `adapter-luid` のプロパティの型・既定値など、コードやプラグインの introspection で分かった事実）。
親が実機で確かめる手順（どの素材で何を見るか）の案を最後に書く。
