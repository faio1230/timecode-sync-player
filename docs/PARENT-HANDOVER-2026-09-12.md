# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

> **2026-09-16 13:20 更新（3 回目）**: V3 の合格基準が利用者の承認で確定し、mpv 除去の順序も改訂した。
> **「2. 現在地」「7. 未完了と次の順」を書き直した。**「3. worktree」「6. OpenCode の扱い」は 09-16 早朝のまま有効。
> 役割・実機の規則・検証コマンドの節は 09-12 のまま有効。

## 1. 役割と進め方

- 親（このセッション）: 設計・指示・独立検証・記録。コードは書かない（runner／集計スクリプト・文書は書く）。
- 実装: OpenCode（DeepSeek V4.1 Flash）。Herdr の 2 ペイン（`w5:p3` = 同期担当、`w5:p6` = 除去担当）。指示は `docs/prompts/*.md` に書き、`herdr pane run <pane> "<文書のパスと要点>"` で渡す。**長い本文をそのまま送るより、指示書を読ませる方が確実。**
- 検証の型: 完了報告を `herdr pane read w5:p3 --source recent-unwrapped --lines 200` で読む → 検証 worktree を報告コミットへ `git checkout --detach <SHA>` → `dotnet build` → 非E2E → 必要なら shim 再ビルド → 実機（1 本ずつ）→ `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` 等へ追記 → 合格なら次の指示、不合格なら差し戻し（原因・証跡・合格条件を明記）。
- 不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13。**I13 は 2026-09-13 追加**: ストリーミングスレッドが要求しうるロックの保持中に GStreamer の状態変更・シークを呼ばない。検査は `python scripts/check-shim-lock-rule.py`）。実装側が単独で決めてはいけない事項もそこにある。プロンプトには毎回パスを含める。

## 2. 現在地（2026-09-16 13:20）

**main は `d9d0e85`**（origin へ push 済み）。今日 1 日で V3 は「原因不明」から「あと 1 点」まで進んだ。

### 今日決着したもの

| 項目 | 結果 |
| --- | --- |
| **V3 の合格基準** | **確定（利用者承認）**: 定常誤差 \|平均\| <= 40ms、プール p95-p5 <= 100ms、**シーク・切替から 1 秒以内に ±80ms**、補正シークが繰り返し出続けない |
| **T7（補正の素材位置）** | Smooth はタイムライン秒と素材秒を引き算しており、Continue の 2 本目以降で無効化されたまま止まっていた。修正後、平均 -25.7ms / p95-p5 71.9ms |
| **T9（着地直後 ±20%）** | 着地から 1 秒だけ速度上限を倍にする。seek-a 0.15〜0.66 秒、seek-b 0.41〜0.76 秒へ短縮 |
| **先行補償（D7-a）** | **既定で無効**（`TCS_SEEK_LATENCY_COMPENSATION=on` のときだけ有効）。有効だと 60fps のシークが 2.38 秒へ悪化する |
| **T8（Jump）** | しきい値 80ms・1 秒セトル。空振り 61→20 回、平均 -141→-70ms。**参考記録**（判定は既定の Smooth で行う） |
| **shim の order はみ出し** | `cf0d3fc` の配列外書き込み。Debug の /RTC1 ダイアログが UI スレッドで再入を起こし 100% クラッシュしていた。`43bba1b` で修正 |
| **除去の順序** | **出荷構成へ切替 → mpv 除去 → CPU 合成除去 → 型付き API → 文書**（`docs/V04-MPV-CPU-REMOVAL-PLAN-2026-09-16.md`） |
| **GPU 合成が使えない PC** | 起動時にダイアログ。**アプリは開いたまま、再生だけ無効**（利用者の決定） |
| **名前の整理** | mpv 形式のコマンド文字列をやめ、型付きメソッドへ（利用者の決定。除去の最後の段） |

### V3（唯一残る門）の状態

**あと 1 点。60fps クリップのシーク収束だけが基準未達。**

| 基準 | 実測（Smooth、T9 の 3 本） | |
| --- | --- | --- |
| \|平均\| <= 40ms | -24〜-27ms | 満たす |
| p95-p5 <= 100ms | 73〜82ms | 満たす |
| 1 秒以内に ±80ms | seek-a 0.15〜0.66 / seek-b 0.41〜0.76 / **seek-c 1.22〜1.72** / seek-back 最大 1.12 秒 | **未達** |
| 補正シークが続かない | 3 本で 2 回 | 満たす |

**T10 で原因を特定済み（アプリの欠陥ではない）**: 264ms のうちロードは 11.6ms、残り 254.6ms は
**直前のキーフレームからのデコード**。V3 の素材は 3 本とも GOP 250 フレーム固定で、60fps では
キーフレーム間隔が 4.17 秒になる。同一クリップ内で、目標 3.680（221 フレーム先）は 277.7ms、
目標 4.240（キーフレーム直後）は 1.5ms。**素材の作り方を現場相当（GOP 1 秒）に直すかを利用者に確認中。**

### 実装済み・検証済み（main）

