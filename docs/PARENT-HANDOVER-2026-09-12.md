# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

> **2026-09-16 18:50 更新（6 回目）**: **D8（= D6）を修正し main `45534a9` へ統合**（安全網 `795c7ff` + リング epoch `d44481f`、R1 の (b) も同時）。
> T2 は段 3（既定 on・デッドバンド 5ms）を実装中、実機測定待ち。**公開リポジトリのため、文書にローカルの絶対パスを書かない**（`be1e61a` で除去。実体は gitignore の `docs/local/LOCAL-PATHS.md`）。
>
> **2026-09-16 14:40 更新（5 回目、3 代目の親）**: D8 の原因を判定（D6 と同一）し修正方針を承認。修正 1（安全網 `795c7ff`）は実機で確認済み、修正 2（リング epoch）を実装中。
> T2 段 1（`7b49187`）は測定で妥当と判定し、段 2 を実装中。「7. いま走っているもの」を書き換えた。詳細は検証記録の末尾 2 節。
>
> **2026-09-16 14:00 更新（4 回目）**: **V3 が合格した。** 素材のキーフレーム間隔を現場相当（1 秒）にして測り直し、
> LTC 4 種で基準を満たした。R1 の E2E で新しい欠陥 D8 を発見。
> **「2. 現在地」「7. 未完了と次の順」を書き直した。**「3. worktree」「6. OpenCode の扱い」は 09-16 早朝のまま有効。
> 役割・実機の規則・検証コマンドの節は 09-12 のまま有効。

## 1. 役割と進め方

- 親（このセッション）: 設計・指示・独立検証・記録。コードは書かない（runner／集計スクリプト・文書は書く）。
- 実装: OpenCode（DeepSeek V4.1 Flash）。Herdr の 2 ペイン（`w5:p3` = 同期担当、`w5:p6` = 除去担当）。指示は `docs/prompts/*.md` に書き、`herdr pane run <pane> "<文書のパスと要点>"` で渡す。**長い本文をそのまま送るより、指示書を読ませる方が確実。**
- 検証の型: 完了報告を `herdr pane read w5:p3 --source recent-unwrapped --lines 200` で読む → 検証 worktree を報告コミットへ `git checkout --detach <SHA>` → `dotnet build` → 非E2E → 必要なら shim 再ビルド → 実機（1 本ずつ）→ `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` 等へ追記 → 合格なら次の指示、不合格なら差し戻し（原因・証跡・合格条件を明記）。
- 不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13。**I13 は 2026-09-13 追加**: ストリーミングスレッドが要求しうるロックの保持中に GStreamer の状態変更・シークを呼ばない。検査は `python scripts/check-shim-lock-rule.py`）。実装側が単独で決めてはいけない事項もそこにある。プロンプトには毎回パスを含める。

## 2. 現在地（2026-09-16 14:00）

**main は `73de42c`**（origin へ push 済み）。**V3 は合格した。** v0.4 の門は開いた。

### V3: 合格（2026-09-16 13:58）

| LTC | 平均 | p95-p5 | ±80ms への収束 |
| --- | ---: | ---: | --- |
| 24 / 25 / 29.97 / 30 | -16〜-21ms | 50〜67ms | **最大 154ms** |

基準は 40ms / 100ms / 1 秒以内。**限定つき**（検証記録の「V3 の判定」節）:
素材のキーフレーム間隔 1 秒が前提、シーク先はキーフレーム直後の良い側（最悪でも +83ms の見積もり）、
判定は既定の `Smooth`、29.97 は `LtcFpsMode=fixed` 前提。

### 今日直した欠陥（V3 に至るまで）

