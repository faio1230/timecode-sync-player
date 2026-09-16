# v0.4 段 4（型付き再生 API）設計調査

状態: 2026-09-16、調査のみ。**コードは変更していない。** 基点: main `2e4ec3a`（U1 統合後）を `agent-a` へ通常マージした時点。
参照: `docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md`（1-5、2 節「段 4」、3 節「判断 2」）、`docs/prompts/2026-09-16-STAGE3-cpu-compose-removal.md`。
利用者の決定（2026-09-15）: 型名にランタイム名を入れない。親の推奨は案 B（型付きの操作 API に置き換える）。

この文書は判断を書かない。材料、選択肢、根拠、衝突箇所を並べる。

---

## 1. `IMpvApi` 越しの呼び出しの棚卸し

`IMpvApi` の境界呼び出しは **約 66 か所**（アプリ本体）。内訳:

| ファイル | SetPropertyString | GetProperty | GetPropertyString | CommandString | SetRateInstant | Create/Initialize/TerminateDestroy | 計 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `MainWindow.xaml.cs` | 11 | 11 | 8 | 2 | 1 | 1 | 34 |
| `MpvStartupPropertyApplier.cs` | 10 | - | - | - | - | - | 10 |
| `PlaybackOperationsCoordinator.cs`（effects 経由） | 5 | - | - | 3 | - | - | 8 |
| `GapPlaybackCommandExecutor.cs` | 3 | - | - | 1 | - | - | 4 |
| `GapFreezePathGuard.cs` | 1 | - | 1 | 1 | - | - | 3 |
| `AudioControlCoordinator.cs`（effects 経由） | 4 | - | - | - | - | - | 4 |
| `MpvSessionInitializer.cs` | - | - | - | - | - | 3 | 3 |
| 計 | 34 | 11 | 9 | 7 | 1 | 4 | 66 |

（`FormatDouble` は `GetProperty` 11 か所の第 3 引数として 11 回。単独の用途はない）

### 1-1. SetPropertyString（34 か所）の全部

| 名前 | 値 | 呼び出し元（シンボル） | 個数 |
| --- | --- | --- | ---: |
| `pause` | `yes` / `no` | `MainWindow`（LTC 信号ロス、ギャップ、プロジェクト復帰 pause 解除、Sync 再開、`TogglePlayPause`、`SeekRelative`）、`PlaybackOperationsCoordinator`（`StopPlayback` / `LoadFile` ×2 / `LoadFilePaused` / `SeekTo`）、`GapPlaybackCommandExecutor`（`PauseForGap` / `LoadPausedAt`）、`GapFreezePathGuard.Check`、`MpvStartupPropertyApplier.Apply` | 15 |
| `volume` / `mute` | `0.###`（InvariantCulture）/ `yes,no` | `AudioControlCoordinator.ApplyStartup` / `ToggleMute` / `SetVolume`（effects 経由） | 4 |
| `speed` | `ToString(InvariantCulture)` | `MainWindow`（`IPlaybackController.CycleSpeed`） | 1 |
| `osd-bar` | `no` | `GapPlaybackCommandExecutor.PauseForGap` | 1 |
| `osd-bar` | `yes` | `MpvStartupPropertyApplier.Apply`、`MainWindow`（`ShowOsdBar` effects） | 2 |
| `osd-msg3` | デバッグ OSD 文字列 | `MainWindow.UpdateOsd` | 1 |
| `vo` / `hwdec` / `keep-open` / `osd-level` / `osd-font-size` / `osd-color` / `osd-border-color` / `osd-border-size` | 起動時の固定値 | `MpvStartupPropertyApplier.Apply` | 8 |
| （effects 配線行） | - | `MainWindow`（playback / audio の `SetPropertyString` 委譲） | 2 |
| 計 | | | 34 |

