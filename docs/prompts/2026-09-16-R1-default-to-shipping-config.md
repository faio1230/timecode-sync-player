# R1: 既定値を出荷構成（GStreamer + GPU 合成）に切り替え、E2E を出荷構成で通す

`docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md` の **段 1**。**この段では何も削除しない。**
mpv と CPU 合成は「設定で明示したときだけ使われる経路」として残し、段 2・3 で消す。

利用者の決定（2026-09-16）:
- GPU 合成が使えない PC では、起動時に**ダイアログを出す。アプリは閉じず、再生だけできない状態にする**
- 名前の整理は型付き API へ置き換える（段 4。この段では触らない）

---

## 1. 直すこと

### 1-1. 既定値

- `AppSettings` の既定: `Backend = Gstreamer`、`OutputBackend = Gpu`
- `AppSettingsManager` の不正値の補正（`Backend` → `Mpv`、`OutputBackend` → `Cpu` に戻している箇所）も出荷構成へ
- 設定ファイルに `backend` / `outputBackend` が明示されていれば、この段ではその値に従う（段 2・3 で扱いを変える）

### 1-2. GPU 合成が使えないときの見せ方（CPU 合成への黙ったフォールバックをやめる）

対象は `outputBackend=Gpu`（既定）のとき。3 つの失敗を**同じ見せ方にそろえる**。

| 失敗 | 今 | 直した後 |
| --- | --- | --- |
| 起動時の D3D11.4 検出に失敗（`OutputBackendState`） | 警告ログだけで CPU 合成へ落ちる | ダイアログ。CPU 合成へは落ちない |
| 検出は通ったが GPU スレッドの資源初期化で例外（`OutputEngine` の `Fault`） | ループが止まり、UI に何も出ない | ダイアログ |
| player の生成に失敗（`ShowWindowLoadedSessionInitializationError`） | 「mpv_create 失敗。mpv-2.dll を確認してください。」 | ランタイム名を含まない文言のダイアログ |

ダイアログに入れるもの:
- 何ができないか（「映像出力を開始できません。再生はできません」）
- 原因（検出や例外の `Detail` をそのまま）
- 必要な条件（Direct3D 11.4 に対応した GPU とドライバ）
- ログの場所（`logs\timecodesyncplayer-YYYYMMDD.log` の実パス）

ダイアログを閉じた後:
- **アプリは開いたまま**。プロジェクトを開く・設定を見る・保存するは使える
- 再生・シーク・LTC 同期による再生開始は**行わない**（例外やクラッシュにしない）。映像の領域には「映像出力を利用できません」と表示する
- どの状態で再生を止めているかを 1 か所で判定する（散らばった null 判定で済ませない）
- ダイアログは起動ごとに 1 回だけ

E2E で自動操作しているとダイアログが残るとテストが止まるので、**E2E からは失敗を注入できる手段を用意し、ダイアログの表示を E2E で 1 件確認する**（手段は提案してよい。製品の設定項目は増やさない）。

### 1-3. E2E を出荷構成で走らせる

- 個別に `backend` / `outputBackend` を書いているテストから、その指定を外す:
  `D5BlackAfterSwitchReproE2ETests`（`backend=1, outputBackend=1`）、`GStreamerBackendE2ETests`（`backend=1`）、
  `CanvasTestCardE2ETests` と `ExitDialogE2ETests`（`outputBackend=1`）
- `SyncAccuracyE2ETests` は測定ディレクトリの `settings.json` を使うので、そのままでよい
- 非E2E のうち既定値を前提にしているもの（`AppSettingsTests`、`OutputBackendStateTests`、`DiRegistrationTests` など）を直す。
  **CPU フォールバックを前提にしたテストは、1-2 の挙動を固定するテストへ書き換える**

## 2. 進め方（実機の使い方）

**実機は親の合図があるまで使わないこと。** 親が別の測定に使っている場合がある。

1. まず**変更前の main** で E2E を全件回し、基準を取る（この時点の構成は大半が mpv + CPU 合成）。
   - コマンド: `dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj -c Debug --no-build --filter "Category=E2E"`
   - 結果（件数、失敗したテスト名と失敗理由の 1 行）を記録する
2. 1-1〜1-3 を実装し、非E2E を通す
3. 出荷構成で E2E を全件回す
4. **1 と 3 で結果が違うテストを 1 件ずつ分類する**:
   - (a) 出荷構成の欠陥（アプリを直す）
   - (b) テストが mpv や CPU 合成の挙動を前提にしている（テストを直す。何を前提にしていたかを書く）
   - (c) 実行ごとに揺れる（同じ構成で 2 回回して確かめる）
   - 直すのは (a) と (b)。(a) は**直す前に親へ報告する**（出荷構成の欠陥は親が扱いを決める）
5. 他のカテゴリ（`Accuracy`、`SeekResync`、`Monkey`、`RealProject`、`SpoutComparison`）はこの段では回さない

## 3. 報告に入れるもの

- 変更前と変更後の E2E の結果表（テスト名、変更前、変更後、分類、対応）
- 1-2 のダイアログのスクリーンショット（E2E の失敗注入で出したもの）
- 非E2E の件数、`check-shim-lock-rule.py`（shim を触っていなければ触っていないと書く）

