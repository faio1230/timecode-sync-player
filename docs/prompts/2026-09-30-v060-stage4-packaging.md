# 指示書: v0.6.0 段 4（同梱）プラグインの同梱・VC++ 14.50 以上・ライセンスの表記

担当: サブエージェント B。作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v060`（`v0.6.0` の先頭まで進めてある）。
設計書: `docs/design/v0.6.0-prores-gpu.md`（2-1〜2-4 節、7 節の段 4、プラグインは v0.2.1）。先に全体を読むこと。
手順書: `docs/RELEASE-PROCEDURE-0.4.md`（1 節と 1.9 節。パッケージの作り方）。

## 範囲

1. **入手スクリプトの整理**（`scripts/get-prores-plugin.ps1`）
   - zip の中の `LICENSE`・`README.txt`・`SHA256SUMS.txt` も `native\gst-prores\licenses\` に置く（DLL と `.cso` は今のまま `native\gst-prores\` の直下）
   - `.cso` 6 個の SHA-256 を `SHA256SUMS.txt` の値と照合する（違えば止める）
   - csproj の `native\gst-prores\*` のコピーを `*.dll` と `*.cso` だけにする（`licenses\` を bin に入れない）
2. **パッケージ**（`scripts/package-release.ps1`）
   - 引数 `-ProResPluginDir`（既定 `native\gst-prores`）。`gstproresd3d11.dll` と `.cso` 6 個を staging の `gstreamer\lib\gstreamer-1.0\` へ（DLL の名前は変えない。`.cso` は DLL と同じフォルダ）
   - DLL の SHA-256 を `get-prores-plugin.ps1` と同じ固定値で照合し（値は 1 か所で持つ。スクリプトの間で重複させない方法を選ぶ。例: `get-prores-plugin.ps1` の値を読み出す関数か、共有の小さな `.psd1`）、`.cso` 6 個の存在と SHA を照合する。**無い・違うときは止める**（ProRes の GPU 復号の無い配布物を黙って作らない）
   - ライセンス: `gstreamer\share\licenses\gst-prores-d3d11\` に `LICENSE` と `README.txt`
   - 配布物の `README.txt`（スクリプトの中の雛形）の要件の行を「Microsoft Visual C++ 2015-2026 Redistributable (x64) 14.50.35710 or later」に。ProRes の GPU 復号の 1〜2 行（NVIDIA で既定有効、設定 `proResGpu`、他のベンダーは既定で CPU）
3. **VC++ 再頒布パッケージ 14.50.35710 以上**
   - `Resolve-VcRedist`: 再頒布パッケージの ProductVersion を読み、14.50.35710 未満なら止める（署名の検査は今のまま）。キャッシュ（`artifacts\cache\vc_redist.x64.exe`）が古いときは止めて、更新の手順をメッセージに出す
   - キャッシュの更新の手順（入手元の URL、版、SHA-256 を固定する場所）を決めてスクリプトの先頭と `docs/RELEASE-PROCEDURE-0.4.md` に書く。入手元は Microsoft の公式の URL（`https://aka.ms/vs/17/release/vc_redist.x64.exe` など。どの URL が 14.50 系を返すかを確かめ、確かめた事実を報告に書く）。ダウンロードしたファイルの版と SHA-256 を固定値と照合する
   - インストーラー（`scripts/installer.iss`）の `VcRuntimeMissing`: `HKLM64\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` の `Installed=1` に加えて、`Major`・`Minor`・`Bld` が 14.50.35710 以上かを比べる。足りなければ同梱の再頒布パッケージを実行する
4. **THIRD-PARTY-NOTICES.md** に節を 1 つ: gst-prores-d3d11 v0.2.1（入手元のタグの URL、LGPL-2.1、同梱物は `gstproresd3d11.dll` と `prores_*.cso` 6 個、ソースは入手元のタグ）。既存の節の書き方に合わせる

## してはいけないこと

- push、`main`・`v0.6.0` への書き込み、stash・reset・clean
- 実機の試験（アプリの起動、E2E、LTC シナリオ）。パッケージの作成と中身の確認まではしてよい（アプリの起動はしない）
- 製品のコード（`src/`・`native/gst-shim/`）の変更。csproj のコピーの指定だけは可
- 素材の作品名・ローカルの絶対パス（利用者名）をコミットするファイルに書くこと
- 開発機の VC++ ランタイムのインストール・更新（確認は読むだけ）

## 完了の条件

- `get-prores-plugin.ps1` を 2 回（ダウンロード・キャッシュ）実行して OK。`.cso` の照合が効くこと（1 個を書き換えた写しで止まることを、一時フォルダで確かめる）
- `package-release.ps1` で zip と setup.exe ができ、zip の中に `gstreamer\lib\gstreamer-1.0\gstproresd3d11.dll` と `.cso` 6 個、`gstreamer\share\licenses\gst-prores-d3d11\`、README.txt の要件の行があること（一覧を報告に）。shim の Release のビルドを `native\tcs_gstreamer.dll` に置く手順は RELEASE-PROCEDURE の 1 節のとおり（終わったら Debug の shim に戻す）
- 14.44 の再頒布パッケージを渡すと `package-release.ps1` が止まること
- `.iss` の比較は、Inno Setup のコンパイルが通ることと、`[Code]` の比較の関数を読んで報告に書く（実際のインストールはしない）
- `.ps1` は PowerShell 5.1 で動く・CRLF・構文エラー 0（書き換えた後に `Parser.ParseFile` で確かめる）
- 非E2E が全件合格（csproj を変えたため）
- 日本語のコミット（1 項目 1 コミットを目安）
- **終わったら、そのツリーでビルドやファイルの書き換えをしない**

## 報告

`docs/reports/2026-09-30-agent-b-v060-stage4.md` に事実だけ（合否は書かない）。コミットの一覧、項目ごとの変更、zip の中の関係するファイルの一覧、VC++ の入手元の URL と版と SHA-256、止まることを確かめた手順、未解決の点。