戻り値の使い方: `pause` の rc は `GapFreezePathGuard` / `GapPlaybackCommandExecutor` / `PlaybackOperationsCoordinator.LoadFile` が記録・ログに使うが、`MainWindow` の直接呼び出しはすべて無視。volume/mute の rc は無視。

### 1-2. GetProperty / GetPropertyString（20 か所）

| 名前 | API | 呼び出し元（シンボル） | 個数 | 戻り値の使い方 |
| --- | --- | --- | ---: | --- |
| `time-pos` | GetProperty | `MainWindow.ReadMpvTimePos`（ギャップ復旧・位置提供）、`SingleModeSyncCoordinator` effects（`GetTimePos`）、`ContinueOnTrack` effects（`GetTimePos`）、`TryCompleteGapFreezeAsync`、`IsNativeGapFreezeTargetReady`、`UpdatePerFrameUI`、`TrySetSeekBarFromPointer`、`RefreshCurrentVideoFrame` | 8 | rc==0 かつ有限値のときだけ採用。失敗は null / 据え置き |
| `duration` | GetProperty | `GapEnterEffects.GetMpvDuration`、`OnTick`（タイマー） | 2 | rc==0 のときだけ採用 |
| `container-fps` | GetProperty | `MainWindow.FetchMetadata` | 1 | rc==0 かつ >0 のときだけ採用 |
| `pause` | GetPropertyString | `MainWindow.TryCompleteGapFreezeAsync`、`IsNativeGapFreezeTargetReady` | 2 | `== "yes"` の真偽 |
| `seeking` | GetPropertyString | `MainWindow.IsNativeSeeking` | 1 | `!= "no"` の真偽（空文字もシーク中扱い） |
| `path` | GetPropertyString | `MainWindow.IsCurrentMpvPathExpectedForGapFreeze`、`GapFreezePathGuard.Check` | 2 | 期待パスとの比較 |
| `width` / `height` | GetPropertyString | `MainWindow.FetchMetadata` | 2 | 数値文字列。失敗は空文字 |
| `video-codec` / `audio-codec` | GetPropertyString | `MainWindow.FetchMetadata` | 2 | 表示用。`audio-codec` は GStreamer 実装では常に空を返す |

### 1-3. CommandString（7 か所）

| コマンド文字列（生成元） | 呼び出し元 | 個数 | 戻り値の使い方 |
| --- | --- | --- | --- |
| `no-osd loadfile "<path>" replace [-1 start=F6]`（`MpvPlaybackCommandBuilder`） | `PlaybackOperationsCoordinator.LoadFile` / `LoadFilePaused` / `TracedLoad`、`GapPlaybackCommandExecutor.LoadPausedAt`、`GapFreezePathGuard.Check` | 5 | rc==0 を成功として扱う。失敗はログと結果型 |
| `[no-osd] seek <F3> absolute+exact`（`PlaybackOperationsCoordinator.SeekTo`） | 同上 | 1 | rc!=0 は警告して false |
| `seek <数値> relative+exact`（`MainWindow.IPlaybackController.SeekRelative`） | 同上 | 1 | rc==0 のときだけ pause を書き直す |
| `stop` | `PlaybackOperationsCoordinator.StopPlayback` | 1 | rc は無視 |

注: `CommandString` の境界呼び出しは 7 か所（直接 4＋effects 経由 3）。`PlaybackOperationsCoordinator.TracedLoad` は 1 か所の effect 呼び出しで loadfile を 3 用途（位置つき / 位置なし / 一時停止ロード）に使うため、上の表の「個数」は用途数である。

### 1-4. セッション操作（4 か所）

| API | 呼び出し元 | 使い方 |
| --- | --- | --- |
| `Create` | `MpvSessionInitializer.Initialize` | ハンドル取得。0 なら `CreateFailed` |
| `Initialize` | `MpvSessionInitializer.Initialize` | 負値なら `TerminateDestroy` して `InitializeFailed` |
| `TerminateDestroy` | `MpvSessionInitializer.Initialize`（失敗時）、`MainWindow`（終了処理） | 破棄 |
| `SetRateInstant` | `MainWindow`（`LtcSyncController` の `ApplyRateInstant` effects） | `== 0` を成功とし、失敗は Smooth 使用不可 |

