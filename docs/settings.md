# Settings reference / 設定リファレンス

TimecodeSyncPlayer stores per-user settings as JSON at
`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`. For isolated automation, the
`TIMECODE_SYNC_PLAYER_SETTINGS_PATH` environment variable can override this with an absolute or
relative file path. Relative override paths are resolved against the process working directory.

TimecodeSyncPlayerはユーザーごとの設定を
`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`へJSONで保存します。自動テストなどで隔離する
場合は、環境変数`TIMECODE_SYNC_PLAYER_SETTINGS_PATH`に絶対または相対ファイルパスを指定できます。
相対パスはプロセスの作業ディレクトリを基準に絶対化されます。

The installer intentionally retains this settings file during uninstall so that a later reinstall
can restore the user's preferences. Delete it manually to remove the preferences completely.

インストーラーは再インストール時に設定を復元できるよう、アンインストール時もこのファイルを
意図的に保持します。設定も完全に削除したい場合は手動で削除してください。

## Keys / キー一覧

| JSON key | Type | Default | Values and validation / 値・検証 |
| --- | --- | --- | --- |
| `syncMode` | enum | `Single` | `Single` or `Continue` / 同期モード |
| `gapBehavior` | enum | `Freeze` | `Freeze` or `Black` / タイムコードギャップ中の表示 |
| `timecodeFpsMode` | enum | `Auto` | `Auto`, `Fixed24`, `Fixed25`, `Fixed29_97`, or `Fixed30` |
| `lastOpenedProjectPath` | string | `""` | Updated only after a successful project load or save; informational and never auto-opened at startup / プロジェクトの読込・保存成功時のみ更新。起動時の自動読込には使用しない |
| `ltcDeviceName` | string | `""` | Capture-device name restored at startup. A missing name falls back to the first enumerated device and is logged. Legacy `ltcDeviceIndex` is ignored / 起動時に名前で復元。見つからない場合は先頭へフォールバックしてログ記録。旧`ltcDeviceIndex`は無視 |
| `windowLeft` | number or null | `null` | Valid range `-7680` to `7680`; otherwise `null` / 範囲外は`null` |
| `windowTop` | number or null | `null` | Valid range `-4320` to `4320`; otherwise `null` / 範囲外は`null` |
| `windowWidth` | number or null | `null` | Greater than `0`, at most `7680`; otherwise `null` / `0`超～`7680`、範囲外は`null` |
| `windowHeight` | number or null | `null` | Greater than `0`, at most `4320`; otherwise `null` / `0`超～`4320`、範囲外は`null` |
| `isTimelineVisible` | boolean | `false` | Timeline panel visibility / タイムラインパネル表示 |
| `autoOffsetOnAdd` | boolean | `true` | Automatically calculate offsets for added clips / 追加クリップのオフセット自動計算 |
| `isMuted` | boolean | `false` | Audio mute state; restored at startup / 音声ミュート状態。起動時に復元 |
| `volume` | number | `100` | Player volume, clamped to `0`–`100`; retained while muted / プレイヤー音量。`0`～`100`へクランプし、ミュート中も保持 |
| `ltcSignalLossMode` | enum | `RunThrough` | `RunThrough` or `Stop`; unknown values reset to `RunThrough` / 不明値は`RunThrough` |
| `ltcSignalLossTimeoutMs` | integer | `250` | Clamped to `100`–`5000` ms / `100`～`5000`msへクランプ |
| `ltcSignalResumeFrames` | integer | `5` | Must be greater than `0`; otherwise resets to `5` / `0`以下は`5`へ補正 |
| `showDebugOsd` | boolean | `false` | Shows playback time and media metadata over the video when `true`; restart required / `true` で再生時刻とメディア情報を映像上に表示。変更後は再起動が必要 |
| `fullscreenDisplayDeviceName` | string | `""` | Device name used for fullscreen output; a missing device falls back to the primary display / フルスクリーン出力先のデバイス名。見つからない場合はプライマリへフォールバック |
| `syncOffsetMs` | number | `0` | Global sync offset in ms, clamped to `-1000`–`1000`. Positive makes the picture lead, compensating input-side and downstream (LED wall etc.) latency together. Editable in the UI / 全体の同期オフセット（ms）。`-1000`～`1000`へクランプ。プラスで映像が先行し、入力側と下流（LED 等）の遅延をまとめて補正する。UI から変更可 |
| `syncCorrectionMode` | enum | `Smooth` | `Smooth` closes small drift by nudging the playback rate; `Jump` seeks when the drift exceeds a threshold. Measured on field material, `Jump` does not reduce overshoot and adds up to ~1 s of frozen picture, so keep `Smooth` unless told otherwise / `Smooth` は再生速度を少し変えて詰める。`Jump` はしきい値を超えたらシークする。実素材の測定では `Jump` にしても行き過ぎは減らず、止まる時間が最大 1 秒ほど増えるため、特に理由が無ければ `Smooth` のまま |
| `decodeMode` | string | `"hardware"` | `"hardware"` (GPU decode, default) or `"software"`; any other value is treated as `"hardware"` with a warning. Restart required / `"hardware"`（GPU デコード、既定）または `"software"`。それ以外は警告して `"hardware"` 扱い。変更には再起動が必要 |
| `outputBackend` | enum | `Gpu` | Only `Gpu` (`1`) exists since v0.4. The v0.3 value `0` (CPU compositing) is ignored with a warning and the file is not rewritten / v0.4 以降は `Gpu`（`1`）のみ。v0.3 の `0`（CPU 合成）は警告して無視し、ファイルは書き換えない |

