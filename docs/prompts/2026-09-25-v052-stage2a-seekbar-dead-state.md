# v0.5.2 段 2a: SeekBarUpdateState の読まれない保留を消す（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`a59a53f` 以降）。**振る舞いは変えない。**
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §6 の 11 と §10。

## 事実（親が確かめた）

- `SeekBarUpdateState` のインスタンスの状態（`HasPendingSeek`・`TargetSeconds`・`MarkSeekSent`・`Clear`・`GetDisplayPosition`）は、
  製品コードでは `MainWindow` が `MarkSeekSent`（シークバーの確定）と `Clear`（読み込み後の初期化）で**書くだけ**で、読む側がいない
- 読むのは `tests/TimecodeSyncPlayer.Tests/SeekBarUpdateStateTests.cs` だけ
- static のヘルパー（`ToSliderValue`・`ToSliderValueFromPointer`・`IsUsableDuration`）は使われている

## やること

1. インスタンスの状態とメソッド、`Contracts/ISeekBarUpdateState.cs`、`App.xaml.cs` の DI 登録、`MainWindow` のフィールド・コンストラクタ引数・2 か所の呼び出しを消す
2. static のヘルパーは残す（クラスを `static` にしてよい。名前は変えない。呼び出し側を変えずに済むため）
3. `SeekBarUpdateStateTests.cs` のうち、消したメソッドのテストを消す。static のヘルパーのテストは残す
4. 同名の別物に注意: `TimecodeSyncSeekState`（同期のシーク保留）と `MainWindow` の `_seekBarInteraction` は**触らない**
5. 消す前に grep で読む側が無いことを自分でも確かめ、結果を報告に書く

## 判定

- ビルドの警告 0
- 非 E2E 全件合格（`--filter "FullyQualifiedName!~E2ETests"`）。段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま
- E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`。LTC シナリオは外す）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 終わったら、消したもの・grep の結果・テストの件数（非 E2E / E2E）をこの画面に書く
