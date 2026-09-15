# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

> **2026-09-15 12:00 更新（2 回目）**: 朝からさらに大きく動いた。
> **「2. 現在地」「3. worktree」「6. OpenCode の扱い」「7. 未完了と次の順」を書き直した。**
> 役割・実機の規則・検証コマンドの節は 09-12 のまま有効。

## 1. 役割と進め方

- 親（このセッション）: 設計・指示・独立検証・記録。コードは書かない（runner／集計スクリプト・文書は書く）。
- 実装: OpenCode（DeepSeek V4.1 Flash）。Herdr の隣ペイン `w5:p3`。指示は `docs/prompts/*.md` に書いて `herdr pane run w5:p3 "<1 行: ファイルを読んで実行>"` で渡す（複数行を直接送らない）。
- 検証の型: 完了報告を `herdr pane read w5:p3 --source recent-unwrapped --lines 200` で読む → 検証 worktree を報告コミットへ `git checkout --detach <SHA>` → `dotnet build` → 非E2E → 必要なら shim 再ビルド → 実機（1 本ずつ）→ `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` 等へ追記 → 合格なら次の指示、不合格なら差し戻し（原因・証跡・合格条件を明記）。
- 不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13。**I13 は 2026-09-13 追加**: ストリーミングスレッドが要求しうるロックの保持中に GStreamer の状態変更・シークを呼ばない。検査は `python scripts/check-shim-lock-rule.py`）。実装側が単独で決めてはいけない事項もそこにある。プロンプトには毎回パスを含める。

## 2. 現在地（2026-09-15 12:00）

**main は `e76ad87`**（origin へ push 済み）。ここまでの判断はすべて記録に落としてある。

### 決着したもの

| 項目 | 結果 |
| --- | --- |
| **V8（表示先の違い）** | **合格。** 基準「40 秒で 5 回以下かつ連続なし」を既定構成が満たす。**MMCSS は v0.4 に入れない**（3 条件に差が出ず、既定 ON の悪化は再現しなかった）。指標も訂正し、主指標は `PresentRefreshCount` の飛び |
| **v0.4 の範囲** | **mpv を完全除去**（利用者が 09-13 に決定、09-15 に再確認）。退避は `decodeMode`。名前の整理も含む |
| **配布方式** | **(a) 同梱。** 実ロード 17 プラグイン・依存閉包 48 DLL / 38.4MB ＋ライセンス 25 ファイル。VC++ は再頒布パッケージをインストーラーから連鎖 |
| **29.97 の実時間換算** | **修正・検証済み**（`22e9ec5`）。`ToRealSeconds` を総フレーム数経由にした。24/25/30 は不変 |
| **LTC 4 種の検証** | **完了。4 種とも同等**（平均 -60〜-69ms、ばらつき 151〜175ms）。**29.97 NDF の自動検出だけ 30 と区別できない**（手動指定で運用、マニュアル記載が要る） |

### V3（唯一残る門）の状態

**原因は完全に特定済み。実装待ち。**

- 連続再生 **-34〜-45ms**、シーク後 **-42〜-157ms**。シーク後の着地誤差が
  **デッドゾーン（200〜250ms）の内側**なので `Decide` が `None` を返し続け、**誰も補正しない**
- ばらつき 151〜175ms の正体は**この 2 つの差（約 110ms）**であって揺れではない
- **D7-a（先行シーク補償）は失敗。** 着地遅延は履歴から予測できず（同じトラックで
  クリップ先頭 17〜21ms、中ほど 274〜332ms）、gst も mpv も悪化させた。**無効のまま**
- seek-c の -274ms は **D4（`db54911`）で既に解消していた**。古い測定を根拠に設計した私の誤り

**対策は T5（同期補正モード）**。利用者の指示で `Smooth`（レート微調整、既定）と
`Jump`（フラッシュシーク）の 2 モード、Preference から選択。

### 実装済み・実機待ち

- `decodeMode`（C ABI + shim + managed + `docs/V11-DECODE-MODE-VERIFICATION.md`）
- 配布物（mpv 除去、48 DLL 同梱、プラグインパス固定、VC++ 連鎖、SHA256 検査）

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

## 7. 未完了と次の順（2026-09-15 12:00）

### いま走っているもの

| ペイン | 作業 | 次のイベント |
| --- | --- | --- |
| `w5:p6` | **T5 同期補正モード** | **C ABI の提案を出してくる。親が承認してから実装** |
| `w5:p3` | **T4 タイムコード入力の丸め込み** | 実装＋テスト。実機不要 |

