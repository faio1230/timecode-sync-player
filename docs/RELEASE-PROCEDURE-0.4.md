# v0.4.0 リリース手順（親が実施する）

作成: 2026-09-17 03:45、親。前提: `docs/release-0.4-plan.md` の完了の定義 1〜5 が満たされていること。
公開先は GitHub Releases（これまでどおり **Pre-release、タイトル `v0.4.0 (beta)`**）。

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
- LTC 同期は検証機で `LtcHardwareLoopE2ETests`（VB-CABLE）をインストール済みアプリに向けて回す（`TIMECODE_SYNC_PLAYER_E2E_APP_PATH`、`GSTREAMER_1_0_ROOT_MSVC_X86_64`=同梱の gstreamer フォルダ、**暫定で PATH の先頭に同梱 `gstreamerin` を足す**。D18）
- 合格してから 2 節へ進む
- **版番号は直ってから上げる（2026-09-17、利用者の方針）**: 検証機との往復の途中ビルドは csproj の `Version` を変えない。ビルドの識別は `ProductVersion` の `+<コミット SHA>` と配布物の SHA-256 で行い、記録にはその 2 つを書く。完了条件がすべて合格してから次の版番号に上げ、CHANGELOG・リリースノートをまとめてタグと公開を行う

## 2. タグと公開

```powershell
git tag -a v0.4.0 -m "v0.4.0"
git -c credential.helper= -c "credential.helper=!gh auth git-credential" push origin v0.4.0
gh release create v0.4.0 --prerelease --title "v0.4.0 (beta)" --notes-file <リリースノート.md> artifacts\release\*.zip artifacts\release\*-setup.exe
```

- リリースノートは `CHANGELOG.md` の 0.4.0 節（英語）を本文にし、日本語の要点は `docs/release-0.4-plan.md` 3 節へのリンクで補う
- **本文にローカルの絶対パスを入れない**

## 3. 公開後

- `docs/PARENT-HANDOVER-2026-09-12.md` とメモリ（`release-status-v0.1`）を「v0.4.0 公開済み」に更新
- 利用者の判断待ちで残ったもの（現場素材の一覧、V4 の実素材追試、D9）は v0.4.1 の候補として `docs/release-0.4-plan.md` に残す
