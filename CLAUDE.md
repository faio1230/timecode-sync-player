# CLAUDE.md

このファイルは Claude Code がリポジトリで作業する際のガイドです。

---

## コーディング規約

### コミットメッセージ
コミットメッセージは**日本語**で書くこと。

例：
```
feat: LTC同期シーク機能を追加
fix: シークバーが戻る問題を修正
docs: 開発状況ドキュメントを更新
```

---

## プロジェクト概要

LTC（Linear Timecode）を受信し、プレイリスト上の動画クリップをタイムコードに同期して再生する **Windows WPF アプリケーション**。
ライブショーでの使用を前提とした堅牢な設計。Spout2によるVJツール連携もサポート。

---

## ビルド方法

**ビルド構成: Debug（開発標準）**

**開発環境: Windows ネイティブ（PowerShell / Visual Studio）**

```powershell
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj
```

**ソリューションファイル:** `TimecodeSyncPlayer.slnx`（.NET SDK 形式の `.slnx`、`.sln` ではない）

**テスト数:** `dotnet test` で確認。

**EXE の場所（ビルド後）:**
```
src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe
```

**ログの場所（実行後）:**
```
src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\timecodesyncplayer-YYYYMMDD.log
```

---

## プロジェクト構成

```
timecode-sync-player/
├── src/TimecodeSyncPlayer/         # アプリ本体 (net8.0-windows, WPF, x64)
│   ├── App.xaml / App.xaml.cs      # 起動・Serilogセットアップ
│   ├── MainWindow.xaml / .cs       # メインUI（映像・LTC・Playlist）
│   ├── ViewModels/                 # MVVM ViewModels
│   │   ├── MainViewModel.cs        # Playlist・Sync・Player を集約
│   │   ├── PlaylistViewModel.cs    # プレイリスト操作コマンド・状態
│   │   ├── SyncViewModel.cs        # LTC開始停止・同期トグル・状態
│   │   └── PlayerViewModel.cs      # 再生状態・コマンド
│   ├── Contracts/                  # DI インターフェース
│   ├── Strategies/                 # Strategyパターン実装
│   ├── FrameRenderer.cs            # WriteableBitmap 保持・レンダリング
│   ├── Mpv.cs                      # libmpv P/Invoke
│   ├── MpvRenderNative.cs          # mpv SW render API P/Invoke
│   ├── LtcDecoder.cs               # libltc 不使用・純C# LTC デコーダ
│   ├── LtcAudioMonitor.cs          # NAudio WASAPI 録音 + LTC デコード
│   ├── SyncDecisionEngine.cs       # LTC秒→シーク判定ロジック
│   ├── TimecodeSyncService.cs      # 同期判定・シーク抑制・ファイルロード状態の統合管理
│   ├── TimecodeSyncSeekState.cs    # 同期シーク保留状態管理
│   ├── GapFreezeHandler.cs         # ギャップ状態（Freeze/Black/通常再生）のステートマシン
│   ├── PlaylistState.cs            # プレイリスト内部状態
│   ├── PlaylistTrack.cs            # トラックモデル（record）
│   ├── SeekBarUpdateState.cs       # シークバーUI状態管理
│   ├── SpoutNative.cs              # SpoutDX P/Invoke
│   ├── SpoutOutput.cs              # Spout送信ライフサイクル管理
│   ├── PcmSampleConverter.cs       # PCM→モノラルfloat変換
│   ├── TimecodeDisplayFormatter.cs # タイムコード表示文字列生成
│   ├── TimecodeFpsSelector.cs      # FPS自動検出・固定選択
│   └── TimecodeFrameDiagnostics.cs # タイムコード診断情報
├── tests/TimecodeSyncPlayer.Tests/ # xUnit + FlaUI テスト (net8.0-windows)
├── native/                         # ネイティブDLL置き場（gitignore済み）
│   └── README.md                   # DLL入手方法
├── scripts/                        # ビルド・診断・検証スクリプト
└── docs/
    ├── SETUP.md                    # セットアップ・ビルド・テスト手順
    ├── ARCHITECTURE.md             # アーキテクチャ解説
    └── verification-checklist.md   # 実機検証チェックリスト
```

---

## アーキテクチャ（データフロー）

```
[マイク/ライン入力] → NAudio WASAPI → LtcAudioMonitor
                                             ↓
                                       LtcDecoder（純C#）
                                             ↓
                                   SyncDecisionEngine.Decide()
                                             ↓ （Seek / None）
                              MainWindow（UIスレッド）→ mpv_command("seek")
                                             ↓
                              mpv SW render context
                              （MPV_RENDER_API_TYPE_SW）
                                             ↓
                              WriteableBitmap → Image コントロール
                                             ↓
                              SpoutOutput.SendFrame() → SpoutDX.dll
```

**スレッドモデル:**
- UI スレッド: WPF メインスレッド（シーク発行・描画更新）
- オーディオスレッド: NAudio WASAPI コールバック（`LtcAudioMonitor`）
- mpv 内部スレッド: レンダー更新コールバック（`DispatcherPriority.Background` で UI へ投げる）

詳細な構造・主要コンポーネントの役割は `docs/ARCHITECTURE.md` を参照。

---

## 重要な既知の問題・クセ

### System.IO の明示的 using が必要
WPF プロジェクトのグローバル using に `System.IO` が含まれていないため、`Path`・`File`・`Directory` を使うファイルには `using System.IO;` が必要。

### vo=libmpv が必須
`vo=libmpv` を設定しないと mpv が自前ウィンドウを開いてしまう。
`vo=null` は破棄用 VO のため再生は進んでも WriteableBitmap へフレームが来ない。

### mpv SW render param 定数
`MPV_RENDER_PARAM_SW_SIZE=17`, `SW_FORMAT=18`, `SW_STRIDE=19`, `SW_POINTER=20`。
（古いドキュメントに 6-9 と記載されている場合があるが誤り）

### MpvRenderParam の明示的パディング
```csharp
struct MpvRenderParam { int Type; int _padding; IntPtr Data; }  // 16バイト
```
`_padding` を省略すると x64 ABI でアライメントがずれてクラッシュする。

### レンダー更新コールバックのデリゲート保持
`mpv_render_context_set_update_callback` に渡したデリゲートはフィールドで保持すること。
ローカル変数のみで保持すると GC に回収されクラッシュする。

---

## ネイティブ DLL について

`native/` フォルダは `.gitignore` 済み。実機動作には以下が必要：

| ファイル | 用途 | 入手方法 |
|---------|------|---------|
| `mpv-2.dll` | libmpv（動画再生） | https://mpv.io/installation/ |
| `SpoutDX.dll` | Spout2送信（オプション） | https://github.com/leadedge/Spout2 |

詳細は `native/README.md` および `docs/SETUP.md` を参照。
