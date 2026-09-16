# LTC 同期の検証行列（利用者の 26 項目、2026-09-17）

作成: 2026-09-17 09:30、親。利用者が挙げた検証項目を、既存の E2E（VB-CABLE ループ）で覆えている範囲と、新しく作る範囲に分ける。
実行環境: 開発機（GPU 1 枚）と検証機（ハイブリッド GPU、インストール済みアプリ、`docs/RELEASE-PROCEDURE-0.4.md` 1.5 節の手順）。
証跡の原則: **画面の状態は画素で判定する**（黒 = 黒画素の割合、フリーズ/冒頭フレーム = 素材に焼き込んだ色で同定）。ログの文言だけで合格にしない。

## 1. 行列

凡例: **済** = 既存テストで覆えている / **一部** = 近いテストはあるが判定が足りない / **新** = 新しく作る

| # | 項目 | 状態 | 既存の根拠 | 新テスト（3 節の ID） |
| --- | --- | --- | --- | --- |
| 1 | 映像を読み込ます | 済 | `GStreamerBackendE2ETests`（読み込み・再生進行）、`WorkflowE2ETests.LoadProjectFromCli` | — |
| 2 | LTC を再生する | 済 | `LtcHardwareLoopE2ETests.CableLoop_TimecodeProgressesMonotonically…`（CABLE Input へ送出、表示が進む） | — |
| 3 | 引っ掛かるかどうか | 済 | `CableLoop_SyncSeeksVideoNearLtcPosition`（同期シーク 1 回、位置が LTC 近傍） | — |
| 4 | 正常な FPS で再生されるか | 一部 | V3（公開フレームの計測）、V5/V6（60fps の公開）。LTC 追従中の fps を E2E で見るものは無い | S-1 |
| 5 | LTC でジャンプする | 済 | `SyncSeekResyncE2ETests`（ずらして戻る 26 回）、`RealProjectGap` の held 切替 | — |
| 6 | Single で LTC を飛ばして往復を何度も | 新 | 往復のストレスは無い | S-2 |
| 7 | Single で範囲外の LTC → その動画の終端 | 一部 | `RealProjectGap.single-eof-recovery`（EOF へ行って戻る。D11） | S-3（終端フレームの画素判定を足す） |
| 8 | Single で別のプレイリストの動画に切り替え | 一部 | `WorkflowE2ETests.DistinctPlaylist_NextPrevious…`（LTC なし） | S-4 |
| 9 | Single ではアクティブ以外は再生されない | 新 | 無い | S-5 |
| 10 | Continue の動作確認 | 済 | `RealProjectGap.boundary-*`（境界を連続通過、期待トラックに切替） | — |
| 11 | Continue で同じ動画内をジャンプ往復 | 新 | 無い（`SyncSeekResync` は UI シーク起点） | C-1 |
| 12 | Continue で別の動画とジャンプ往復 | 一部 | `RealProjectGap.held-switch`（held で切替 1 往復） | C-2 |
| 13 | Continue のギャップの動作確認 | 済 | `RealProjectGap`（Black ⇄ Freeze、trimmed Freeze）、V4 合格 | — |
| 14 | Continue、引っ掛けて Black 領域に入ったら黒 | 一部 | `RealProjectGap.AssertBlackImage`（境界通過時の黒判定） | G-1（画素の閾値を明示） |
| 15 | Continue、Black から動画に入って復帰 | 一部 | `RealProjectGap.recovery-*` | G-2 |
| 16 | Continue、ジャンプで Black 領域へ → 黒 | 一部 | `RealProjectGap.held-switch`（held で Black へ） | G-3 |
| 17 | Continue、ジャンプで Black から動画の途中へ → 復帰 | 一部 | 同上 | G-4 |
| 18 | Continue、Black を含むジャンプ往復のストレス | 新 | 無い | G-5 |
| 19 | Continue、最初の動画の前のオフセット領域は黒（Black） | 新 | 代替プロジェクトは 0:10 開始だが判定していない | G-6 |
| 20 | Continue、引っ掛けて Freeze 領域 → 前の動画の最終フレーム | 一部 | `RealProjectGap.visible-freeze`（frozen の判定。**最終フレームであることの画素判定は無い**） | F-1 |
| 21 | Continue、ジャンプで Freeze 領域 → 前の動画の最終フレーム | 一部 | 同上 | F-2 |
| 22 | Continue、Freeze → 別の Freeze へジャンプで held が更新 | 一部 | `held-switch-passed`（held の切替は見ている。画素は見ていない） | F-3 |
| 23 | Continue、Freeze を含むジャンプ往復のストレス | 新 | 無い | F-4 |
| 24 | Continue、最初の動画の前のオフセット領域は最初の動画の冒頭フレーム（Freeze） | 新 | 無い | F-5 |

