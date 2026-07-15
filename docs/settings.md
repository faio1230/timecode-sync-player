# Settings reference / 設定リファレンス

TimecodeSyncPlayer stores per-user settings as JSON at
`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`. For isolated automation, the
`TIMECODE_SYNC_PLAYER_SETTINGS_PATH` environment variable can override this with an absolute or
relative file path. Relative override paths are resolved against the process working directory.

TimecodeSyncPlayerはユーザーごとの設定を
`%LOCALAPPDATA%\TimecodeSyncPlayer\settings.json`へJSONで保存します。自動テストなどで隔離する
場合は、環境変数`TIMECODE_SYNC_PLAYER_SETTINGS_PATH`に絶対または相対ファイルパスを指定できます。
相対パスはプロセスの作業ディレクトリを基準に絶対化されます。

## Keys / キー一覧

| JSON key | Type | Default | Values and validation / 値・検証 |
| --- | --- | --- | --- |
| `syncMode` | enum | `Single` | `Single` or `Continue` / 同期モード |
| `gapBehavior` | enum | `Freeze` | `Freeze` or `Black` / タイムコードギャップ中の表示 |
| `timecodeFpsMode` | enum | `Auto` | `Auto`, `Fixed24`, `Fixed25`, `Fixed29_97`, or `Fixed30` |
| `lastOpenedProjectPath` | string | `""` | Last project path / 最後に開いたプロジェクトパス |
| `ltcDeviceIndex` | integer | `-1` | `-1` means no selection; values below `-1` reset to `-1` / `-1`は未選択、未満は`-1`へ補正 |
| `windowLeft` | number or null | `null` | Valid range `-7680` to `7680`; otherwise `null` / 範囲外は`null` |
| `windowTop` | number or null | `null` | Valid range `-4320` to `4320`; otherwise `null` / 範囲外は`null` |
| `windowWidth` | number or null | `null` | Greater than `0`, at most `7680`; otherwise `null` / `0`超～`7680`、範囲外は`null` |
| `windowHeight` | number or null | `null` | Greater than `0`, at most `4320`; otherwise `null` / `0`超～`4320`、範囲外は`null` |
| `isTimelineVisible` | boolean | `false` | Timeline panel visibility / タイムラインパネル表示 |
| `autoOffsetOnAdd` | boolean | `true` | Automatically calculate offsets for added clips / 追加クリップのオフセット自動計算 |
| `ltcSignalLossMode` | enum | `RunThrough` | `RunThrough` or `Stop`; unknown values reset to `RunThrough` / 不明値は`RunThrough` |
| `ltcSignalLossTimeoutMs` | integer | `250` | Clamped to `100`–`5000` ms / `100`～`5000`msへクランプ |
| `ltcSignalResumeFrames` | integer | `5` | Must be greater than `0`; otherwise resets to `5` / `0`以下は`5`へ補正 |

Enum values are serialized as JSON numbers by the current application. The symbolic names above
describe their meaning and stable UI labels; use settings written by the application when possible.

現在のアプリはenum値をJSON数値として保存します。上表のシンボル名は意味とUI表示を示します。
可能な限り、アプリ自身が書き出した設定を利用してください。

## When changes take effect / 反映タイミング

Most settings are applied when they are changed in the UI or loaded at startup. Changes made
directly to `ltcSignalLossTimeoutMs` or `ltcSignalResumeFrames` require an application restart,
because the signal-loss policy reads them when the main window is created.

大半の設定はUIでの変更時または起動時に反映されます。`ltcSignalLossTimeoutMs`と
`ltcSignalResumeFrames`をファイル上で変更した場合は、信号断ポリシーがメインウィンドウ生成時に
値を読み込むため、アプリの再起動が必要です。