## 4. 守ること

1. **何も削除しない。** mpv と CPU 合成のコードはこの段では残す
2. ギャップの Freeze が GPU 構成で `Hold` になる件（計画書 1-3）は触らない。親が V4 と合わせて確認する
3. 合否判定は書かない。親が出す
4. 実機は直列に 1 本ずつ。自分が起動した PID だけ終了する。プロセス名での kill はしない
5. main への書き込みはしない。自分のブランチにコミットする

---

## 実装側メモ（2026-09-16、agent-b。後任が同じことを調べ直さないための記録）

### 実装コミットと変更ファイル

- 実装コミット: `1e9bf866a612ca15b5e9330cd9a5d0ae6ec75987`（短縮 `1e9bf86`、branch `agent-b`）
- 変更 14 ファイル（新規 2）:
  - src: `AppSettings.cs`（既定と不正値補正）、`App.xaml.cs`（コメントのみ）、`MainWindow.xaml` / `MainWindow.xaml.cs`、
    `Output/OutputBackendState.cs`、`Output/PlaybackAvailabilityState.cs`（新規）
  - tests: `AppSettingsTests.cs`、`OutputBackendStateTests.cs`、`Helpers/E2EAppRunner.cs`、
    `E2E/PlaybackUnavailableE2ETests.cs`（新規）、`E2E/CanvasTestCardE2ETests.cs`、
    `E2E/D5BlackAfterSwitchReproE2ETests.cs`、`E2E/ExitDialogE2ETests.cs`、`E2E/GStreamerBackendE2ETests.cs`

### 再生可否の状態と 2 つのゲート

- `PlaybackAvailabilityState`（`TimecodeSyncPlayer.Output`、public）が唯一の状態。`IsAvailable` と
  **最初に記録した** `Detail` を保持する（UI スレッドからのみ更新）
- MainWindow のゲートは 2 つだけ:
  - `IsPlaybackAvailable`: 再生**開始**のゲート（LoadFile / LoadFilePaused / SeekTo / TogglePlayPause /
    SeekRelative / CycleSpeed / ファイルを開く・追加 / タイムラインシーク / シークバー確定 /
    signal-loss resume / gap resume）
  - `IsPlayerReady` = `IsPlaybackAvailable && _mpv != IntPtr.Zero`: player を使う処理のゲート
    （LTC 同期コンテキストの IsMpvReady、フレーム毎 UI、OSD、ミュート・音量、各 effect の IsMpvReady）
- 利用不可にする経路（すべて `MainWindow.EnterPlaybackUnavailable` に集約）:
  1. 起動時検出失敗: `OutputBackendState.PlaybackAvailable == false` → MainWindow ctor で取り込み、
     `InitializeWindowLoadedSession` が即 false。player 生成と OutputEngine の生成/開始を行わない
  2. 実行中の GPU ワーカー fault: `OnTick` が `_outputEngine.Faulted` を検知 → 同じ経路（タイマー停止）
  3. player 生成失敗: `ShowWindowLoadedSessionInitializationError` → 同じ経路（文言から mpv 等のランタイム名を除去）
- `OutputBackendResolver` は Cpu へフォールバックしない。`Effective` は要求値（Gpu）のままで
  `PlaybackAvailable=false` を返す。`FallbackApplied` は削除した

### E2E の注入手段と追加テスト

- 環境変数 `TIMECODE_SYNC_PLAYER_FORCE_GPU_UNAVAILABLE`（`1`/`true`/`yes` で検出をスキップして利用不可。
  `0`/`false`/`no`/空/未設定は無効）。製品の設定項目は増やしていない
- 追加 E2E: `PlaybackUnavailableE2ETests.GpuUnavailable_ShowsDialogOnceAndKeepsAppOpenWithPlaybackDisabled`
  （ダイアログ 1 回 → OK → 利用不可表示のままアプリ継続 → 再生ボタンでクラッシュしない → ダイアログ再出なし）
- そのために `E2EAppRunner.FindWindowByName`（デスクトップをタイトルで探す）を追加

### 未検証の点（実機で確認する）

- ネイティブ MessageBox をタイトル一致で探す `E2EAppRunner.FindWindowByName` は実機未検証。
  見つからない場合は FlaUI の `MainWindow.ModalWindows` 経由などに切り替える
- 起動ダイアログは Loaded 後に `Dispatcher.BeginInvoke(DispatcherPriority.Background)` で出している
  （UIA の起動待ちをモーダルでブロックしないため）。このタイミングで E2E が拾えるかは実機で確認
- `OutputBackendState` の初期化前プレースホルダは `Effective = Cpu` のまま。MainWindow を初期化せず
  構築する単体テストが GPU を起動しないための措置。**段 3（CPU 合成の除去）で必ず引っかかる**ので、
  そのときは初期化必須にするか、テスト側を直す

### 検証状況

- 非E2E: 1954 件成功・失敗 0（`--filter "Category!=E2E"`）
- `check-shim-lock-rule.py`: PASS（**shim は未変更**）
- ビルド: アプリ・テストとも 警告 0・エラー 0。削除はしていない
- E2E: 未実行。合図後に「変更前 main（`149025f`）の基準取り → agent-b で全件」の順で実施する