| # | 内容 | コミット |
| --- | --- | --- |
| 1 | shim の `order` 1 要素はみ出し（Debug の /RTC1 ダイアログ → UI スレッド再入 → 100% クラッシュ） | `43bba1b` |
| 2 | 補正がタイムライン秒と素材秒を引き算（Continue の 2 本目以降で Smooth が停止していた） | `184281d` / `77350e1` |
| 3 | 先行補償が既定有効（60fps のシークを 2 倍以上遅くする）→ 既定オフ | `4fd56dd` |
| 4 | 着地直後の速度上限 ±10% では 1 秒に収束しない → 着地から 1 秒だけ ±20% | `4fd56dd` |
| 5 | Jump が残差の揺れでシークし続ける → しきい値 80ms・1 秒セトル | `b3fb49b` |
| 6 | 解析がフェーズ開始の LTC を取りこぼす | `b415508` |
| 7 | 測定素材のキーフレーム間隔が x264 既定の 250 のまま | `6e6214e` |

### R1（段 1: 出荷構成への切替）の E2E 結果

| 構成 | 実行 | 合格 | 失敗 |
| --- | ---: | ---: | ---: |
| main 149025f（mpv + CPU 合成） | 61 | 60 | 1 |
| agent-b 1e9bf86（**出荷構成**） | 62 | 58 | 4 |

- **(a) `GStreamerBackend_SurvivesRepeatedTrackSwitches` = 新しい欠陥 D8。** 解像度が変わる切替
  （1080p60 → 720p25）で GPU デバイス消失（`0x887A0005`、`GetDeviceRemovedReason` も DEVICE_REMOVED）。
  60ms で復旧するが再生クロックがほぼ停止（0.13〜0.18）。3/3 再現。指示書 `docs/prompts/2026-09-16-D8-*.md`
- (b) Spout のテストが CPU 合成の統計を見ている／新規ダイアログ E2E のヘルパーが要素を誤検出 → 実装側が修正中

#### D8 の調査（2026-09-16 14:03、実装側の 1 次報告）

- **同解像度の切替では 0 件**（3 切替）。解像度が変わる切替でのみ発生（再現性あり）
- `GetDeviceRemovedReason` は `0x887A0005`（DEVICE_REMOVED）。**誤検知ではない**
- 復旧（60ms）後、**新しいデバイスの `sharedFence.Signal`（`OutputEngine.cs:1018`）でドライバ側の
  アクセス違反 → WER ダンプでプロセス停止**。共有フェンスは復旧で作り直されている（古いデバイスの使い回しではない）
- **shim 側のログに `ring: created` が出ていない**＝解像度が変わってもリングを作り直していない疑い。
  アプリが古いリングのリースを使って GPU フォールトを起こしている可能性（未確定）
- 実装側の案: **A. shim にリングの生成・破棄・リースの stderr ログを足して再測定**（最優先）、
  B. 復旧後の新デバイスの健全性を確かめてから合成を再開、C. 復旧時に shim 側も作り直す
- **親が A のみ承認済み（測定）。B・C は次の親が判断する**
- 構成非依存の既存失敗: `SystemScenario.ProjectRoundTrip`（main でも失敗。未調査）

### 判定済み・実装済み（main）

T3・T4・T5・T6・T7・T8・T9・T10・T11、V11（decodeMode）、配布物（GStreamer 同梱・VC++ 連鎖）、
段 1 の実装（既定を出荷構成へ、GPU 不可時のダイアログ、再生可否の単一判定）。**削除はまだ何もしていない。**

## 3. worktree と用途（2026-09-16 整理後）

**3 つだけ。** 2026-09-16 に 10 → 3、ブランチを 43 → 5 に整理した。

| パス | ブランチ | 用途 |
| --- | --- | --- |
| `timecode-sync-player`（本体） | **`main`** | **親の検証・記録・コミット場所。** 報告 SHA へ `checkout --detach` して検証し、必ず `main` へ戻す |
| `timecode-sync-player-wt-a` | `agent-a` | **同期担当のエージェント**（ペイン `oc-sync`） |
| `timecode-sync-player-wt-b` | `agent-b` | **もう 1 人のエージェント**（ペイン `oc-v11`。次は mpv 除去） |

作業ツリー名は役割ではなく `a` / `b` にした。**役割は変わるが、作業ツリーは作り直さずに済むように。**
担当が変わったらペインのラベルだけ付け替える。

