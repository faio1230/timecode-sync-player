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
# Release ビルド + zip + setup.exe（Inno Setup、GStreamer ランタイム同梱、VC++ 再配布の連鎖）
powershell -File scripts\package-release.ps1            # Version は csproj から読む
#   必要なら -InnoSetupCompiler / -GStreamerRoot / -VcRedistPath を明示
# 出力: artifacts\release\（zip、TimecodeSyncPlayer-v0.4.0-setup.exe）
```

- 出力の zip と setup.exe の **SHA-256 を `docs/release-0.4-plan.md` に記録**する
- 配布物に `libmpv-2.dll` / `mpv-2.dll` が含まれていないこと、`tcs_gstreamer.dll` と GStreamer のプラグイン閉包が含まれていることを確認する（`Expand-Archive` して一覧）
- 別のディレクトリに展開して起動し、**素材 1 本の再生と Spout 送信、LTC 同期 1 本（V3 ハーネスではなく手動でよい）**を確認する。ログの `=== TimecodeSyncPlayer v0.4.0 起動 ===` を見る

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
