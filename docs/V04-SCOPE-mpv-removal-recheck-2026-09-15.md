# mpv 除去棚卸しの再検証（対象: main `101a2ab`）

検証日: 2026-09-15。棚卸し本体は `docs/V04-SCOPE-mpv-removal-decode-mode.md` の
「コミット A の棚卸し」（`fc1ee84`、同日午前）。その後の変更（T5 同期補正、decodeMode、
4K 色変換、T4、T3 など `fc1ee84..main` の 24 コミット）を踏まえ、棚卸しが今も正しいかを確認した。

方法: `git grep` とバージョン間のファイル集合比較のみ。ビルド・テスト・実機は未使用。
比較基準: `fc1ee84`（棚卸し時点）と `main`（`101a2ab`）。

## 結論

1. **製品コードに新しく mpv を参照する箇所は無い。** mpv を参照する src ファイルは 41 → 41 で同一、
   ファイル集合の差もゼロ。新規追加ファイル（`DecodeModePolicy` / `SeekLatencyCompensator` /
   `SyncCorrectionController` とそのテスト）は mpv 参照ゼロ。
2. **新しく生まれた依存も無い。** 削除対象シンボルの消費者集合は `fc1ee84` と同一
   （`SnapshotInputMailbox` / `RenderedFrameSnapshot` / `SubmitFrame` / `WaitUntilOrStopOrSignal` /
   `CreateMpvSnapshotSource` / `UploadPendingSnapshot` などすべて差分ゼロ）。
   ただし、棚卸しが「消す」側に入れている 3 ファイルには**現役経路からの参照**があり、
   この分類のまま削除すると GStreamer 経路が壊れる（依存自体は新規ではない）。
3. **「消すもの」のうち `scripts/get-mpv.ps1` は既に存在しない**（P1 パッケージ作業で削除済み）。
   `native/libmpv-2.dll` は `fc1ee84` 時点でも git 管理外で、「同梱を消す」の実体は csproj の
   Content Include 4 行として残っている。mpv 実装 10 ファイルはすべて現存する。

## 1. 新しく mpv を参照するようになった箇所

- src: **41 ファイル（`fc1ee84`）→ 41 ファイル（`main`）**。ファイル集合の差はゼロ。
- `fc1ee84` 以降に追加された src ファイルは 3 本で、いずれも mpv 参照なし:
  `DecodeModePolicy.cs` / `SeekLatencyCompensator.cs` / `SyncCorrectionController.cs`
- mpv の一致数が増えたファイルは 2 本だけ:
  - `MainWindow.xaml.cs` +2:
    T5 の補正配線 `GetPlaybackSeconds: () => ReadMpvTimePos()` と
    `ApplyRateInstant: rate => _mpvApi.SetRateInstant(_mpv, rate) == 0`。
    参照先は「維持してコミット B で改名する」`IMpvApi` 側で、実 mpv への依存ではない。
  - `Gst/GstMpvApiAdapter.cs` +1: `SetRateInstant` 失敗時の `Log.Error` 行（GStreamer 実装側）。
- tests: **37 → 37** でファイル集合も同一。新規テスト（`SeekLatencyCompensatorTests` /
  `SyncCorrectionControllerTests` / `DecodeModePolicyTests` / `GstRootResolutionTests` など）に
  mpv 参照は無い。
- 製品コード外:
  - `scripts/package-release.ps1`: Release 出力への mpv 混入ガード（意図的な検査。削除対象ではない）
  - `scripts/analyze-v3-spread-stages.py`: `backend=1` のとき `mpv.frame` を読む分岐（解析ツール）

## 2. 消すと壊れる依存（分類の見直しが必要な 3 ファイル）

依存は `fc1ee84` 時点から存在し、**新たに生まれたものではない**。ただし棚卸しの
「消すもの: mpv の実装本体」に、現役の GStreamer 経路から参照されるファイルが 3 つ含まれている。

| ファイル | 現役の参照元（`main`） | そのまま削除すると |
| --- | --- | --- |
| `MpvRenderNative.cs` | `Contracts/IMpvRenderApi.cs`（契約の型として）、`Gst/GstMpvRenderApiAdapter.cs`、`Gst/GstBackendState.cs`、`RenderContextParameterBuilder.cs`、`RenderFrameParameterBuilder.cs`、`RenderSession.cs` | mpv 実装の下請けではなく、**維持する GStreamer レンダー抽象の型定義**（`MpvRenderParam` / `MpvRenderUpdateFn`）。削除で GStreamer 経路がコンパイル不能 |
| `MpvRenderFrameExecutor.cs` | `RenderSession.cs`（`new MpvRenderFrameExecutor(RenderNativeFrame)`） | `RenderSession` は App DI と MainWindow でバックエンドに関わらず生成される。削除するなら RenderSession 側の置換（中立名への改名かインライン化）が同コミットで必要 |
| `MpvPlaybackCommandBuilder.cs` | `PlaybackOperationsCoordinator.cs`、`GapFreezePathGuard.cs`、`GapPlaybackCommandExecutor.cs`（ほかに `GstCommandTranslator` のコメント参照） | 生成した `loadfile ...` 文字列を `GstMpvApiAdapter` → `GstCommandTranslator` が解釈する**共有経路**。削除するなら文字列生成の移設（中立ヘルパー化）が必要 |