### 残したブランチ

| ブランチ | 理由 |
| --- | --- |
| `codex/v8c-thread-priority-20260915` | **MMCSS（採否は要検討）**。実装と、観測スクリプトの判定値の訂正（`280c327`、MMCSS 中の `GetThreadPriority` は相対 15） |
| `codex/v3-next-20260914` | 旧・共有ツリーに残っていた未コミット変更の保全先（`70eefda`）。`run-v3-accuracy.ps1` の `TIMECODE_SYNC_PLAYER_OUTPUT_TRACE` 設定は **main が構成を変えていて機械的に当てられず要確認** |

### ビルド依存と素材の置き場（gitignore のため作業ツリーに入らない）

| 物 | 場所 |
| --- | --- |
| `vendor/`（Spout2 ほか） | `E:	cs-archiveuild-depsendor` → 各作業ツリーの `vendor/` へ複写済み |
| `SpoutDX.dll` / `libmpv-2.dll` | `E:	cs-archiveuild-deps
ative-dll` → 各作業ツリーの `native/` へ複写済み |
| **`tcs_gstreamer.dll`** | **保全していない。各作業ツリーで自分のソースから建てる**（他ツリーの DLL を流用しない） |
| V1 素材セット | `E:	cs-archive\media1-set1` |
| **H.264 4K60 素材**（V11-e で生成） | `E:	cs-archive\media11-extra1_h264_4k60.mp4` |
| 測定の証跡（`TestResults`） | `E:	cs-archive	estresults\<旧作業ツリー名>` |

**文書中の旧パス `...-wt-integrate-20260912rtifacts\media1` は失効している。** 素材は E: を使うこと。

## 3.5 素材・DLL・実機ハーネスの置き場（2026-09-16 14:50 追記）

**「アプリや素材が見つからない」で止まらないための一覧。** `artifacts/` と `native/` と `vendor/` は
gitignore なので、**作業ツリーごとに実体が要る**。

### 現在の実体（2026-09-16 14:50 時点）

| もの | 置き場 | main | wt-a | wt-b |
| --- | --- | :-: | :-: | :-: |
| アプリ（Debug ビルド） | `<worktree>\src\TimecodeSyncPlayerin\Debug
et8.0-windows\TimecodeSyncPlayer.exe` | あり | あり | あり |
| shim | `<worktree>
ative\gst-shimuild-debug	cs_gstreamer.dll`（`build-shim.ps1 -Config Debug` で生成。csproj が bin へコピー） | あり | あり | あり |
| `SpoutDX.dll` / `libmpv-2.dll` | `<worktree>
ative\` | あり | あり | あり |
| Spout のソース（shim のビルドに必須） | `<worktree>endor\Spout2` | あり | あり | あり |
| **E2E 用の素材 6 本** | `<worktree>rtifacts\media\`（`test_1080p60.mp4` ほか） | あり | **無し** | あり |
| **V1 のコーデック素材 23 ファイル** | **`E:	cs-archive\media1-set1`（522MB）**。使う作業ツリーの `artifacts\media1` へコピーする | 無し | 無し | 無し |
| V11 の追加素材（4K H.264） | `E:	cs-archive\media11-extra`（12MB） | 無し | 無し | 無し |
| GStreamer ランタイム | `C:\Program Files\gstreamer.0\msvc_x86_64`（環境変数 `GSTREAMER_1_0_ROOT_MSVC_X86_64` 設定済み） | 共通 | | |
| ffmpeg / ffprobe | `C:\Program Filesfmpegin` | 共通 | | |
| 過去の測定の証跡 | `E:	cs-archive	estresults\<worktree 名>\` | | | |

**C: の空きは 19GB しかない。** 素材をコピーするときは使う作業ツリー 1 つだけにすること。

### 足りないものの作り方

```powershell
# E2E 用の素材 6 本（wt-a に無い。ffmpeg が要る）
powershell -File scripts\make-e2e-media.ps1            # <repo>rtifacts\media へ出す