### 1-5. 使われていないもの

- `IMpvApi.Free` — アプリからの呼び出し 0 件（アダプタ実装は no-op）
- `IMpvApi.FormatDouble` — `GetProperty` の引数としてのみ（フォーマット種別の抽象）
- `frame-step` / `frame-step force` — アプリからの発行 0 件（`GstCommandTranslator` とそのテストのみ）

---

## 2. `IMpvRenderApi` 越しの呼び出しの棚卸し

| メンバー | 呼び出し元 | 用途 |
| --- | --- | --- |
| 7 つの定数プロパティ（`MpvRenderParam*`、`MpvRenderApiTypeSw`、`MpvRenderUpdateFrame`） | `RenderSession.Create`（SW 種別文字列）、`RenderContextParameterBuilder.BuildSoftwareBackendParams`、`RenderFrameParameterBuilder.Build`（size / format / stride / pointer） | `RenderParam[]` の型番号と更新フラグの抽象。値は mpv のレンダー API と同じ番号 |
| `RenderContextCreate` | `RenderSession.Create` | context 取得（GStreamer 実装はポインタ検証のみ） |
| `RenderContextSetUpdateCallback` | `RenderSession.Create` | フレーム通知コールバックの登録（shim → `GstBackendState.AttachRenderCallback`） |
| `RenderContextUpdate` | `RenderSession.DrainNativeUpdatesAsync` | 更新フラグの消費（`GstBackendState.ConsumeUpdate`） |
| `RenderContextRender` | `RenderSession.RenderNativeFrame`（trace 経路 2 か所） | リース画像を `RenderParam` のバッファへコピー（`GstBackendState.RenderInto` → `LeasedCpuCopy`） |
| `RenderContextFree` | `RenderSession.FreeContext` | コールバック解除 |

`RenderParam` / `RenderUpdateFn` は段 2 で `Contracts/RenderTypes.cs` へ移設済みで、名前は中立。
`RenderSession` の `GpuFrameSink` / `PositionSecondsProvider` は現在設定する箇所がなく、`_api.` の呼び出しは上表の 8 か所に限られる。

---

## 3. `GstCommandTranslator` / `GstMpvApiAdapter` の解釈範囲

### 3-1. 解釈されるコマンド（`GstCommandTranslator`）

| 形 | 結果 | 備考 |
| --- | --- | --- |
| `stop` | `GstStopOperation` | |
| `frame-step` / `frame-step force` | `GstFrameStepOperation` | アプリ未使用 |
| `no-osd ` / `no-osd-bar ` 前置詞 | 除去して再解釈 | `no-osd-bar` を発行するアプリ箇所は無い |
| `loadfile "<path>" replace [-1] [start=SECONDS]` | `GstLoadFileOperation(path, start?)` | `start=` は InvariantCulture で解析。引用符と `\\` `\"` を解釈 |
| `seek SECONDS absolute*` / `seek SECONDS relative*` | `GstSeekOperation(seconds, relative)` | 秒は InvariantCulture で解析 |

### 3-2. 解釈されるプロパティ

| API | 名前 | 実装 |
| --- | --- | --- |
| SetPropertyString | `pause` | `tcs_player_set_paused`（`yes/1/true/on` を true） |
| SetPropertyString | `volume` | `tcs_player_set_volume`（InvariantCulture で解析） |
| SetPropertyString | `mute` | `tcs_player_set_mute` |
| SetPropertyString | `speed` | `tcs_player_set_speed`（InvariantCulture、>0 のみ） |
| GetProperty | `time-pos` / `duration` / `container-fps` | `TryGetTimePos` / `TryGetDuration` / `TryGetFps`（未知は -1） |
| GetPropertyString | `path` / `width` / `height` / `video-codec` / `seeking` / `pause` | ネイティブ取得＋`seeking` は到着数ベースの自前判定 |
| GetPropertyString | `audio-codec` | 常に空文字（shim に問い合わせが無い） |

