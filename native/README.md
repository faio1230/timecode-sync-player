# Native Dependencies

このフォルダは、ソースからビルドするときに必要なネイティブDLLの配置先です。
`native/*.dll` は `.gitignore` 対象であり、リポジトリにはDLL本体を含めません。

## 推奨: スクリプトでlibmpvを導入

リポジトリのルートで、最初に次を1回実行してください。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1
```

スクリプトは[mpv公式Installationページ](https://mpv.io/installation/)が案内する
[shinchiro Windows builds](https://github.com/shinchiro/mpv-winbuild-cmake/releases/latest)から
最新の通常x64開発アーカイブを取得し、SHA-256を検証して`native/libmpv-2.dll`へ配置します。
アーカイブ展開には[7-Zip公式の7zr.exe](https://www.7-zip.org/a/7zr.exe)を一時利用し、
ピン留めしたSHA-256と照合します。処理後にダウンロードした一時ファイルを削除します。

GitHub Releaseに検証可能なSHA-256 digestがない場合、スクリプトは目立つ警告を出して既定で
中断します。独立した手段でアーカイブを検証済みの場合に限り、`-AllowUnverified`を明示すると
続行できます。

既存の`libmpv-2.dll`がある場合は上書き確認を行います。自動実行で確認済みとして
上書きする場合のみ、`-Force`を指定してください。

## libmpvを手動で配置する場合

1. [shinchiro Windows buildsの最新Release](https://github.com/shinchiro/mpv-winbuild-cmake/releases/latest)
   を開きます。
2. Assetsから`mpv-dev-x86_64-<日付>-git-<コミット>.7z`をダウンロードします。
   `x86_64-v3`版ではなく、通常の`x86_64`版を選んでください。
3. [7-Zip](https://www.7-zip.org/)でアーカイブを展開します。
4. アーカイブ直下の`libmpv-2.dll`を、このフォルダへ名前を変えずに配置します。

従来名の`mpv-2.dll`も互換性のため利用できます。両方ある場合は`mpv-2.dll`、
`libmpv-2.dll`の順に読み込まれます。どちらも無い場合、ビルドは成功しますが動画再生はできません。

## SpoutDX.dll

配布zipには`SpoutDX.dll`を同梱します。zip版の利用者が別途用意する必要はありません。

ソースからビルドする場合のみ、[Spout2](https://github.com/leadedge/Spout2)のSDKから
x64版`SpoutDX.dll`を用意し、このフォルダへ配置してください。DLLが無い場合は
Spout出力ボタンが無効になるだけで、動画再生やLTC同期は利用できます。

## 配置例

```text
timecode-sync-player/
└── native/
    ├── libmpv-2.dll   # 必須（ソースビルド時）
    └── SpoutDX.dll    # Spout出力を使う場合
```

配置後は[セットアップ手順](../docs/SETUP.md)に従ってビルドしてください。