フルスクリーン出力中に出力モニターへマウスを載せると、ESCで閉じられるよう出力ウィンドウへフォーカスが移ります。

Enum values are serialized as JSON numbers by the current application. The exact mappings are:

| JSON key | Numeric mapping |
| --- | --- |
| `syncMode` | `0` = `Single`, `1` = `Continue` |
| `gapBehavior` | `0` = `Freeze`, `1` = `Black` (the UI list is displayed in the opposite order) |
| `timecodeFpsMode` | `0` = `Auto`, `1` = `Fixed24`, `2` = `Fixed25`, `3` = `Fixed29_97`, `4` = `Fixed30` |
| `ltcSignalLossMode` | `0` = `RunThrough`, `1` = `Stop` |
| `syncCorrectionMode` | `0` = `Smooth`, `1` = `Jump` |
| `outputBackend` | `1` = `Gpu` |

`syncMode` and `gapBehavior` are saved immediately when changed and restored into the UI at
startup. Their defaults are `Single` and `Freeze`, matching `AppSettings.Default` and the initial UI.

現在のアプリはenum値をJSON数値として保存します。上表のシンボル名は意味とUI表示を示します。
可能な限り、アプリ自身が書き出した設定を利用してください。

## When changes take effect / 反映タイミング

Most settings are applied when they are changed in the UI or loaded at startup. Changes made
directly to `ltcSignalLossTimeoutMs`, `ltcSignalResumeFrames`, or `showDebugOsd` require an application restart,
because the signal-loss policy reads them when the main window is created.

大半の設定はUIでの変更時または起動時に反映されます。`ltcSignalLossTimeoutMs`、
`ltcSignalResumeFrames`、`showDebugOsd`をファイル上で変更した場合は、メインウィンドウ生成時に
値を読み込むため、アプリの再起動が必要です。`decodeMode`も再起動が必要です。

## Environment variables / 環境変数

Set these before starting the app. Most exist for measurement; only `TCS_GOP_SCAN` is meant for field use.

起動前に設定します。ほとんどは計測用で、現場で使う想定のものは `TCS_GOP_SCAN` だけです。

| Variable | Default | Effect / 効果 |
| --- | --- | --- |
| `TCS_GOP_SCAN=off` | on | Skips the keyframe-interval scan done when a file is loaded. Use it to avoid the first full read of very large files (a 300 GB file is expected to take about 8 minutes). **The long-keyframe-interval warning is not shown while this is off** / 読み込み時のキーフレーム間隔の解析を止める。非常に大きい素材で初回の読み切りを避けたいときに使う（300GB で 8 分程度の見込み）。**止めている間は、キーフレーム間隔の警告も出ない** |
| `TCS_PUMP_BUDGET_MS` | `4000` | Upper bound (1–60000 ms) on waiting for the first frame after a seek while paused / 一時停止中のシークで最初のフレームを待つ上限（1～60000ms） |
| `TCS_SEEK_COST_HINT=on` | off | Measurement only. Aims the follow-start seek ahead by an estimate derived from the keyframe interval. Off by default because it broke other cases; **do not use in the field** / 計測用。追従開始のシークを、キーフレーム間隔から見積もったぶん先へ狙う。別の場面を壊したため既定で無効。**現場では使わない** |
| `TCS_SYNC_POSITION_FEEDBACK=on` | off | Measurement only. While a seek has not been confirmed to have landed, derives the position used for sync decisions from the frame actually delivered / 計測用。シークの着地が確認できるまで、同期の判断に使う位置を実際に届いたフレームから求める |
| `TCS_LTC_SAMPLE_CLOCK=off` | on | Measurement only. Disables the correction that adds the time from the LTC frame end to its handling / 計測用。LTC フレームの終端から処理までの経過を足す補正を無効にする |