# V1 のコーデック素材（生成はしない。アーカイブからコピーする）
robocopy E:	cs-archive\media1-set1 <repo>rtifacts\media1 /E

# shim（vendor\Spout2 が無いと CMake が失敗する。無ければ E:	cs-archiveuild-depsendor から取る）
powershell -File native\gst-shimuild-shim.ps1 -Config Debug
```

### 実機ハーネスの呼び方（既定は全部リポジトリ相対。2026-09-16 に直した）

```powershell
# V3（LTC 同期精度）。素材は run ごとに自動生成、VB-CABLE のループが要る
powershell -File scripts
un-v3-accuracy.ps1 -Backends gst -Label <名前> [-Repeats 3] `
  [-LtcFps 24|25|29.97|30] [-LtcFpsMode auto|fixed] [-SyncCorrectionMode smooth|jump]
#   出力先: <repo>\TestResults3\<名前>-ltc<fps>-gst[-n]#   アプリは同じ作業ツリーの Debug ビルドを自動で使う（TIMECODE_SYNC_PLAYER_E2E_APP_PATH 未設定時）

# V1 / V11（コーデック行列）。-MediaDir 未指定なら <repo>rtifacts\media1 を見る
powershell -File scripts\GpuOutputProbeHarness\Run-V1Matrix.ps1 [-MediaDir ...] [-DecodeMode software] [-Only ...]

# 単発の実機 run
powershell -File scripts\GpuOutputProbeHarness\Invoke-AppGpuTrial.ps1 -MediaPath <素材> -Label <名前> [-PlayerBackend Gstreamer]

# E2E（アプリのパスは env 未設定なら同じ作業ツリーの Debug ビルドを探す）
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj -c Debug --filter "Category=E2E"
```

**罠**: `Run-V1Matrix.ps1` と `Invoke-AppGpuTrial.ps1` の既定パスは、2026-09-16 の整理で消した作業ツリー
（`wt-integrate-20260912` / `wt-verify-oe-20260911-1344`）を指したままだった。同日に**リポジトリ相対へ直した**。
**古い絶対パスが残っていないかは `grep -rn "wt-" scripts/` で確認できる。**

## 4. 実機試験の規則（利用者の指示、厳守）

- 直列に 1 本ずつ。OpenCode が GPU 試験中（報告に開始時刻が出る）は親は走らせない。逆も同じ（親の試験中は指示を送らない）。
- console セッションであること（`query session` に `>console`。RDP 中は不可）。自分が起動した PID だけ終了する。プロセス名での kill は禁止。
- 新しいネイティブ経路は 1080p 短時間 → 4K の順。ビルドや重い処理を性能試験と同時に走らせない。
- 異常（フリーズ兆候、GPU エラー）が出たら試験を止めて状態を保存し、利用者に報告。GPU リセット・ドライバー操作・OS 再起動・WPR/UAC 採取はしない。
- main への書き込みは統合（ff）のみ。stash・reset・clean・discard は使わない。

## 5. 検証コマンド（統合 worktree を例に）