（項目 25・26 は 19・24 と同じ内容として統合）

## 2. 素材とプロジェクト（新テスト共通）

- 素材は `scripts/make-e2e-media.ps1` で生成する **色で同定できる 3 本**（各 20 秒、1280x720、30fps、無音、H.264）:
  - A: 全面 **赤**、最後の 1 秒だけ **黄**（= A の最終フレームは黄）、最初の 1 秒だけ **白**（= A の冒頭フレームは白）
  - B: 全面 **緑**、最後の 1 秒 **シアン**、最初の 1 秒 **マゼンタ**
  - C: 全面 **青**、最後の 1 秒 **橙**
  - 各秒に秒番号を焼き込む（`drawtext`。判定には使わない。目視用）
- プロジェクト（`tests/TimecodeSyncPlayer.Tests/Fixtures/ltc-scenario.tsp`、素材は相対パス）:
  - 先頭オフセット 0:00:05（LTC 0〜5 秒は最初の動画の前）
  - A 0:00:05〜0:00:25、ギャップ 5 秒、B 0:00:30〜0:00:50、ギャップ 5 秒、C 0:00:55〜0:01:15
  - 同期モードとギャップ動作はテストが切り替える
- 画面の判定: プレビューかフルスクリーンの読み戻し（`RealProjectGap.CaptureImage` と同じ経路）から、**中央 60% 領域の平均色**を取り、次で判定する
  - 黒: 平均輝度 < 8/255 かつ黒画素 ≥ 99%
  - 色の同定: 期待色との距離（RGB ユークリッド）< 60、かつ他の候補色との距離の方が大きい
- LTC は `LtcSignalPlayer`（`Play` / `PlayHeld` / `PlayWithSilence`）。ジャンプは `PlayHeld` か `Play` の開始時刻を変えて送出し直す

### 2.5 現場の実素材で回す（利用者の要望 2026-09-17: 検証機には現場で使う様々な素材があるので、それで現場ベースのテストをする）

- 新テストは **素材を固定しない**。プロジェクトは環境変数 `TIMECODE_LTC_SCENARIO_PROJECT`（`.tsp` のパス）で差し替えられ、未設定なら 2 節の色素材の `ltc-scenario.tsp` を使う
- 実素材では色が分からないので、**参照フレームをテストの最初に自分で採る**: 各トラックについて、一時停止で「冒頭フレーム」（MediaIn）と「最終フレーム」（MediaOut または尺 − 1 フレーム）へシークし、画面の読み戻しを参照画像として保存する。以後の判定は「参照画像との一致」（中央 60% 領域の平均色の距離 < 60、かつ他の参照との距離の方が大きい。加えて画素差分の平均 < 12/255）で行う。色素材でもこの方式で判定し、既知の色は目視用にジャーナルへ書くだけにする（判定経路を 1 本にする）
- 「再生中」の判定は色ではなく、**位置（`TimeLabel`）が進む** ことと、参照画像のどれかに近い（＝そのトラックの絵が出ている）ことで行う
- プロジェクトの生成: `scripts/make-ltc-scenario-project.ps1 -MediaDir <素材フォルダ> -Out <.tsp> [-Tracks 3] [-SegmentSeconds 20]`。フォルダ内の動画（拡張子 mp4 / mov / mkv / mxf / ts）を名前順に先頭から `-Tracks` 本、各 `MediaIn 0`・`MediaOut SegmentSeconds`、先頭オフセット 5 秒、ギャップ 5 秒で並べる。`ffprobe` で尺を読み、`SegmentSeconds` より短い素材は尺どおり。相対パスではなく `MediaDir` からの相対で書く（`.tsp` を `MediaDir` 直下に置く）
- 検証機での運用: ランナーに `-MediaDir` を渡すと上のスクリプトでプロジェクトを作り、`TIMECODE_LTC_SCENARIO_PROJECT` に設定して回す。あわせて `RealProjectGapE2ETests`（`TIMECODE_REAL_PROJECT_PATH`）にも同じ `.tsp` を渡して V4 相当を実素材で回す

