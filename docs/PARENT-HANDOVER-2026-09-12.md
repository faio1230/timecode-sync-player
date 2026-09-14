# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

> **2026-09-15 更新**: 2〜3 日ぶんの進捗を反映した。**「2. 現在地」と「7. 未完了と次の順」は全面的に書き直してある。**
> それ以外の節（役割・worktree・実機の規則・検証コマンド・OpenCode の扱い）は 09-12 のまま有効。

## 1. 役割と進め方

- 親（このセッション）: 設計・指示・独立検証・記録。コードは書かない（runner／集計スクリプト・文書は書く）。
- 実装: OpenCode（DeepSeek V4.1 Flash）。Herdr の隣ペイン `w5:p3`。指示は `docs/prompts/*.md` に書いて `herdr pane run w5:p3 "<1 行: ファイルを読んで実行>"` で渡す（複数行を直接送らない）。
- 検証の型: 完了報告を `herdr pane read w5:p3 --source recent-unwrapped --lines 200` で読む → 検証 worktree を報告コミットへ `git checkout --detach <SHA>` → `dotnet build` → 非E2E → 必要なら shim 再ビルド → 実機（1 本ずつ）→ `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` 等へ追記 → 合格なら次の指示、不合格なら差し戻し（原因・証跡・合格条件を明記）。
- 不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13。**I13 は 2026-09-13 追加**: ストリーミングスレッドが要求しうるロックの保持中に GStreamer の状態変更・シークを呼ばない。検査は `python scripts/check-shim-lock-rule.py`）。実装側が単独で決めてはいけない事項もそこにある。プロンプトには毎回パスを含める。

## 2. 現在地（2026-09-15）

- **ゴールが変わった**。v0.4 は「GStreamer×GPU 合成の**一本**にした版」。
  **mpv（`PlayerBackend`）と CPU 合成（`outputBackend=Cpu`）を v0.4 で除去する**（利用者の決定、2026-09-14）。
  退避は GStreamer 内の `decodeMode=hardware/software` が担う。範囲は `docs/V04-SCOPE-mpv-removal-decode-mode.md`。
  **V7 は消滅、V11（decodeMode）が追加**された。
- **既定値はまだ `Mpv`＋`Cpu` のまま**。切替は V1〜V6・V8〜V11 の合格後。

### 統合済みの修正（すべて親が独立検証して ff 統合）

| | 内容 | 効果 |
| --- | --- | --- |
| D1 `9669fad` | アダプターが `seeking`／`pause` を実装しておらず、**同期エンジンが一度も動いていなかった** | 精度の測定数 393 → 1067 |
| D2 `fdbf543` | `frame_lock` 保持中に GStreamer の状態変更・シークを呼んでいた（I13 制定の元） | 黒ギャップ復帰 11978ms → 51ms |
| A1 `c00d0db` | **GPU 経路の同期精度計測**（ソース／キャンバスの読み戻し） | **出荷経路で初めて精度が測れるようになった** |
| C1 `ecb796d`/`ad03ef8` | 比較用スイッチ `TCS_GAP_MODE` / `TCS_SEEK_METHOD`（計測専用、既定は現状） | 3(a)/3(b) の比較に使用。**どちらも現状維持が結論** |
| D4 `db54911` | ロード安定ゲートが CPU 描画数を見ており、**GPU 合成では 5 秒間ゲートが開かない** | seek-c の定常誤差 -274ms → -29〜-59ms |
| D5 `d53e9fa` | リース返却漏れで合成器が**同じフレームを返し続ける**（黒のまま戻らない） | 全面黒 10 本中 2 本 → **5 本中 0 本** |

### V1〜V11 の状況

| | 状態 |
| --- | --- |
| V1, V2, V5, V9, V10 | **合格** |
| V3 | **材料は揃っている。採用構成で 1 回だけ再測定して判定する**（D4・D5 の効果込み） |
| V4 | 未実施。**実施方法が利用者の判断待ち**（現場のプロジェクトファイルか、親が構成したもので代替か） |
| V6 | 部分合格。**60 分再測が必要**（V8-D に相乗り） |
| V8 | **基準を確定**（40 秒で落ち 5 回以下かつ連続なし、MMCSS 既定有効）。**V8-D で最終確認中** |
| ~~V7~~ | 消滅（mpv 除去による） |
| V11 | 未着手（decodeMode の実装から） |
| D6 | 記録のみ。解像度が変わる切替でデバイス消失 → 旧サイズでリング再オープン → 黒 |