### 仕様は書いたが未着手（`...-wt-d6-sync-20260915/docs/prompts/`）

| # | 内容 | 実機 | 備考 |
| --- | --- | --- | --- |
| **T5** | 同期補正モード（`Smooth` 既定 / `Jump`） | 要 | **V3 の門。** C ABI 承認が要る |
| **T3** | `SyncOffsetMs`（全体オフセット、**ms 単位**、UI 付き） | 不要 | **T5 と同じ同期入口を触るので T5 の後** |
| **T2** | LTC 入力遅延の直接測定 | 要 | 4 種の fps を横に並べ 1/fps へ回帰して内訳を分解する |
| **T4** | タイムコード入力の丸め込み | 不要 | 着手中 |

### 実機待ちの行列（V3 が片付いてから、この順）

1. **T5 の実機測定 → V3 の判定**
2. **V11-a〜d**（`decodeMode`。手順は `docs/V11-DECODE-MODE-VERIFICATION.md`）
3. **配布物の実機確認** — パッケージ dry run、vc_redist の取得・署名検証、
   **同梱レイアウトで実際に再生できるか**（方式 (a) の最大のリスク）、shim の `--policy-only` self-test
4. **T2**（LTC 入力遅延の実測）

### V3 合格後

1. **mpv 実装の削除（コミット A）** — 棚卸しは `fc1ee84`「名前で消すと壊れる箇所」
2. **名前の整理（コミット B）** — `IMpvApi` → `IPlayerApi` 等。**削除と改名を混ぜない**
3. 既定値切替、`docs/SETUP.md` と `docs/verification-checklist.md` の更新、リリースノート

### V3 の合格基準は書き直しが要る（利用者の指示、未確定）

現行の基準（定常誤差が mpv 以下、シーク回復 p95 250ms 以下）は前提が変わった。

- **mpv は削除するので比較対象にしない。GStreamer 単独の絶対値で見る**
- **現場基準の ms で置く**
- **シークのジャンプが若干遅れるのは許容**（デコードの物理限界がある）
- **落ち着いた先の精度**を主基準にする

**T5 の結果を見てから正式に提案する。** 数値は親が独断で決めない。

### 利用者の回答待ち

- **現場素材の一覧** — V11-b（ソフトウェアデコードの実力）を実素材で測るのに要る。
  合成素材では 4K HEVC が実時間の 3.5 倍だったが、実写ではもっと厳しくなる
- **V4 の実施方法** — 現場のプロジェクトファイル（`TIMECODE_REAL_PROJECT_PATH`）を貰うか、親が構成したもので代替するか

### 積み残し（軽微）

- `mediaPos + L` のトラック終端クランプ（D7-a 由来、補償は既定 off なので現状は害なし）
- CPU 合成（`outputBackend=0`）は `SourceFrameReady` 未接続
- gst の seek-c で 1 回だけ 1266ms（他 2 回は 354/356ms）。単発の外れ値、未調査
- gst は load 直後に切替前のフレームを 2 回公開してから target へ飛ぶ
- `PlaylistTrackFormatter.TryParseTimecode` は同期経路とは別物（T4 で扱う）

## 8. 主要文書

- 設計: `docs/OUTPUT-GPU-DESIGN-CONFIRMED.md`、`docs/OUTPUT-PIPELINE-DESIGN.md`、`docs/OUTPUT-GPU-INTEGRATION-PLAN.md`、`docs/GPU-SOURCE-CONTRACT-SPEC.md`、`docs/CANVAS-PLACEMENT-SPEC.md`、`docs/OUTPUT-GPU-STAGE4-5-SPEC.md`、`docs/OUTPUT-GPU-INVARIANTS.md`
- 記録: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`（**V1〜V11 の結果・判定基準・欠陥 D1〜D6 の全記録。最重要**）、`docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md`、`docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md`
- v0.4 の範囲: `docs/V04-SCOPE-mpv-removal-decode-mode.md`（mpv 除去の棚卸し・decodeMode の設計・V11）、`docs/release-0.4-plan.md`（完了の定義・既知の仕様・判断待ち）
- 指示の写し: `docs/prompts/`
- shim: `native/gst-shim/README.md`（別デバイス・共有リング・H-3 規則）
- コード側引き継ぎ: `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`