## 3. 新テスト（`tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs`、VB-CABLE が無ければ Skip）

| ID | 内容 | 合格の観測 |
| --- | --- | --- |
| S-1 | Continue で LTC 追従中 10 秒、`Playback perf` の `frameUpdates`（アプリログ）を 2 秒ごとに読む | 各区間 55〜65（30fps 素材、2 秒） |
| S-2 | Single、A をアクティブ。LTC を 8s ⇄ 20s で 10 往復（各 2 秒保持） | 各保持で位置が目標 ±0.3 秒に入る（20 回）、シーク成功 20、ERR 0 |
| S-3 | Single、A をアクティブ。LTC を 0:00:40（範囲外）で保持 | 位置が A の終端（MediaOut または尺）±1 フレームで止まり、画面が**黄**。LTC を 0:00:10 に戻すと復帰 |
| S-4 | Single、LTC 0:00:35（B の範囲）を保持したまま、プレイリストで B を選択 → C を選択 | B 選択で画面が**緑**（位置 5 秒付近）、C 選択で **青**（LTC は C の範囲外なので C の冒頭か終端。実装の仕様どおり。どちらかを記録） |
| S-5 | Single、A をアクティブ、LTC 0:00:35（B の範囲）を 5 秒保持 | 画面は A の側（赤系）のまま。B へ切り替わらない |
| C-1 | Continue、LTC を A 内 8s ⇄ 20s で 10 往復 | S-2 と同じ判定 + 画面は常に赤系 |
| C-2 | Continue、LTC を A 12s ⇄ B 40s で 10 往復 | 各保持で赤 / 緑が交互、位置が目標 ±0.3 秒 |
| G-1 | Continue + Black、LTC を 0:00:23 から連続送出（A の終端 → ギャップ） | 0:00:25 通過後 1 秒以内に黒 |
| G-2 | G-1 の続きで 0:00:30 通過 | 1 秒以内に緑（B の冒頭はマゼンタなので、最初の 1 秒はマゼンタ、その後緑） |
| G-3 | Continue + Black、A 再生中に LTC を 0:00:27 へジャンプ（保持） | 1 秒以内に黒 |
| G-4 | G-3 から 0:00:40 へジャンプ | 1 秒以内に緑、位置 10 秒 ±0.3 |
| G-5 | Continue + Black、A 12s → ギャップ 27s → B 40s → ギャップ 52s → C 60s → 12s … を 5 周（各 2 秒保持） | 各保持で期待（赤 / 黒 / 緑 / 黒 / 青）、ERR 0、最後に A へ戻って赤 |
| G-6 | Continue + Black、LTC を 0:00:01 から送出（先頭オフセット） | 0〜5 秒は黒、5 秒通過後に白（A の冒頭）→ 赤 |
| F-1 | Continue + Freeze、LTC を 0:00:23 から連続送出 | 0:00:25 通過後、画面が**黄**（A の最終フレーム）のまま 0:00:30 まで |
| F-2 | Continue + Freeze、A 再生中に 0:00:27 へジャンプ | 1 秒以内に黄 |
| F-3 | Continue + Freeze、0:00:27（A の後）→ 0:00:52（B の後）へジャンプ | 黄 → シアン（B の最終フレーム）に更新 |
| F-4 | Continue + Freeze、G-5 と同じ周回 | 各保持で赤 / 黄 / 緑 / シアン / 青、ERR 0 |
| F-5 | Continue + Freeze、LTC を 0:00:01 から送出 | 0〜5 秒は**白**（A の冒頭フレーム）、5 秒通過後に白 → 赤 |