```powershell
# 1) 報告 SHA へ（検証 worktree）
git -C C:\Users\<user>\Documents\timecode-sync-player-wt-verify-oe-20260911-1344 checkout -q --detach <SHA>
# 2) ビルド・非E2E（同 worktree）
dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj --configuration Debug
dotnet test tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj --configuration Debug --filter "Category!=E2E"
# 3) shim を変更した報告なら再ビルドして配置（GSTREAMER_1_0_ROOT_MSVC_X86_64 は設定済み、vendor/Spout2 が必要）
powershell -File native\gst-shim\build-shim.ps1 -Config Debug
Copy-Item native\gst-shim\build-debug\tcs_gstreamer.dll src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\ -Force
# 4) E2E（全部で約 4 分。テスト bin に libmpv-2.dll / SpoutDX.dll / tcs_gstreamer.dll が必要）
dotnet test ... --no-build --filter "Category=E2E"
# 5) 実機 1 本（runner は scripts\GpuOutputProbeHarness\Invoke-AppGpuTrial.ps1）
powershell -File scripts\GpuOutputProbeHarness\Invoke-AppGpuTrial.ps1 -MediaPath <mp4> -Label <label> -Seconds 50 -PlayerBackend Gstreamer -AppExe <検証 exe> -LogRoot <結果ルート>
#    オプション: -ProjectPath(.tsp は素材と同じディレクトリに置く) -ClickPlay -ScreenshotAtSeconds -TestCardOn/OffAtSeconds
#               -KillReceiverAfterSeconds -SimulateDeviceLoss "10,20" -GpuRetryAtSeconds -ExitDialog None|Normal|Force -NoSpout
# 6) 集計
python scripts\GpuOutputProbeHarness\app_run_summary.py <run> 8 48      # 実フレーム/秒・遅れ・表示・合成・lead・errors
python scripts\GpuOutputProbeHarness\analyze_probe.py <run>\app --start 10 --end 48 --output <run>\analysis-10-48
python scripts\GpuOutputProbeHarness\v1_matrix_summary.py <TestResults\v1> 8 48   # V1 行列の表（ffprobe が PATH に必要: C:\Program Files\ffmpeg\bin）
```

合格の目安（GStreamer×Gpu、1080p／4K）: 実フレーム = 素材 fps（起動 2 秒を除く）、表示 59.9Hz 以上、合成 p99 1ms 以下、Spout 60Hz、生成→走査 4〜6ms、error 0、exit 0、seq+2 と NotReady の対 0。

## 6. OpenCode（Herdr）の扱い（2026-09-16 整理後）

| ペイン | ラベル | 作業ツリー | 担当 |
| --- | --- | --- | --- |
| `w5:p3` | `oc-sync` | `wt-a` | 同期精度の調査（T6） |
| `w5:p6` | `oc-v11` | `wt-b` | V11 完了。**次は mpv 除去**（同期側と同じコード領域なので V3 が片付いてから） |

- **PC 再起動後の復元でセッションがペイン間で入れ替わることがある**（2026-09-16 に発生）。
  ラベルもタイトルも当てにならない。**直近の発言内容とコンテキスト使用率で照合**してからラベルを直す
- 状態: `herdr pane get <pane>`。`blocked` はほぼ許可プロンプト。**範囲を確認してから**
  `Right` → `Enter`（Allow always）→ `Enter`（Confirm）。**`C:\*` のような広い範囲は Allow once にとどめる**
- `/new` は `MSYS_NO_PATHCONV=1` を付けて送り、補完メニューで止まるので `Enter` を追送する。
  **切り替える前に、会話にしか無い情報を文書へ書き写す**
- **実機は 1 本ずつ。** 片方が測定中はもう片方の GPU・ディスプレイ・重い CPU を止める
- **モデル障害の罠**: 途中で止まり追加指示も即終了するなら、`pane read | grep -i 'error\|opt in'` で確認する

## 7. 未完了と次の順（2026-09-16 14:00）

### いま走っているもの

| ペイン | 作業 | 状態 |
| --- | --- | --- |
| `w5:p3`（同期担当） | **T2 段 2**（`TCS_LTC_SAMPLE_CLOCK`、既定 off。指示書 `docs/prompts/2026-09-16-T2-ltc-sample-clock.md` 3 節） | 実装中（基点 `ee3de1f`）。実機は on 3 本 + off 1 本、親の合図後 |
| `w5:p6`（除去担当） | D8 は完了・統合済み（`45534a9`）。**次は段 2 の移設部分**（`docs/prompts/2026-09-16-STAGE2-mpv-relocation.md`） | 指示待ち → 着手 |

**実機は 1 つ。親が順番を管理する。** エージェントには「実機を使う前に一報」を毎回指示している。
利用者にも、実機を使う間は PC に触らないよう都度お願いしている。

### 優先順

