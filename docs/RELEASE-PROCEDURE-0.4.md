# v0.4.0 リリース手順（親が実施する）

作成: 2026-09-17 03:45、親。前提: `docs/release-0.4-plan.md` の完了の定義 1〜5 が満たされていること。
公開先は GitHub Releases（これまでどおり **Pre-release、タイトル `v0.4.0 (beta)`**）。

> **2026-09-26 追記（利用者の方針）**: 安定版は v0.5.x の最新。**v0.5.4 から Latest**（`--prerelease` を付けず、`--latest`）。
> v0.5.3 までと v0.6.x は Pre-release（`--prerelease`、タイトル `vX.Y.Z (beta)`）。v0.6.x を公開しても v0.5.x の Latest は外さない（`--latest=false`）。
> v0.5.4 の公開前の確認では、未解決の製品側項目が 0 であることを示す。

## 0. ゲート（すべて main で、親が自分で確認する）

| # | 条件 | 確認方法 |
| --- | --- | --- |
| 1 | V1〜V10（V7 は対象外、V8 は既知の仕様） | `docs/release-0.4-plan.md` 1 節の表がすべて合格／対象外 |
| 2 | 既定が出荷構成 | `AppSettings` の既定と起動ログ `OutputBackend: Gpu` |
| 3 | 全 E2E 成功 | 受信ツールのある作業ツリーで **63 実行・63 合格**を 1 回（親のツリーは 59/59 + 受信依存 4 件がスキップ） |
| 4 | SETUP / verification-checklist が現行 | 段 5 後半の統合後に親が通読 |
| 5 | リリースノート草案 | `docs/release-0.4-plan.md` 3 節と `CHANGELOG.md` 0.4.0 節 |
| 6 | 非E2E 全件・ロック規則・grep（mpv / CPU 合成 / ローカルパス） | `dotnet test`、`check-shim-lock-rule.py`、`git grep` |
| 7 | バージョン | `csproj` の `Version` が `0.4.0`、起動ログが `v0.4.0` |

## 1. 配布物を作る

```powershell
# 先に shim の Release ビルド（package-release.ps1 は native\gst-shim\build-release\tcs_gstreamer.dll を要求する）
powershell -File native\gst-shim\build-shim.ps1 -Config Release
# csproj は native\tcs_gstreamer.dll があればそれを bin へコピーするので、Release の shim をそこへ置いてからパッケージする（終わったら消して Debug の shim に戻す）
Copy-Item native\gst-shim\build-release\tcs_gstreamer.dll native\tcs_gstreamer.dll
# Release ビルド + zip + setup.exe（Inno Setup、GStreamer ランタイム同梱、VC++ 再配布の連鎖）
powershell -File scripts\package-release.ps1            # Version は csproj から読む
#   必要なら -InnoSetupCompiler / -GStreamerRoot / -VcRedistPath を明示
# 出力: artifacts\release\（zip、TimecodeSyncPlayer-v0.4.0-setup.exe）
```

- 出力の zip と setup.exe の **SHA-256 を `docs/release-0.4-plan.md` に記録**する
- 配布物に `libmpv-2.dll` / `mpv-2.dll` が含まれていないこと、`tcs_gstreamer.dll` と GStreamer のプラグイン閉包が含まれていることを確認する（`Expand-Archive` して一覧）
- 別のディレクトリに展開して起動し、**素材 2 本（うち音声付き AAC 44.1kHz を 1 本。`scripts\make-e2e-media.ps1` の `test_720p30_aac44k.mp4`）の再生と Spout 送信、LTC 同期 1 本（V3 ハーネスではなく手動でよい）**を確認する。ログの `=== TimecodeSyncPlayer v0.4.0 起動 ===` を見る

## 1.5 クリーンな検証機で確認する（公開の前。2026-09-17 追加、利用者の指示）

- 配布物は **公開前に**別マシンのクリーン環境（GStreamer 未導入、setup.exe のみ）で確認する。検証用のビルドを GitHub のリリースに出さない（v0.4.1 で一度だけ例外にした）
- 受け渡しは同じアカウントのプライベートネットワーク経由（`docs/local/LOCAL-PATHS.md` の「検証機」。公開文書にホスト名を書かない）
- 見るもの: exe の FileVersion、`logs	cs-gst-YYYYMMDD.log` の `load.summary`（profile と total_ms）、`all video profiles failed` が 0、**音声付き素材（44.1kHz と 48kHz、映像トラック先頭）**と現場の実素材、残プロセス 0
- LTC 同期は検証機で `scripts\run-ltc-scenarios.ps1 -AppExe <インストール先の exe>` を 1 回実行する（前提検査、素材生成、テスト、ログと証跡の複製、要約まで行う。D18 により同梱 gstreamer を環境変数なしで認識する。実素材で回す場合は `-MediaDir <素材フォルダ>` を足す）
- 合格してから 2 節へ進む
- **版番号は直ってから上げる（2026-09-17、利用者の方針）**: 検証機との往復の途中ビルドは csproj の `Version` を変えない。ビルドの識別は `ProductVersion` の `+<コミット SHA>` と配布物の SHA-256 で行い、記録にはその 2 つを書く。完了条件がすべて合格してから次の版番号に上げ、CHANGELOG・リリースノートをまとめてタグと公開を行う

