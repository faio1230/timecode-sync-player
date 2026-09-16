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

本リポジトリにはネイティブDLL本体を含めません。

| ファイル | 必須/任意 | 入手方法 |
|---------|---------|---------|
| `native/SpoutDX.dll` | 任意 | [Spout2](https://github.com/leadedge/Spout2)のSDK内SpoutDXプロジェクトをビルドして配置します。配布zip／インストーラーには同梱済みです。 |
| `native/tcs_gstreamer.dll` | **必須** | 動画再生に使います。下記「GStreamer 1.28.2」を参照して`native/gst-shim`からビルドします。 |

詳細は [native/README.md](../native/README.md) を参照してください。

### GStreamer 1.28.2

配布パッケージ（zip／インストーラー）には、このアプリが実際に使う GStreamer の DLL・
プラグインとライセンス文書（`gstreamer\share\licenses`）を `gstreamer\` に同梱しています。
アプリは環境変数が未設定なら同梱ランタイムを優先し、プラグイン探索とレジストリキャッシュを
同梱ディレクトリへ固定します（システムに別の GStreamer があっても混在しません）。
**ソースからビルドして動かす場合のみ**、公式の**MSVC x64ランタイム 1.28.2**を別途導入します。
[GStreamer公式ダウンロード](https://gstreamer.freedesktop.org/download/)から
MSVC x64用の1.28.2ランタイムをインストールし、`bin`に`gstreamer-1.0-0.dll`（または
`gstreamer-1.0.dll`）があることを確認します。

- 既定の探索先: `C:\Program Files\gstreamer\1.0\msvc_x86_64`
- 別の場所へインストールした場合: 環境変数`GSTREAMER_1_0_ROOT_MSVC_X86_64`にルート
  （`bin`の親ディレクトリ）を設定します。

#### native/gst-shim のビルド

`native/gst-shim`は、GStreamerのデコード結果をD3D11テクスチャのリースAPIとして
合成層へ公開するshimです。`tcs_gstreamer.dll`は配布物にも含まれます。次の手順でビルドします。

```powershell
# Spout2 (tag 2.007.017) を vendor/Spout2 へ取得（git管理外）
powershell -File native\gst-shim\get-spout.ps1
# Debugビルド（Visual Studio Build Tools + CMake + Ninja が必要）
powershell -File native\gst-shim\build-shim.ps1 -Config Debug
# 配布物を作る場合は Release ビルドも必要
powershell -File native\gst-shim\build-shim.ps1 -Config Release
# 出力: native\gst-shim\build-debug\tcs_gstreamer.dll
#       native\gst-shim\build-release\tcs_gstreamer.dll
```

配布物（`scripts\package-release.ps1`）は `native\gst-shim\build-release\tcs_gstreamer.dll` と
同じ内容の DLL が Release 出力にあることを検査します。Debug ビルドの DLL は
MSVCP140D / ucrtbased に依存するため配布できません。

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

**注意:** `tcs_gstreamer.dll`や`SpoutDX.dll`が無くてもビルド自体は成功します。ただし
`tcs_gstreamer.dll`とGStreamerランタイムが無い場合は動画再生ができません。`SpoutDX.dll`が無い場合は
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
- `native/tcs_gstreamer.dll`とGStreamerランタイムが配置されていること
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
GStreamer ランタイムと`tcs_gstreamer.dll`が利用可能）を満たした状態で実行してください。

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

### OutputBackend

設定は`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`（`TIMECODE_SYNC_PLAYER_SETTINGS_PATH`で
上書き可）の次のキーで選びます。変更後はアプリを再起動してください（`decodeMode`も同様に、
プレイヤー生成時に読むため再起動しないと反映されません）。

| JSONキー | 値 | 既定 | 内容 |
|---|---|---|---|
| `outputBackend` | `1` = Gpu | `1` | 映像出力バックエンド。`0`（Cpu）は v0.3 の設定で、v0.4 は無視して `1`（Gpu）で起動します（警告ログ 1 行、設定ファイルは書き換えません） |
| `decodeMode` | `hardware` / `software` | `hardware` | デコード方式。`software`はCPUデコーダを優先し、GPUデコーダは最後の手段として使います（GPUに落ちた場合は警告ログ）。**変更はアプリの再起動が必要です**（プレイヤー生成時に1回だけ読みます）。不正値は`hardware`として扱い、警告ログを出します |

- v0.3 の `backend` キーは v0.4 で廃止しました（再生バックエンドは GStreamer 固定）。
  値があっても無視して警告ログを 1 行出し、設定ファイルは書き換えません。
- 再生には `tcs_gstreamer.dll` と GStreamer ランタイムが必要です。
- `outputBackend=1`で D3D11.4（`ID3D11Device5` / `ID3D11DeviceContext4`）が使えない環境では、
  起動時にダイアログを出し、**再生だけを無効**にします（アプリは開いたまま。Cpu 合成へは
  フォールバックしません。設定ファイルは書き換えません）。

### LTC fps モード（24 / 25 / 29.97 / 30）

LTC のフレームレートを Preference の「LTC fps」で選びます（既定 **Auto**）。Auto は受信した
フレームから自動判定します。判定結果はログの
`LTC fps resolved mode=... detectedFps=... resolvedFps=...` で確認できます。

| モード | 使う場面 |
|---|---|
| **Auto**（既定） | 24 / 25 / 30 は自動判定できます |
| **Fixed 24 / Fixed 25 / Fixed 30** | Auto の判定を固定したいとき |
| **Fixed 29.97** | **29.97 ノンドロップの信号を使うときは必須**。LTC のドロップフレームフラグが false のため、Auto では 30 と区別できず 30 として解決されます |

- V3 の LTC fps マトリクス（24 / 25 / 29.97 / 30）は `scripts/run-v3-accuracy.ps1` の
  `-LtcFps` と `-LtcFpsMode` で指定します。29.97 ノンドロップは `-LtcFpsMode fixed` で実行します
- 24 / 25 / 30 の Auto 判定が合わない場合は Fixed に切り替え、どのモードで再現したかを記録してください

### 同期補正モード（T5）

LTC 同期の残差（`effectiveLtcSeconds - playbackSeconds`）をデッドゾーンの内側で詰める方法を選びます。
Preference の「同期補正」で選べます（既定は **Smooth**）。

| モード | 挙動 |
|---|---|
| **Smooth**（既定） | 残差に比例して再生レートを動かします（`rate = 1 + clamp(e / 1.0s, ±0.10)`）。デッドバンド **5ms**、戻りバンド **2ms** です。LTC の時刻は音声サンプル位置から出したフレーム終端を基準にしています（`TCS_LTC_SAMPLE_CLOCK=off` で従来の受信時刻基準）。**シークやトラック切替の着地直後 1.0 秒だけ上限が ±0.20** になります（収束を 1 秒以内にするため。着地誤差 100〜150ms なら約 0.5〜0.8 秒で詰まります）。**シークを発行しない**ので絵は飛びません。GStreamer のパイプラインが `INSTANT_RATE_CHANGE` に非対応の場合は使用不可となり、その状態を表示します（**Jump へ切り替えて**ください。フラッシュシークへの自動フォールバックはしません）。レートを出しても残差が縮まない場合も、残差が大きいとき（窓の開始で 30ms 以上）に限り既定回数で諦めて警告し、補正なしへ落ちます |
| `Jump` | 残差が **80ms（LTC 2 フレーム分）** を超えたらフラッシュシークで合わせます。Smooth のデッドバンド（5ms）より広いのは、LTC の粒度と音声コールバック（50ms ごと）で残差に ±20〜40ms の揺れが乗るためです。連続 3 回で止まり、残差が 1 秒間しきい値の内側に留まると回数が戻ります（揺れで飛び続けない） |

**音声への影響（重要）**: Smooth の上限 ±10% は音程で約 **165 セント（約 1.65 半音）**です。
着地直後の 1.0 秒間は上限が ±20% になり、音程が一時的に約 **316 セント（約 3.2 半音、従来の約 2 倍）**まで振れます。
**このアプリから音を出す構成では明確に聞こえます。** 音を別マシンで出して同期を取る運用向けです。
このアプリで音も出す場合は `Jump` を選ぶか、レート上限を下げてください（将来、ピッチ補正を入れる余地があります。v0.4 の範囲外）。

### 同期オフセット（T3、v0.4）

入力側（LTC ケーブル〜オーディオ IF〜デコード）と出力側（HDMI/SDI〜LED プロセッサ〜LED ウォール）の
遅延を、**映像を先に進める 1 つの値**でまとめて補正します。Preference の「オフセット」スライダーで
-1000〜+1000 ms を即時反映で調整できます（`settings.json` の `syncOffsetMs`、既定 `0`）。

| 状況 | 入れる値 |
|---|---|
| LTC が 80ms 遅れて届く | **+80** |
| LED ウォールが 2 フレーム（33ms）遅れて光る | **+33** |
| 両方 | **+113** |

- **符号の規約: プラスで映像が先行**（下流の遅延を補正する向き）。逆に入れると誤差が増えます
- **単位は ms のみ**です。UI にもフレーム換算は出しません。異なる fps のタイムコードと素材を
  実時間で吸収する設計のため、fps 依存の単位は操作系に持ち込みません
- 適用は**同期の入口で 1 回だけ**（`effectiveLtcSeconds = ltcSeconds + syncOffsetMs / 1000`）。
  同期判断・シーク・クリップ切替・ギャップ出入りがすべて同じ量だけずれます
- 範囲外の値は clamp され、警告ログが出ます
- **1 フレーム分の定数を製品は足しません**（2026-09-16 の決定）。フレーム境界の差は素材 fps・
  LTC fps・音声デバイスのバッファで変わるため、固定値を足すと別の条件で誤差になります。
  フレーム単位のずれが残る場合は、このオフセットで合わせます
- **V3 の測定は `0`（既定）で行います。** オフセットは現場の調整手段であり、精度を作る手段ではありません

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
| 映像が表示されない | `native/tcs_gstreamer.dll`またはGStreamerランタイムが未配置です。「GStreamer 1.28.2」の節に従って導入・ビルドしてください。 |
| Spout出力ボタンが押せない（無効化されている） | `native/SpoutDX.dll` が無いだけです。Spout出力を使わないなら正常な動作であり、修正不要です。 |
| `outputBackend=1`で再生できない | D3D11.4（`ID3D11Device5` / `ID3D11DeviceContext4`）が使えない環境では、起動時ダイアログを出して**再生だけを無効**にします（Cpu 合成へはフォールバックしません）。表示された原因とログを確認してください。 |
| 再生開始に失敗する | GStreamerランタイム未導入、または`tcs_gstreamer.dll`の配置漏れです。「GStreamer 1.28.2」の節に従って導入・ビルドしてください。 |
| コンソール出力やログの日本語が文字化けする | PowerShellのコンソールエンコーディングをUTF-8に設定してください。<br>`[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` |