- ストレス（S-2、C-1、C-2、G-5、F-4）は周回数を環境変数で増やせるようにする（既定は上の値。長時間版は opt-in）
- 各テストの終わりに ERR/FTL 0、残プロセス 0
- 期待と違う挙動が出たら、**仕様か欠陥かは親が判断する**。テストは観測値をジャーナルに残して失敗させる

## 4. 検証機での自動実行（利用者の要望 2026-09-17: テストを書くだけでなく検証機でも自動で回す）

- 1 コマンドで回すランナー `scripts/run-ltc-scenarios.ps1` を用意する（同期担当、`docs/prompts/2026-09-17-LTC-scenario-runner.md`）:
  - 引数: `-AppExe <インストール済みの exe>`（省略時はリポジトリの Debug ビルド）、`-ReportDir`、`-Cycles`、`-Filter`（既定は `LtcHardwareLoopE2ETests|LtcScenarioE2ETests`）、**`-MediaDir <実素材フォルダ>`**（2.5 節。指定時は `make-ltc-scenario-project.ps1` でプロジェクトを作り、`LtcScenarioE2ETests` と `RealProjectGapE2ETests` に渡す）
  - 行うこと: 前提の検査（VB-CABLE の CABLE Input / Output、ffmpeg、.NET SDK、対象 exe と同梱 GStreamer）→ 素材生成（`make-e2e-media.ps1`）→ tests のビルド → `dotnet test`（trx）→ 対象アプリの `logs\`（timecodesyncplayer と tcs-gst）とジャーナル・画像を `ReportDir` へ複製 → 要約（合格 / 失敗 / スキップ、失敗テスト名、ERR 行数、残プロセス）を 1 画面に出す
  - **D18 を直す**: `E2EAppRunner.ResolvePrereqs` が `TIMECODE_SYNC_PLAYER_E2E_APP_PATH` の exe と同じディレクトリの `gstreamer` を同梱ランタイムとして認める。ランナーは環境変数を要求しない
- 検証機の運用: `TSP-TestMachine` に「main を pull → `run-ltc-scenarios.ps1 -AppExe <インストール先>` → 要約と `ReportDir` の場所を報告」を依頼する。配布物を送るたびに同じ依頼を出す（手順書 1.5 節に追記）

## 4.5 進捗（親の記録）

- 2026-09-17 10:05: ランナー `scripts/run-ltc-scenarios.ps1` と D18（同梱 `gstreamerin` を exe の隣で認識、環境変数不要）を main `6921064` に統合。開発機で `LtcHardwareLoopE2ETests` を対象に 1 回: passed=14 failed=0 skipped=0 err_ftl=0 leftover=0（`TestResults/ltc-scenarios/20260916T232948Z`、agent-a の作業ツリー）。統合後の非E2E は 1 回目に 1 件だけ失敗し、再実行で 1676/1676（不安定なテストの疑い。再発したら trx で特定する）
- 検証機には clone の付け替え（履歴書き換え後）とランナーの 1 回実行を依頼（10:05）

## 5. 進め方

1. 除去担当（`w5:p6`）が素材・プロジェクト・色判定ヘルパー・新テストを実装（`docs/prompts/2026-09-17-LTC-scenario-e2e.md`）
2. 開発機で親が独立に実行 → 判定 → 統合
3. 検証機（インストール済みアプリ）で同じテストを実行（`TSP-TestMachine`）
4. 結果はこの文書の 1 節の「状態」欄を更新して残す