### 3-3. 解釈されず 0 / 空 / -1 を返すもの

| 呼び出し | 現在の結果 | 対象 |
| --- | --- | --- |
| SetPropertyString | 0（無視） | `vo`、`hwdec`、`keep-open`、`osd-level`、`osd-font-size`、`osd-bar`、`osd-color`、`osd-border-color`、`osd-border-size`、`osd-msg3`（計 12 か所） |
| GetProperty | -1 | 上記 3 つ以外の名前 |
| GetPropertyString | 空文字 | 上記 6 つ以外の名前 |
| CommandString | 0（無視） | `loadfile` / `seek` / `stop` / `frame-step` 以外（デバッグログ 1 行） |

`vo` / `hwdec` / `keep-open` は起動時に毎回送られ、すべて無視される。`pause=yes` だけが実効（`MpvStartupPropertyApplier` の役割は実質これ 1 つ）。
デバッグ OSD（`osd-msg3`、`osd-level=3`）は GStreamer 構成では表示先が無く、現在の出荷構成では画面に出ない。

### 3-4. 戻り値の落とし穴（現行実装の事実）

| 箇所 | 事実 |
| --- | --- |
| `GstMpvApiAdapter.CommandString` の `seek` | shim の `Seek` が失敗しても **0 を返す**（`_seekPending` を立てるだけ） |
| `GstMpvApiAdapter.CommandString` の `frame-step` | 常に 0 |
| 未知コマンド | 0 を返す（呼び出し側は成功と区別できない） |
| `MainWindow.IPlaybackController.SeekRelative` | コマンド文字列の数値書式に `InvariantCulture` が無い（計画 1-5 の実害例） |

---

## 4. 型付き API の案

### 4-1. メソッド案と対応（最小限）

| メソッド案 | 現在の呼び出し | 実装先（既存の型付き層） | 失敗の表現案 | スレッド |
| --- | --- | --- | --- | --- |
| `Load(string path, double? startSeconds, bool paused)` | `no-osd loadfile ...`（5 か所） | `IGstNativeApi.Load`（`out string error`） | 結果型（rc＋error） | UI |
| `Seek(double seconds)` | `[no-osd] seek X absolute+exact` | `IGstNativeApi.Seek` | 結果型 | UI |
| `SeekRelative(double deltaSeconds)` | `seek X relative+exact` | 現状はアダプタが `time-pos` で絶対値へ変換してから `Native.Seek` | 結果型 | UI |
| `Stop()` | `stop` | `IGstNativeApi.Stop` | 結果型（rc 無視可） | UI |
| `StepFrame()` | `frame-step`（未使用） | `IGstNativeApi.StepFrame` | 結果型 | UI |
| `SetPaused(bool)` | `pause=yes/no`（15 か所） | `IGstNativeApi.SetPaused` | 結果型 | UI |
| `SetRate(double)` | `speed=X`（UI の速度切替） | `IGstNativeApi.SetSpeed` | 結果型 | UI |
| `SetRateInstant(double)` | `SetRateInstant`（T5 Smooth） | `IGstNativeApi.SetRateInstant` | 結果型 | UI |
| `SetVolume(double 0-100)` / `SetMute(bool)` | `volume` / `mute` | `IGstNativeApi.SetVolume` / `SetMute` | 結果型 | UI |
| `TryGetTimePos(out double)` | `GetProperty time-pos`（8 か所） | `IGstNativeApi.TryGetTimePos` | bool（現在と同じ） | UI（段 3 後。以前は snapshot スレッドからも） |
| `TryGetDuration(out double)` | `GetProperty duration`（2 か所） | `IGstNativeApi.TryGetDuration` | bool | UI |
| `TryGetFps(out double)` | `GetProperty container-fps` | `IGstNativeApi.TryGetFps` | bool | UI |
| `GetPath()` | `GetPropertyString path`（2 か所） | `IGstNativeApi.GetPath` | string（空 = 不明） | UI |
| `TryGetSize(out int w, out int h)` | `width` / `height` | `IGstNativeApi.TryGetSize` | bool | UI |
| `GetVideoCodec()` | `video-codec` | `IGstNativeApi.DecoderName` | string | UI |
| `IsPaused()` / `IsSeeking()` | `pause` / `seeking` | `IsPaused` / 到着数ベースの自前判定 | bool | UI |
| セッション `Create` / `Initialize` / `TerminateDestroy` | 同左 | `GstBackendState.EnsurePlayer` / `DisposePlayer` | 結果型（失敗理由つき） | UI |
| （任意）`SetDecodeMode` | 現在は `GstBackendState.ApplyDecodeMode` がネイティブ直呼び | `IGstNativeApi.SetDecodeMode` | 結果型 | UI（起動時 1 回） |