### 既知の仕様として残すもの

**全画面表示中、40 秒あたり数回 1 枚だけ提示機会を飛ばす**（連続しない）。
原因は OS のスケジューリングで、**mpv でも同じ頻度**。`docs/release-0.4-plan.md` の「3.6 既知の仕様」。
将来の対処候補（swapchain の latency waitable で歩調を取る等）も同節に記録。

### 親が繰り返した誤りの型（**後任は必ず読むこと**）

**「成否や件数」を見て「中身が進んだか」を見ない**、という同じ誤りを 3 回している。

- S4: `compose.acquire` の `NotReady` を遅延の指標と誤読しかけた（実際は「このティックに新フレーム無し」）
- D5: `source.acquire` の `Ready` を「新フレーム取得」と誤読した（**同じフレームでも Ready**。`compose.srv` を見れば分かった）
- V8: 秒ごとの present 件数で「落ち」を数えた（実レートが 60.000Hz ちょうどでないため**偽の落ち**が出る。**間隔で見る**）

**進んでいるかを見たいときは、成否ではなく中身が変わったかを数える指標を選ぶこと。**

## 3. worktree と用途

| パス | ブランチ／状態 | 用途 |
| --- | --- | --- |
| `C:\Users\codea\Documents\timecode-sync-player` | `main` 58ef4a5 | 正。利用者の承認を得て統合済み。以後も統合は ff のみ |
| `...-wt-integrate-20260912` | `integrate/gpu-output-20260912`（= main） | 親の作業場。native DLL・shim・素材（`artifacts/media`、`artifacts/media/v1`）配置済み、ビルド済み。`TestResults/v1` に V1 の run |
| `...-wt-verify-oe-20260911-1344` | `integrate/s1s2-20260912`（= main `fa77d0b`） | 親の検証ビルド用（報告 SHA へ `checkout --detach` して使う）。`vendor/Spout2` あり（shim ビルドに必要） |
| `...-wt-output-engine-20260911-1247` | `codex/gst-validation-20260912` d347673 | OpenCode の作業場。**親は書き込まない** |
| `.superpowers/worktrees/session-refactor` | `refactor/session-lifecycle` 46bd759 | 旧作業場。`TestResults/gpu-app-20260911`（段階 0〜5 の生 run、gitignore）と `TestResults/gpu-mutex-retry-session-20260910T0752Z/environment.md`（タイムライン原本）が残る |
| `...-wt-gstreamer-20260911-0141` | 5eb7e62 | 旧 GStreamer 移行ブランチ。参照のみ |

## 4. 実機試験の規則（利用者の指示、厳守）

- 直列に 1 本ずつ。OpenCode が GPU 試験中（報告に開始時刻が出る）は親は走らせない。逆も同じ（親の試験中は指示を送らない）。
- console セッションであること（`query session` に `>console`。RDP 中は不可）。自分が起動した PID だけ終了する。プロセス名での kill は禁止。
- 新しいネイティブ経路は 1080p 短時間 → 4K の順。ビルドや重い処理を性能試験と同時に走らせない。
- 異常（フリーズ兆候、GPU エラー）が出たら試験を止めて状態を保存し、利用者に報告。GPU リセット・ドライバー操作・OS 再起動・WPR/UAC 採取はしない。
- main への書き込みは統合（ff）のみ。stash・reset・clean・discard は使わない。

## 5. 検証コマンド（統合 worktree を例に）