関連して、棚卸しに現状の対応が曖昧なもの:

- **`RenderSession` / `RenderFrameWorker` / `RenderFrameParameterBuilder` / `RenderContextParameterBuilder`**:
  名前に mpv を含まないため棚卸しの一覧に無いが、`IMpvRenderApi` と `MpvRenderNative` に依存し、
  `RenderSession` は両バックエンドで生成される。CPU 合成の除去と一緒に落とすのか、中立名で残すのかを
  棚卸しに明記した方がよい（「`FrameRenderer` の mpv 経路」がこれを指している可能性が高いが、
  現在その名前のファイルは無い）。
- **`RenderedFrameSnapshot`**: 非 mpv の消費者が 2 つある（`OutputFrame.cs`、`RenderSession.cs`）。
  `fc1ee84` から変わっていないため、棚卸しの「他に利用者が無ければ消す」は現状そのままでは適用不可。
  CPU 合成 / RenderSession の扱いとセットで判断が必要。
- **`SnapshotInputMailbox`**: 参照は `Output/MpvSnapshotSource.cs` と `Output/OutputEngine.cs` のみで不変。
  OutputEngine 側の使用 5 か所を消せば型ごと消せる。
- **`SubmitFrame` / `WaitUntilOrStopOrSignal`**: 参照ファイルは `fc1ee84` と同一
  （`MainWindow` + `OutputEngine` / `OutputEngine` + `VblankWaitTimer`）。
- **行番号**: 棚卸しの行番号は `fc1ee84` 時点のもので、`main` では T5 などの追加でずれている
  （例: `OutputEngine.cs` の mpv 参照は 23 箇所に増えている）。場所ではなくシンボルで探すのが安全。

## 3. 棚卸しにあり、現在存在しないファイル

- `scripts/get-mpv.ps1`: **`fc1ee84` に存在、`main` には無い**（P1 パッケージ作業で削除済み）。
  この「消す」項目は完了済み。
- `native/libmpv-2.dll`: `fc1ee84` でも `main` でも git 管理外（未コミットの外部 DLL）。
  「同梱を消す」の実体は `src/TimecodeSyncPlayer/TimecodeSyncPlayer.csproj` の
  Content Include 4 行（`native\mpv-2.dll` / `native\libmpv-2.dll` と `Link` 名）で、これは残っている。
- mpv 実装 10 ファイル（`Mpv.cs` / `MpvApi.cs` / `MpvRenderApi.cs` / `MpvRenderNative.cs` /
  `MpvLibraryNameResolver.cs` / `MpvSessionInitializer.cs` / `MpvStartupPropertyApplier.cs` /
  `MpvRenderFrameExecutor.cs` / `MpvPlaybackCommandBuilder.cs` / `Output/MpvSnapshotSource.cs`）:
  **すべて `main` に存在**。
- 周辺の整理状況: `scripts/installer.iss` の mpv 記述 0、`native/README.md` 0（整理済み）。
  `docs/SETUP.md` は 2 箇所（`backend` 表と組み合わせ説明）が残存。
  `scripts/package-release.ps1` は混入ガード 6 行が残存（コミット A 後も安全網として残せる）。
- mpv 参照テスト 37 ファイルは現存（`Mpv*Tests` 7 本を含む）。
- E2E に mpv バックエンド実行は残っていない（E2E の mpv の語は 2 つのコメントのみ）。
  `PlayerBackend` を参照するテストは `AppSettingsTests` の 4 箇所だけ（既定値と roundtrip の確認）。

## 棚卸しの確認方法への提案

「コミット A 後に grep して残るのは 4 ファイルだけ」という確認方法は、上記 2 の 3 ファイルを
改名/移設する進め方を取る場合に成立しない。確認は次の形がよい。

- 残存を許すファイルを「GStreamer 側の実装/抽象（中立名へ改名予定のもの）」として列挙し、
  **実 mpv ライブラリをロードする経路（`MpvApi` / `MpvRenderNative` の P/Invoke 本体 /
  `libmpv-2.dll` 参照 / `PlayerBackend.Mpv` 分岐）が 1 つも残っていないこと**を個別に確認する。
- `WaitUntilOrStopOrSignal` の signal 引数が消えていること、`OutputEngine` から
  `mpvSource` / `snapshotInput` / `UploadPendingSnapshot` / `CreateMpvSnapshotSource` が
  grep で 0 件であることは従来どおり個別確認する。
- 行番号ではなくシンボルで確認する。

## 証跡（読み取りのみ）

- `git grep -i -l mpv fc1ee84 -- src` / `main -- src`（41 対 41、集合差ゼロ）
- `git diff fc1ee84..main -- src tests scripts` の追加行（mpv の新規参照は上記のとおり）
- シンボル別ファイル集合比較（`MpvRenderNative` / `MpvRenderFrameExecutor` /
  `MpvPlaybackCommandBuilder` / `SnapshotInputMailbox` / `RenderedFrameSnapshot` ほか）
- `git cat-file -e` による削除対象ファイルの存在確認