- `seeking` は shim のプロパティではなく、`GstMpvApiAdapter` の「シーク発行時の到着数を基準に、新位置のフレームが 1 枚届くまで yes」という自前の意味論。型付き API でもこの意味論をどこに置くかを決める必要がある（アダプタ内に残す / shim に降ろす）。
- `pause` は `GstMpvApiAdapter` がミラー（`GstBackendState.IsPaused`）を持ち、`Load` の初期状態に使う。`Load(path, start, paused)` にすればミラー依存は減るが、`IsPaused()` の読み取りは必要。
- `LoadFile` は「位置つきロードで mpv の EOF pause を引き継ぐ」等の現行コメント上の事情を `Load(path, start, paused)` の引数で明示できる。
- `frame-step` はアプリ未使用（テストのみ）。

### 4-2. 失敗の表現（選択肢）

| 案 | 形 | 根拠 | 影響 |
| --- | --- | --- | --- |
| (a) int rc のまま | `int Load(...)` | 呼び出し側の `rc == 0` を変えずに済む | 未知コマンドの「黙って 0」を排除できない。失敗理由が無い |
| (b) bool＋out error | `bool Load(..., out string error)` | shim の既存契約（`IGstNativeApi`）と同じ形 | 呼び出し側の変更は小さい（`rc == 0` → `bool`） |
| (c) 結果型 | `PlaybackCommandResult(bool Success, string Error)` | ログ・診断に理由を残せる。void 操作にも同じ形 | 呼び出し側で `Success` を読む/捨てる判断が要る |

例外は現行の全経路（未知コマンド 0、シーク失敗 0、pause 無視）と意味が変わるため、選択肢に含めるなら影響範囲の列挙が要る。

### 4-3. スレッド

| 層 | 現状 | 段 3 後に想定 |
| --- | --- | --- |
| 再生操作（Load/Seek/Set*） | UI スレッドのみ | UI のみ |
| 取得（time-pos 等） | UI。`ReadMpvTimePos` は mpv の snapshot スレッドからも呼ばれた（mpv 経路は段 2 で削除済み、現在の設定箇所なし） | UI のみ |
| フレーム通知 | shim コールバック → `RenderSession` が UI にスケジュール | 同じ（`RenderUpdateFn` の契約は不変） |

---

## 5. 消えるもの

