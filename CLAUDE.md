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
│   ├── App.xaml / App.xaml.cs      # 起動・DI・Serilogセットアップ
│   ├── MainWindow.xaml / .cs       # メインUI（映像・LTC・Playlist）
│   ├── ViewModels/                 # MVVM ViewModels
│   │   ├── MainViewModel.cs        # Playlist・Sync・Player を集約
│   │   ├── PlaylistViewModel.cs    # プレイリスト操作コマンド・状態
│   │   ├── SyncViewModel.cs        # LTC開始停止・同期トグル・状態
│   │   └── PlayerViewModel.cs      # 再生状態・コマンド
│   ├── Contracts/                  # 境界の型（IPlaybackApi / PlaybackResult / IRenderUpdateSource / IVideoSource ほか）
│   ├── Strategies/                 # Strategyパターン実装
│   ├── Gst/                        # shim 連携（GstPlaybackApi / GstRenderUpdateSource / GstBackendState / GstNativeApi）
│   ├── Output/                     # GPU 合成・全画面・Spout・デバイス復旧（OutputEngine / ComposeLayer / GStreamerSource ほか）
│   ├── LtcDecoder.cs               # libltc 不使用・純C# LTC デコーダ
│   ├── LtcAudioMonitor.cs          # NAudio WASAPI 録音 + LTC デコード
│   ├── LtcSyncController.cs        # LTC 受信・信号断・Single/Continue/Gap の分岐統合
│   ├── SyncDecisionEngine.cs       # LTC秒→シーク判定ロジック
│   ├── TimecodeSyncService.cs      # 同期判定・シーク抑制・ファイルロード状態の統合管理
│   ├── TimecodeSyncSeekState.cs    # 同期シーク保留状態管理
│   ├── GapFreezeHandler.cs         # ギャップ状態（Freeze/Black/通常再生）のステートマシン
│   ├── PlaylistState.cs            # プレイリスト内部状態
│   ├── PlaylistTrack.cs            # トラックモデル（record）
│   ├── SeekBarUpdateState.cs       # シークバーUI状態管理
│   ├── GstSpoutOutput.cs           # Spout 有効/無効の状態（送信は OutputEngine の Spout worker）
│   ├── RenderSession.cs            # レンダーコンテキスト・フレーム通知の駆動・世代管理
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
                              IPlaybackApi（GstPlaybackApi）→ tcs_gstreamer.dll
                                             ↓
                              shim の共有リング（GPU テクスチャ）+ 共有フェンス
                                             ↓
                              OutputEngine の GPU worker が合成
                                   ↙                    ↘
                           全画面 Present            Spout worker
```

**スレッドモデル:**
- UI スレッド: WPF メインスレッド（型付き再生 API の同期呼び出し・コマンド発行・状態 mailbox）
- GPU worker（`OutputEngine.GPU`）: 合成・全画面 Present・プレビュー読み戻し・ソース lease
- Spout worker（`OutputEngine.Spout`）: 別デバイスで合成画像を Spout 送信
- オーディオスレッド: NAudio WASAPI コールバック（`LtcAudioMonitor`）
- shim 内部スレッド（GStreamer）: フレーム更新コールバック（`RenderSession` 経由・`DispatcherPriority.Background` で UI へ投げる）

詳細な構造・主要コンポーネントの役割は `docs/ARCHITECTURE.md` を参照。

---

## 重要な既知の問題・クセ

### System.IO の明示的 using が必要
WPF プロジェクトのグローバル using に `System.IO` が含まれていないため、`Path`・`File`・`Directory` を使うファイルには `using System.IO;` が必要。

### レンダー更新コールバックのデリゲート保持
レンダー更新コールバックを登録する API に渡したデリゲートはフィールドで保持すること。
ローカル変数のみで保持すると GC に回収されクラッシュする（`GstBackendState._thunk`、`RenderSession._updateCallback`）。

### GPU 出力は D3D11.4 が必須
`ID3D11Device5` / `ID3D11DeviceContext4` が無い環境では、起動時にダイアログを出して**再生だけを無効**にする。
CPU 合成へのフォールバックは無い（v0.4 で除去）。詳細は [docs/SETUP.md](docs/SETUP.md)。

### shim のロック規則（I13）
GStreamer の状態変更・シーク（`gst_element_set_state` / `gst_element_seek` / `gst_element_send_event`）を
`frame_lock` 保持中に呼ばない。検査は `scripts/check-shim-lock-rule.py`。

### 再生 API は型付き・失敗は結果型
相対シークは API に無い（`Seek` は絶対秒。呼び出し側が現在位置へ加算する）。
失敗は `PlaybackResult(bool Success, string? Error)` で返し、呼び出し側が成功と取り違えない。

### v0.3 の設定キーは無視する
`backend`（再生バックエンドの選択）と `outputBackend=Cpu` は v0.4 で廃止。値があっても無視して
警告ログを 1 行出し、設定ファイルは書き換えない。

---

## ネイティブ DLL について

`native/` フォルダは `.gitignore` 済み。実機動作には以下が必要：

| ファイル | 用途 | 入手方法 |
|---------|------|---------|
| `tcs_gstreamer.dll` | 映像ソース（GStreamer shim）。`native/gst-shim/build-shim.ps1` でビルド | `native/gst-shim/README.md` |
| `SpoutDX.dll` | Spout2送信（Spout 出力を使う場合に必要） | https://github.com/leadedge/Spout2 |

GStreamer ランタイム（`GSTREAMER_1_0_ROOT_MSVC_X86_64` または `Program Files\gstreamer`）が必要です。

詳細は [native/README.md](native/README.md) および [docs/SETUP.md](docs/SETUP.md) を参照。