| 優先 | 内容 |
| --- | --- |
| **P0** | **D8**（解像度が変わる切替で GPU デバイス消失）。**測って切り分けてから直す。** 現場のプレイリストは解像度が混在しうるので出荷前に必須 |
| **P1** | R1 の (b) 2 件の修正を検証して統合 → 段 1 の完了判定 |
| **P2** | 段 2（mpv 除去）→ 段 3（CPU 合成除去、shim の CPU コピー ABI も）→ 段 4（型付き API）→ 段 5（文書・配布物・リリースノート） |
| **P3** | V4（ギャップ 3 種。**GPU 合成で Freeze が Hold になっている件**もここで）、V5 のシーク連打、V6 の 60 分、V10 の寸法ダイアログ、`SystemScenario.ProjectRoundTrip` の既存失敗 |
| **P4** | T2（LTC の時刻を音声サンプル位置から出す。残差の ±20〜40ms の揺れが消え、補正の往復も止まる）、MMCSS の結論、HEVC 8bit、再入ガード、shim の決定的ビルド（`/Brepro`） |

### 利用者の回答待ち

- **現場素材の一覧** — V1 と V11-b を実素材で締めるのに要る
- **V4 の実施方法** — 現場のプロジェクトファイル（`TIMECODE_REAL_PROJECT_PATH`）を貰うか、親が構成したもので代替するか
- **MMCSS の採否**（要検討のまま。実装は `codex/v8c-thread-priority-20260915`）

### 積み残し（軽微）

- `mediaPos + L` のトラック終端クランプ（D7-a 由来。補償は既定 off になったので現状は害なし）
- `OutputBackendState` の初期化前プレースホルダが `Effective=Cpu`（段 3 で必ず引っかかる）
- gst は load 直後に切替前のフレームを 2 回公開してから target へ飛ぶ
- 同一ソースでも shim のハッシュがリンクごとに変わる（PE タイムスタンプ。同一性はコミット SHA で見る）
- `run-v3-accuracy.ps1` は `-Repeats` で複数本、`-LtcFps` / `-LtcFpsMode` / `-SyncCorrectionMode` を取る。
  既定パスはリポジトリ相対（`8b0e600`）。E2E はアプリの stdout/stderr を run ディレクトリに保存する（`9b82070`）

### 今日踏んだ罠（繰り返さない）

1. **指標の符号**: `delta`（LTC − 再生位置）と `signedErrorMs`（絵 − LTC）は向きが逆。**足す**のが正しい。
   差で計算して「74ms のずれ」を作り、T5 の結論・max-buffers 掃引・T6 の設計をその上に積んでいた
2. **統合の検証**: ビルドと非E2E だけで完了にしない。**Debug の実機ロードを 1 本**通すまで完了にしない
3. **native の中から WPF が再入するダンプ**は、まずクラッシュダンプを `"was corrupted"` で文字列検索する
4. **測定条件が現場と違っていないかを疑う**: 60fps の「アプリの欠陥」に見えた 264ms は、x264 既定の
   キーフレーム間隔 250 フレームによるデコード時間だった。**素材の作り方も測定条件の一部**

## 8. 主要文書

- 設計: `docs/OUTPUT-GPU-DESIGN-CONFIRMED.md`、`docs/OUTPUT-PIPELINE-DESIGN.md`、`docs/OUTPUT-GPU-INTEGRATION-PLAN.md`、`docs/GPU-SOURCE-CONTRACT-SPEC.md`、`docs/CANVAS-PLACEMENT-SPEC.md`、`docs/OUTPUT-GPU-STAGE4-5-SPEC.md`、`docs/OUTPUT-GPU-INVARIANTS.md`
- 記録: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`（**V1〜V11 の結果・判定基準・欠陥 D1〜D6 の全記録。最重要**）、`docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md`、`docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md`
- v0.4 の範囲: `docs/V04-SCOPE-mpv-removal-decode-mode.md`（mpv 除去の棚卸し・decodeMode の設計・V11）、`docs/release-0.4-plan.md`（完了の定義・既知の仕様・判断待ち）
- 指示の写し: `docs/prompts/`
- shim: `native/gst-shim/README.md`（別デバイス・共有リング・H-3 規則）
- コード側引き継ぎ: `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`