| 対象 | 理由 |
| --- | --- |
| `MpvPlaybackCommandBuilder`（＋`MpvPlaybackCommandBuilderTests`） | loadfile 文字列の生成が不要 |
| `GstCommandTranslator`（＋`GstCommandTranslatorTests`） | 文字列の再解析が不要 |
| `MpvStartupPropertyApplier`（＋`MpvStartupPropertyApplierTests`） | 実効が `pause=yes` のみ。残りは `vo`/`hwdec`/`keep-open`/osd の no-op |
| `MpvSessionInitializer` の mpv 前提の役割 | 中立な初期化（`PlaybackSessionInitializer` 相当）へ。失敗 enum/result も改名 |
| `MpvRenderFrameExecutor` / `MpvRenderFrameResult`（＋テスト） | 段 3 でスナップショット経路ごと消える（段 4 より前） |
| `IMpvApi.Free` / `FormatDouble` | 未使用（`FormatDouble` は `GetProperty` の引数専用） |
| 定数 `MpvValueYes` / `MpvValueNo` / `MpvSeekModeAbsolute` / `MpvSeekModeRelative` / `MpvCommandNoOsd` / `MpvPropertyOsd*` | 文字列文法の消滅 |
| `Mpv` 名のコメント・ログ・パラメータ名（`_mpv`、`mpvApi`、`initializeMpvSession`、`ReadMpvTimePos` など） | 改名対象（機械的） |
| `osd-*` / `vo` / `hwdec` / `keep-open` の呼び出し 12 か所 | 型付き API に相当メソッドが無いため自然消滅 |

## 6. 残る名前の中立案（役割で命名）

| 現在 | 役割 | 中立名の案（候補） |
| --- | --- | --- |
| `IMpvApi` | 再生操作と状態取得の境界 | `IPlaybackApi` / `IPlaybackEngine` / `IMediaPlaybackApi` |
| `IMpvRenderApi` | フレーム更新通知と context 寿命 | `IRenderUpdateSource` / `IFrameUpdateSource` / `IRenderContextApi` |
| `GstMpvApiAdapter` | `IPlaybackApi` の GStreamer 実装 | `GstPlaybackApi` / `GstPlaybackEngine` |
| `GstMpvRenderApiAdapter` | `IRenderUpdateSource` の GStreamer 実装 | `GstRenderUpdateSource` |
| `MpvSessionInitializer` / `MpvSessionInitialization*` | セッション作成と失敗理由 | `PlaybackSessionInitializer` / `PlaybackSessionInitialization*` |
| `MpvStartupPropertyApplier` | 起動プロパティ | 消滅（`pause=yes` はセッション初期化へ） |
| `MpvRenderFrameExecutor` | レンダー呼び出しの計測ラッパ | 消滅（段 3） |
| `RenderParam` / `RenderUpdateFn` | レンダー境界の構造体とコールバック | 変更なし（既に中立） |
| `GstBackendState` / `GstNativeApi` / `IGstNativeApi` | shim 所有者と薄いラッパ | 変更なし |
| `Mpv` を含むフィールド名・引数名・ログ | - | `player` / `playbackApi` / `renderUpdateSource` など |

命名の制約: 既存 `IPlaybackController`（MainWindow が実装）と衝突しないこと。`PlayerBackend` / `OutputBackend.Cpu` は段 2・3 で消える。

---

## 7. 順序とリスク

### 7-1. 移行順序の選択肢

| 案 | 内容 | 根拠 | リスク |
| --- | --- | --- | --- |
| 一括 | 型付き API を定義し、全 66 か所を同時に切り替え、translator/builder/startup を削除 | 往復経路が 1 コミットで消える。E2E 1 回で両方向を確認できる | 差分が大きく、失敗時に切り分けにくい |
| 段階（呼び出し側ごと） | (1) `GapPlaybackCommandExecutor` / `GapFreezePathGuard`（load/pause/seek の一部）、(2) `PlaybackOperationsCoordinator` と `AudioControlCoordinator`、(3) `MainWindow` の読み取り系、(4) セッション初期化と削除 | 各段で E2E/非 E2E を回せる。ギャップ系（V3 の freeze-sweep）を早い段で検証できる | 一時的に 2 経路が併存し、`seeking` など状態の二重管理が要る |
| 逆順（削除先行） | 使われない osd 系を先に消し、translator の解釈範囲を縮めてから型付き化 | 消える呼び出しが減ると差分が小さい | 段 3 と衝突しやすい。今は該当 12 か所が no-op として動いているだけなので急ぐ理由が薄い |