```powershell
# 1) 報告 SHA へ（検証 worktree）
git -C C:\Users\codea\Documents\timecode-sync-player-wt-verify-oe-20260911-1344 checkout -q --detach <SHA>
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

## 6. OpenCode（Herdr）の扱い

- 状態: `herdr pane get w5:p3`（`agent_status` は working／idle／done／blocked）。blocked は許可プロンプトのことが多い。`herdr pane read w5:p3 --source visible` で内容を見て、`herdr pane send-keys w5:p3 Enter`（Allow once）や `Right` → `Enter`（Allow always）で応答する。検証 worktree への読み取りは「常に許可」済み（OpenCode 再起動まで）。
- 監視: 30 秒ごとに `agent_status` の遷移を出す Monitor を前任が使った（`herdr pane get` を while ループで回すだけ）。後任も同じで良い。
- 報告の型: コミット SHA、変更ファイル、非E2E 件数、実機の時刻と指標、設計差異、未検証。設計差異は必ず不変条件と照合する。
- コンテキストが 80% を超えたら、次の指示は自己完結した内容にする（圧縮で失われても続けられるように）。

## 7. 未完了と次の順（2026-09-15）

1. **V8-D の検証** → V8 確定。MMCSS を既定で有効にする変更と、未検証だった 3 点
   （失敗経路・強制終了時の revert・長時間運転）。長時間は **V6 の 60 分に相乗り**。
   検証では**負の対照**（既定を無効に戻すと落ちが増える）を必ず取ること。
2. **V3 を採用構成で 1 回だけ再測定** → V3 判定。
   基準は「定常誤差は mpv×Gpu と同等以下、回復時間はシーク p95 250ms 以下・ギャップ再入 p95 500ms 以下」。
   **mpv の基準値は取得済み**（`TestResults/d4v/d4-mpv`: 平均 -93.3ms、ばらつき 97.3ms、n=1070）。
3. **V4**（ギャップ 3 種）。**実施方法の判断待ち。**
4. **V11**（decodeMode の実装 → a〜d の検証）。
5. **コミット A（mpv 除去）**。棚卸しは `docs/V04-SCOPE-mpv-removal-decode-mode.md` の冒頭にある。
   **`IMpvApi` / `IMpvRenderApi` / `GstMpvApiAdapter` / `GstMpvRenderApiAdapter` は消さない**
   （GStreamer の現役経路。コミット B で名前だけ変える）。
   **出力エンジンの mpv スナップショット経路**（`UploadPendingSnapshot` / `snapshotInput` /
   `WaitUntilOrStopOrSignal` の signal）は**ファイル削除では残るので個別に消す**。
6. **コミット B（名前整理）** → 既定値切替 → インストーラー（GStreamer ランタイム同梱。**配布方式が判断待ち**）
   → 全 E2E → `docs/SETUP.md` と `docs/verification-checklist.md` → v0.4 リリースノート草案。
7. 積み残し: **D6**（解像度変更時のデバイス消失）、V8 の落ちの根治（既知の仕様として保留）、
   120Hz 表示先（機材なし）、HAP（v0.4 対象外）。

### 利用者の判断待ち（2026-09-15 時点）

1. **V4 の実施方法** — 現場のプロジェクトファイルを受け取るか、親が構成したもので代替するか。**次の作業単位に入る**
2. **GStreamer ランタイムの配布方式** — 同梱／インストール後にダウンロード／別途インストール。
   **インストーラー更新の前に必要**。親の調査では公式ランタイムの ffmpeg は LGPL-2.1-or-later で、
   GPL なのは x264・x265・a52dec（再生経路では不使用）。ただし法務的な確認は別途

## 8. 主要文書

- 設計: `docs/OUTPUT-GPU-DESIGN-CONFIRMED.md`、`docs/OUTPUT-PIPELINE-DESIGN.md`、`docs/OUTPUT-GPU-INTEGRATION-PLAN.md`、`docs/GPU-SOURCE-CONTRACT-SPEC.md`、`docs/CANVAS-PLACEMENT-SPEC.md`、`docs/OUTPUT-GPU-STAGE4-5-SPEC.md`、`docs/OUTPUT-GPU-INVARIANTS.md`
- 記録: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`（**V1〜V11 の結果・判定基準・欠陥 D1〜D6 の全記録。最重要**）、`docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md`、`docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md`
- v0.4 の範囲: `docs/V04-SCOPE-mpv-removal-decode-mode.md`（mpv 除去の棚卸し・decodeMode の設計・V11）、`docs/release-0.4-plan.md`（完了の定義・既知の仕様・判断待ち）
- 指示の写し: `docs/prompts/`
- shim: `native/gst-shim/README.md`（別デバイス・共有リング・H-3 規則）
- コード側引き継ぎ: `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`
