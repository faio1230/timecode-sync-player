# セットアップ手順

このドキュメントは、別マシンで本リポジトリをクローンしてから TimecodeSyncPlayer をビルド・実行・検証するまでの手順をまとめたものです。

---

## 1. 前提環境

- Windows 10/11 (x64)
- .NET 8 SDK

`dotnet` コマンドが利用可能であること、およびバージョンを確認します。

```powershell
dotnet --version
```

`8.x.x` 系のバージョンが表示されれば問題ありません。表示されない場合は [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) をインストールしてください。

---

## 2. クローンとディレクトリ構成

```powershell
git clone https://github.com/faio1230/timecode-sync-player.git
cd timecode-sync-player
```

主なディレクトリ構成:

```
timecode-sync-player/
├── src/TimecodeSyncPlayer/         # アプリ本体 (net8.0-windows, WPF, x64)
├── tests/TimecodeSyncPlayer.Tests/ # xUnit + FlaUI テスト
├── native/                         # ネイティブDLL置き場（gitignore済み、自分で配置する）
├── scripts/                        # ビルド・診断・検証スクリプト
├── docs/                           # ドキュメント（本ファイルもここ）
└── TimecodeSyncPlayer.slnx         # ソリューションファイル（.NET SDK形式）
```

---

## 3. ネイティブDLLの配置

本リポジトリにはネイティブDLL本体を含めません。libmpvは、リポジトリ直下で次のスクリプトを
1回実行して導入する方法を推奨します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1
```

スクリプトはmpv公式が案内するshinchiroの最新通常x64開発アーカイブを選び、公開SHA-256と
ピン留めした7zr.exeのSHA-256を検証して、アーカイブ直下の`libmpv-2.dll`を名前を変えず
`native/libmpv-2.dll`へ配置します。GitHub Releaseに検証可能なdigestがない場合は既定で中断します。
独立した手段で確認済みの場合に限り、`-AllowUnverified`を明示して続行できます。

手動導入する場合は、[mpv公式インストール案内](https://mpv.io/installation/)からリンクされる
[shinchiroの最新Release](https://github.com/shinchiro/mpv-winbuild-cmake/releases/latest)で
`mpv-dev-x86_64-<日付>-git-<コミット>.7z`（`v3`ではない通常x64版）を取得し、アーカイブ直下の
`libmpv-2.dll`をそのまま`native/`へ配置します。従来名`mpv-2.dll`も互換用に利用できますが、
リネームは不要です。

| ファイル | 必須/任意 | 入手方法 |
|---------|---------|---------|
| `native/libmpv-2.dll` | **必須** | 推奨は`scripts/get-mpv.ps1`。手動時も上流名のまま配置します。**x64版**であることを確認してください。 |
| `native/SpoutDX.dll` | 任意 | [Spout2](https://github.com/leadedge/Spout2)のSDK内SpoutDXプロジェクトをビルドして配置します。配布zip／インストーラーには同梱済みです。 |

詳細は [native/README.md](../native/README.md) を参照してください。

---

## 4. ビルド

```powershell
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

ビルド時、`native/` に存在するDLLだけが `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\` へ自動コピーされます。

**注意:** libmpv DLLや`SpoutDX.dll`が無くてもビルド自体は成功します。ただし
`libmpv-2.dll`／`mpv-2.dll`のどちらも無い場合は動画再生ができません。`SpoutDX.dll`が無い場合は
Spout出力ボタンが無効化されるだけで、それ以外は正常に動作します。

---

## 5. テスト

### 5.1 非E2Eテスト

デスクトップセッションが無い環境（CIなど）でも実行でき、ネイティブDLLも不要です。

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName!~E2ETests"
```

### 5.2 E2Eテスト

FlaUIによるUI自動操作テストのため、以下が必須です。

- **デスクトップセッションが必要**（リモートデスクトップの切断状態やヘッドレスCI環境では実行できません）
- `native/libmpv-2.dll`（または互換用`mpv-2.dll`）が配置されていること
- **テスト実行中に実際にアプリウィンドウが開閉します**（フォーカスを奪う可能性があるため、実行中は他の操作を避けてください）

```powershell
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --filter "FullyQualifiedName~E2ETests"
```

---

## 6. 一括検証スクリプト

ビルド・非E2Eテスト・E2Eテストをまとめて実行する検証スクリプトが `scripts/` に用意されています。

```powershell
scripts\run-timecodesyncplayer-verification.ps1 -Profile Strict
```

主なプロファイル:

| プロファイル | 内容 |
|---|---|
| `Full` | ビルド + 非E2E + E2E をすべて実行 |
| `Quick` | E2Eをスキップして高速に確認 |
| `Strict` | 自己診断テストに加え、警告もすべて失敗扱いにする厳格モード |
| `LogOnly` | ビルド・テストを行わず、既存ログのみ診断 |

`Strict` / `Full` プロファイルはE2Eテストを含むため、5.2の実行前提（デスクトップセッション必須・
libmpv DLL配置済み）を満たした状態で実行してください。

---

## 7. アプリの起動確認とログの場所

ビルド後、以下のEXEを実行します。

```powershell
src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe
```

または `dotnet run` でも起動できます。

```powershell
dotnet run --project src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
```

実行時のログは以下に出力されます。

```
src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\timecodesyncplayer-YYYYMMDD.log
```

起動後、映像が表示され、LTC入力デバイスがプルダウンに表示されることを確認してください。

---

## 8. トラブルシューティング

| 症状 | 原因・対処 |
|---|---|
| 起動直後に `mpv_create` 失敗、または映像が表示されない | `native/libmpv-2.dll`（または互換用`mpv-2.dll`）が未配置、もしくはx86/x64の不一致です。`get-mpv.ps1`でx64版を導入してください。 |
| Spout出力ボタンが押せない（無効化されている） | `native/SpoutDX.dll` が無いだけです。Spout出力を使わないなら正常な動作であり、修正不要です。 |
| コンソール出力やログの日本語が文字化けする | PowerShellのコンソールエンコーディングをUTF-8に設定してください。<br>`[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` |