### 7-2. E2E / V3 で守れる範囲

| 経路 | 検証 | 内容 |
| --- | --- | --- |
| load / loadfile（位置つき・なし） | E2E 全件、V3 | トラック切替、ギャップの前トラック最終フレーム |
| seek（絶対・sync 補正・シークバー） | E2E 全件、V3 | `seeking` の意味論、ロード安定ゲート |
| pause / 信号ロス / ギャップ | E2E 全件、V3 freeze-sweep | `pause` ミラーと `PauseForGap` |
| rate（Smooth / Jump / 速度切替） | V3（Smooth）、E2E | `SetRateInstant` と `speed` の区別 |
| volume / mute | 非 E2E（AudioControl 系） | 戻り値は元から無視 |
| 相対シーク | 非 E2E（`MainWindowManualSeekTests`） | 文字列記録を型付き呼び出し記録へ更新 |

### 7-3. E2E / V3 で守れないもの

| 項目 | 現状の事実 |
| --- | --- |
| `frame-step` | アプリから未使用（テストのみ） |
| `osd-*` / `vo` / `hwdec` / `keep-open` / `osd-msg3` | 元から無視。デバッグ OSD は GStreamer 構成で表示されない |
| `Free` / `FormatDouble` | 未使用 |
| `GetPropertyString("audio-codec")` | 常に空 |
| 未知コマンドの 0 戻り | 型付き API 化で「呼べない」ようになる（挙動の差は無いが、ログの `未対応コマンドを無視` は消える） |

### 7-4. 現行の既知の欠陥（型付き API で同時に直る候補）

| 欠陥 | 根拠 |
| --- | --- |
| 相対シークの数値書式がカルチャ依存 | `MainWindow.IPlaybackController.SeekRelative` の文字列補間（`InvariantCulture` なし） |
| `seek` が shim 失敗でも 0 を返す | `GstMpvApiAdapter.CommandString` の `GstSeekOperation` 分岐 |
| 未知の名前・コマンドが黙って成功扱い | SetPropertyString 0 / CommandString 0 |

### 7-5. 失敗表現を変える場合の影響

- `pause` / `volume` / `mute` / `stop` / `frame-step` の戻り値は現在すべて無視されている（`GapFreezePathGuard` 等が記録に使う `load` / `pause` を除く）。
- 結果型にする場合、無視してよい呼び出しに `_ =` を付けるか、戻り値なしの別メソッドにするかの選択が生じる。

---

## 8. 段 3（CPU 合成の除去）との衝突

段 3 は `agent-b` で進行中。段 4 は段 3 の後に始める前提で、同じファイル・同じメンバーに触る箇所を列挙する。

