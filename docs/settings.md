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

フルスクリーン出力中に出力モニターへマウスを載せると、ESCで閉じられるよう出力ウィンドウへフォーカスが移ります。

Enum values are serialized as JSON numbers by the current application. The exact mappings are:

| JSON key | Numeric mapping |
| --- | --- |
| `syncMode` | `0` = `Single`, `1` = `Continue` |
| `gapBehavior` | `0` = `Freeze`, `1` = `Black` (the UI list is displayed in the opposite order) |
| `timecodeFpsMode` | `0` = `Auto`, `1` = `Fixed24`, `2` = `Fixed25`, `3` = `Fixed29_97`, `4` = `Fixed30` |
| `ltcSignalLossMode` | `0` = `RunThrough`, `1` = `Stop` |

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
値を読み込むため、アプリの再起動が必要です。
