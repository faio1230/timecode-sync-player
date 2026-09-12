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
| `native/tcs_gstreamer.dll` | 任意 | `PlayerBackend=Gstreamer`を使う場合のみ。下記「GStreamerバックエンド」を参照して`native/gst-shim`からビルドします。 |

詳細は [native/README.md](../native/README.md) を参照してください。

### GStreamer 1.28.2（PlayerBackend=Gstreamer を使う場合）

GStreamerバックエンドで再生する場合は、公式の**MSVC x64ランタイム 1.28.2**を別途導入します
（本リポジトリには同梱しません）。[GStreamer公式ダウンロード](https://gstreamer.freedesktop.org/download/)から
MSVC x64用の1.28.2ランタイムをインストールし、`bin`に`gstreamer-1.0-0.dll`（または
`gstreamer-1.0.dll`）があることを確認します。

- 既定の探索先: `C:\Program Files\gstreamer\1.0\msvc_x86_64`
- 別の場所へインストールした場合: 環境変数`GSTREAMER_1_0_ROOT_MSVC_X86_64`にルート
  （`bin`の親ディレクトリ）を設定します。

#### native/gst-shim のビルド

`native/gst-shim`は、GStreamerのデコード結果をD3D11テクスチャのリースAPIとして
合成層へ公開するshimです。`tcs_gstreamer.dll`は配布物に含めません。次の手順でビルドします。

```powershell
# Spout2 (tag 2.007.017) を vendor/Spout2 へ取得（git管理外）
powershell -File native\gst-shim\get-spout.ps1
# Debugビルド（Visual Studio Build Tools + CMake + Ninja が必要）
powershell -File native\gst-shim\build-shim.ps1 -Config Debug
# 出力: native\gst-shim\build-debug\tcs_gstreamer.dll
```

`tcs_gstreamer.dll`は`native\tcs_gstreamer.dll`へ置くか、`build-debug`に出力したままにすると、
本体のビルド時に`src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\`へ自動コピーされます
（`native\tcs_gstreamer.dll`が優先）。GStreamerランタイムのパスが通っていないとDLLを
ロードできないため、先にランタイムを導入してください。

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

VB-CABLEを使った乱操作・耐久試験は [MONKEY-TESTING.md](MONKEY-TESTING.md) を参照してください。通常のテストとは別に明示実行します。

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

## 8. バックエンド設定と環境変数

### PlayerBackend / OutputBackend

設定は`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`（`TIMECODE_SYNC_PLAYER_SETTINGS_PATH`で
上書き可）の次のキーで選びます。変更後はアプリを再起動してください。

| JSONキー | 値 | 既定 | 内容 |
|---|---|---|---|
| `backend` | `0` = Mpv / `1` = Gstreamer | `0` | 再生バックエンド。Gstreamerは`tcs_gstreamer.dll`とGStreamerランタイムが必要です |
| `outputBackend` | `0` = Cpu / `1` = Gpu | `0` | 映像出力バックエンド。Cpuは従来の`OutputFrame`→`WriteableBitmap`→`SendImage`経路です |

例（GStreamer + GPU出力）:

```json
{
  "backend": 1,
  "outputBackend": 1
}
```

- `outputBackend=1`でも、D3D11.4（`ID3D11Device5` / `ID3D11DeviceContext4`）が使えない環境では
  起動時にCpuへフォールバックし、ログに理由を出力します（設定ファイルは書き換えません）。
- `backend=1`は`tcs_gstreamer.dll`とGStreamerランタイムが見つからない場合、再生開始に失敗します。
  ログのエラーを確認し、前節のセットアップを行ってください。
- 組み合わせの違い: `backend=0`＋`outputBackend=1`はmpvのスナップショットをGPU合成へ渡します。
  `backend=1`＋`outputBackend=1`はshimの共有リングを直接ソースにします。
  `backend=1`＋`outputBackend=0`は互換アダプター（CPU読み戻し）経路です。

### 環境変数

| 変数 | 用途 |
|---|---|
| `TIMECODE_SYNC_PLAYER_SETTINGS_PATH` | `settings.json`の場所を上書き（自動テストの隔離用） |
| `TIMECODE_SYNC_PLAYER_SPOUT_NAME` | Spout送信者名の上書き。未設定は既定名 |
| `TIMECODE_SYNC_PLAYER_OUTPUT_TRACE` | 出力トレース（`manifest.json` / `events.jsonl` / `summary.json`）の出力ディレクトリ。未設定は無効。停止時にまとめて書き出すため、強制終了では残りません |
| `TIMECODE_SYNC_PLAYER_OUTPUT_TRACE_CAPACITY` | 出力トレースのイベント上限（正の整数）。未設定・不正値は既定 `1000000`（60Hz で約 8.3 分）。上限到達で以降は破棄され、最初の 1 件で警告ログ、`summary.json` の `droppedEvents` と `capacity` に記録されます。イベントはメモリに溜めるため、上限を上げると常駐が増えます（実測: 1,000,000 件で約 170MB、60 分 60Hz 相当の約 730 万件で約 1.2GB） |
| `TIMECODE_SYNC_PLAYER_TEST_CARD` | `1`または`true`で起動時にテストカードをON（動作確認用） |
| `TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS` | `<秒>[,<秒>...]`。指定時刻に疑似デバイス消失を発生させ、復旧経路を確認する検証用 |
| `GSTREAMER_1_0_ROOT_MSVC_X86_64` | GStreamerランタイムのルート。既定は`C:\Program Files\gstreamer\1.0\msvc_x86_64` |

## 9. トラブルシューティング

| 症状 | 原因・対処 |
|---|---|
| 起動直後に `mpv_create` 失敗、または映像が表示されない | `native/libmpv-2.dll`（または互換用`mpv-2.dll`）が未配置、もしくはx86/x64の不一致です。`get-mpv.ps1`でx64版を導入してください。 |
| Spout出力ボタンが押せない（無効化されている） | `native/SpoutDX.dll` が無いだけです。Spout出力を使わないなら正常な動作であり、修正不要です。 |
| `outputBackend=1`にしたが映像が出ない | D3D11.4が使えずCpuへフォールバックした可能性があります。ログの`OutputBackend: Gpu を指定されましたが利用できないため Cpu へフォールバックします`を確認してください。 |
| `backend=1`で再生開始に失敗する | GStreamerランタイム未導入、または`tcs_gstreamer.dll`の配置漏れです。「GStreamer 1.28.2」の節に従って導入・ビルドしてください。 |
| コンソール出力やログの日本語が文字化けする | PowerShellのコンソールエンコーディングをUTF-8に設定してください。<br>`[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` |