| ファイル / 型 | 段 3 の変更 | 段 4 で必要な変更 | 衝突の内容 |
| --- | --- | --- | --- |
| `RenderSession.cs` | スナップショット経路（`RenderFrameWorker`、`MpvRenderFrameExecutor`、`RenderFrameParameterBuilder`、`RenderedFrameSnapshot`、フリーズバッファ）を削除。フレーム通知の駆動と寿命は残す | `IMpvRenderApi` の改名、`RenderContextRender` 呼び出しの扱い、`ReadMpvTimePos` 参照の整理 | 同じメソッド群（`Create` / `DrainNativeUpdatesAsync` / `FreeContext`）に両者が触る。**段 3 の削除後の形を見てから着手** |
| `Gst/GstMpvRenderApiAdapter.cs` | `RenderContextRender`（`RenderInto` 経由の CPU コピー）を削除 | クラス名・メンバー名の改名、interface 形の再定義 | 同じファイル。段 3 で `RenderContextRender` が消えると、`IMpvRenderApi` に残るのは context 寿命と更新通知だけになる |
| `Gst/GstBackendState.cs` | `RenderInto` を削除（`LeasedCpuCopy` 経路） | セッション生成をここへ寄せる場合、`IMpvApi.Create/Initialize/TerminateDestroy` の行き先になる | 同じファイル。`Attach/DetachRenderCallback`・`ConsumeUpdate` は両段で使う |
| `Gst/IGstNativeApi.cs` / `GstNativeApi.cs` / `GstNative.cs` / shim | `LeasedCpuCopy` と `tcs_player_leased_cpu_copy` を C ABI ごと削除（I13 管轄） | 型付き API は既存メソッドで足りる想定。追加が要る場合だけここに触る | 段 4 が shim に相対シークや `seeking` を降ろす選択をした場合に衝突 |
| `MainWindow.xaml.cs` | CPU プレビュー・全画面の `Image` 経路・`FrameRenderer` 系の整理で触れる可能性 | 呼び出し 34 か所の書き換え、`_mpv`/`ReadMpvTimePos` の改名 | 同じファイルで広範囲。段 3 の差分確定後に着手 |
| `App.xaml.cs` DI | CPU Spout 系の整理 | `GstMpvApiAdapter` / `GstMpvRenderApiAdapter` / `IMpvApi` / `IMpvRenderApi` / `MpvStartupPropertyApplier` / `MpvSessionInitializer` の登録変更 | 登録の並びが近い |
| `GapPlaybackCommandExecutor.cs` / `GapFreezePathGuard.cs` / `PlaybackOperationsCoordinator.cs` / `AudioControlCoordinator.cs` | 直接の変更なし（計画上） | 型付き API への置換 | 段 3 とは独立。先行して移せる候補 |
| テスト | `GstBackendAdapterTests`（render adapter 部）、`RenderSessionTests` の分割、`RenderContextParameterBuilderTests` / `RenderFrameParameterBuilderTests` / `PixelBufferManager` 系 | 同ファイルの fake 更新、`Mpv*` テストの改名・削除 | 同じテストファイルに両段が触れる |

段 3 の完了条件（grep 0 件）のうち段 4 に関係するもの: `FrameRenderer`、`RenderFrameWorker`、`RenderedFrameSnapshot`、`LeasedCpuCopy`、`tcs_player_leased_cpu_copy`。段 4 の完了条件は「型名・ファイル名の `Mpv` が 0 件」。

### 8-1. 段 4 着手前の確認事項（段 3 の結果に依存）

- 段 3 後も `RenderContextCreate` / `RenderContextUpdate` / `RenderContextSetUpdateCallback` / `RenderContextFree` が `IMpvRenderApi` に残るか
- `RenderContextParameterBuilder` / `RenderContextParameterBuilderTests` が残るか（API 種別 `"sw"` の宣言が不要になる可能性）
- `PixelBufferManager` / `StartupBufferInitializer` を GPU プレビュー読み戻しが使うかの対応表（段 3 の決定 4 の報告）

---

## 9. 判断が必要な事項（材料のみ）

1. `osd-msg3` のデバッグ OSD は GStreamer 構成で表示先が無い。型付き API への移行で消えるが、デバッグ表示を WPF 側へ移すかは未決
2. `StepFrame` を型付き API に残すか（アプリ未使用、テストのみ）
3. `seeking` の意味論（到着数ベース）をアダプタ内に残すか、shim のプロパティへ降ろすか
4. `SetDecodeMode`（設定 `decodeMode`）を再生 API に含めるか、`GstBackendState` の初期化のままにするか
5. 相対シークを型付き API のクライアント計算のままにするか、shim に相対シークを追加するか
6. 失敗表現を int rc / bool＋error / 結果型のどれにするか（7-5 の影響）
7. 移行を一括にするか段階にするか（7-1）