- T3（`SyncOffsetMs`、ms 単位・UI 付き）、T4（タイムコード入力の丸め）、T5（補正モード）、T6（分解トレース）、
  T7（補正を素材位置で評価）、T8（Jump）、T9（着地窓）、V11（decodeMode）、配布物（GStreamer 同梱・VC++ 連鎖）
- 解析側: `analyze-t6-gap.py`、`analyze-t7-correction.py`、フェーズ開始の LTC 再開検出（`b415508`）、
  `run-v3-accuracy.ps1` の既定パスをリポジトリ相対に（`8b0e600`）、E2E で shim の stderr を保存（`9b82070`）

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

## 7. 未完了と次の順（2026-09-16 13:20）

### いま走っているもの

| ペイン | 作業 | 次のイベント |
| --- | --- | --- |
| `w5:p3`（同期担当） | T10 の報告を日本語でまとめ直して追記 | 素材の GOP をどうするかの利用者判断待ち |
| `w5:p6`（除去担当） | **R1 の E2E 全件**（変更前 main → 出荷構成 → 差分の分類）。45〜70 分 | 差分の分類結果。(a) 出荷構成の欠陥は親へ報告してから直す |

**実機は 1 つ。親が順番を管理する。** エージェントには「実機を使う前に一報」を毎回指示している。
利用者にも E2E の間は PC を触らないよう伝えてある。

### 優先順

| 優先 | 内容 |
| --- | --- |
| **P0** | 60fps の着地（素材の GOP をどうするか決定）→ V3 の再測定 → **V3 の合否判定** |
| **P1** | R1 の E2E の差分の分類と修正（出荷構成の欠陥は親が扱いを決める） |
| **P2** | 段 2（mpv 除去）→ 段 3（CPU 合成除去、shim の CPU コピー ABI も）→ 段 4（型付き API）→ 段 5（文書・配布物・リリースノート） |
| **P3** | V4（ギャップ 3 種。**GPU 合成で Freeze が Hold になっている件**もここで確認）、V5 のシーク連打、V6 の 60 分、V10 の寸法ダイアログ |
| **P4** | T2（LTC の到着時刻を音声サンプル位置から出す。残差の ±20〜40ms の揺れが減る）、MMCSS の結論、HEVC 8bit、再入ガード、shim の決定的ビルド（`/Brepro`） |

### 利用者の回答待ち

- **V3 の素材の GOP**（案 A: 1 秒間隔で作り直して判定 ／ 案 B: 現状のまま「長い GOP では 1.2〜1.7 秒」を仕様とする）
- **現場素材の一覧** — V1 と V11-b を実素材で締めるのに要る
- **V4 の実施方法** — 現場のプロジェクトファイル（`TIMECODE_REAL_PROJECT_PATH`）を貰うか、親が構成したもので代替するか
- **MMCSS の採否**（要検討のまま。実装は `codex/v8c-thread-priority-20260915`）

### 積み残し（軽微）

- `mediaPos + L` のトラック終端クランプ（D7-a 由来。補償は既定 off になったので現状は害なし）
- `OutputBackendState` の初期化前プレースホルダが `Effective=Cpu`（段 3 で必ず引っかかる。R1 の実装メモに記載）
- gst は load 直後に切替前のフレームを 2 回公開してから target へ飛ぶ
- 同一ソースでも shim のハッシュがリンクごとに変わる（PE タイムスタンプ。同一性はコミット SHA で見る）

### 今日踏んだ罠（繰り返さない）

1. **指標の符号**: `delta`（LTC − 再生位置）と `signedErrorMs`（絵 − LTC）は向きが逆。**足す**のが正しい。
   差で計算して「74ms のずれ」を作り出し、T5 の結論・max-buffers 掃引・T6 の設計をその上に積んでいた
2. **統合の検証**: ビルドと非E2E だけで完了にしない。**Debug の実機ロードを 1 本**通すまで完了にしない
   （`cf0d3fc` の配列はみ出しを素通しし、翌日 100% クラッシュとして跳ね返ってきた）
3. **native の中から WPF が再入するダンプ**は、まずクラッシュダンプを `"was corrupted"` で文字列検索する（二分探索より速い）

## 8. 主要文書

- 設計: `docs/OUTPUT-GPU-DESIGN-CONFIRMED.md`、`docs/OUTPUT-PIPELINE-DESIGN.md`、`docs/OUTPUT-GPU-INTEGRATION-PLAN.md`、`docs/GPU-SOURCE-CONTRACT-SPEC.md`、`docs/CANVAS-PLACEMENT-SPEC.md`、`docs/OUTPUT-GPU-STAGE4-5-SPEC.md`、`docs/OUTPUT-GPU-INVARIANTS.md`
- 記録: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`（**V1〜V11 の結果・判定基準・欠陥 D1〜D6 の全記録。最重要**）、`docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md`、`docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md`
- v0.4 の範囲: `docs/V04-SCOPE-mpv-removal-decode-mode.md`（mpv 除去の棚卸し・decodeMode の設計・V11）、`docs/release-0.4-plan.md`（完了の定義・既知の仕様・判断待ち）
- 指示の写し: `docs/prompts/`
- shim: `native/gst-shim/README.md`（別デバイス・共有リング・H-3 規則）
- コード側引き継ぎ: `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`