## 1.9 公開の直前に必ず見る（2026-09-19 追加。0.4.5 で踏みかけた）

**`artifacts\release\` は候補を跨いで溜まる。同じ版番号の古い成果物がそのまま残る。**

0.4.5 で実際に起きかけたこと:

| 成果物 | 生成時刻 | 中身 |
| --- | --- | --- |
| `TimecodeSyncPlayer-v0.4.5-setup.exe` | 01:53 | **候補 5**（SHA-256 `81DD0AB0…`） |
| `TimecodeSyncPlayer-v0.4.5-win-x64.zip` | 02:45 | 候補 6（検証したもの） |

この状態で `gh release create ... artifacts\release\*.zip artifacts\release\*-setup.exe` を打つと、
**検証していない候補のインストーラーを、検証した zip と一緒に公開する**。版番号が同じなので
リリースページを見ても気づけない。

**したがって:**

1. **公開の直前に `package-release.ps1` を `-SkipInstaller` なしで通し直す**（zip と setup.exe を
   同じコミットから同時に作る）
2. **`Get-ChildItem artifacts\release -Filter '*<版>*' | Select Name, LastWriteTime` を見て、
   zip と setup.exe の時刻が揃っていることを確認する**
3. **glob ではなく明示のファイル名で `gh release create` に渡す**
4. 古い候補の成果物は `.candidate<N>-stale` に改名して退避しておく（削除しない。SHA の突き合わせに要る）

Inno Setup は `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` にある（Program Files ではない）。
`package-release.ps1` はそこも探すので `-InnoSetupCompiler` は不要。

**インストーラーは zip と同じステージングから作られる**ので、**1 回の実行なら中身は必ず一致する**。
食い違うのは候補を跨いだときだけ。だから「公開直前に 1 回通し直す」で解決する。

### 検証した成果物と公開する成果物がずれる件

**公開時に作り直すと zip の SHA-256 は変わる**（ステージングを作り直すので、中の日時が変わる）。
つまり「検証機が検証した SHA」と「公開した SHA」は一致しない。手順はこうする:

1. **検証機へ送った zip を `.candidate<N>-verified` に複製して残す**（上書きされないように）
2. **文書の修正をすべて済ませてから**パッケージする。`ProductVersion` に埋まるコミット SHA が
   公開するコミットと一致するようにするため
3. 作り直したら、**退避した zip と中身をファイル単位のハッシュで突き合わせる**。
   全ファイルが一致すれば「検証したものと同じ中身」と言い切れる
4. リリースノートには**公開した成果物の SHA-256 と、検証機が検証したコミット**の両方を書く。
   コミットが違う場合は `git diff` で製品コードの差分が無いことを確認して、その旨も書く

## 2. タグと公開

```powershell
git tag -a v0.4.0 -m "v0.4.0"
git -c credential.helper= -c "credential.helper=!gh auth git-credential" push origin v0.4.0
gh release create v0.4.0 --prerelease --title "v0.4.0 (beta)" --notes-file <リリースノート.md> artifacts\release\*.zip artifacts\release\*-setup.exe
```

- **リリース本文は `docs/release-notes/v<版>.md`（日本語）**。`--notes-file` でそのまま渡す。
  **v0.4.0 の手順は「CHANGELOG の英語節を本文にする」と書いていたが、v0.4.1 以降は日本語の
  リリースノートを本文にしている**（`gh release view v0.4.3 --json body` で確認、2026-09-19）。
  `CHANGELOG.md` はリポジトリの記録として別に維持する（英語）
- **本文に出す前に `（下書き）` と作業用の注記（「確定前に差し替えること」など）を消す**
- **本文にローカルの絶対パスを入れない**

## 3. 公開後

- `docs/PARENT-HANDOVER-2026-09-12.md` とメモリ（`release-status-v0.1`）を「v0.4.0 公開済み」に更新
- 利用者の判断待ちで残ったもの（現場素材の一覧、V4 の実素材追試、D9）は v0.4.1 の候補として `docs/release-0.4-plan.md` に残す
