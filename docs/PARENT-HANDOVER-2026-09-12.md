# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

## 1. 役割と進め方

- 親（このセッション）: 設計・指示・独立検証・記録。コードは書かない（runner／集計スクリプト・文書は書く）。
- 実装: OpenCode（DeepSeek V4.1 Flash）。Herdr の隣ペイン `w5:p3`。指示は `docs/prompts/*.md` に書いて `herdr pane run w5:p3 "<1 行: ファイルを読んで実行>"` で渡す（複数行を直接送らない）。
- 検証の型: 完了報告を `herdr pane read w5:p3 --source recent-unwrapped --lines 200` で読む → 検証 worktree を報告コミットへ `git checkout --detach <SHA>` → `dotnet build` → 非E2E → 必要なら shim 再ビルド → 実機（1 本ずつ）→ `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` 等へ追記 → 合格なら次の指示、不合格なら差し戻し（原因・証跡・合格条件を明記）。
- 不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I12）。実装側が単独で決めてはいけない事項もそこにある。プロンプトには毎回パスを含める。

## 2. 現在地

- **main は `fa77d0b`**。GPU 出力層の全段階（0〜2、6、6b、3、4、5、7）に加え、S1／S2 の shim 修正を統合済み。既定値は `PlayerBackend=Mpv`、`OutputBackend=Cpu` のまま（切替は下記 V1〜V10 合格後）。
- **進行中: 実運用検証 V1〜V10**（`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`）。**V1（コーデック行列）は 11 本すべて合格**（2026-09-12 19:10 JST）。S1（音声付き MP4）・S2（ProRes）の修正 `d347673` を検証のうえ main へ rebase して ff 統合した（`fa77d0b`、native ツリーは検証 SHA と同一）。評価は `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md` の「追記: S1・S2 修正 `d347673`」。
- **次は V2**（音声出力・ミュート・音量・速度）。V1 で新たに 2 件の追跡項目が出た（S3: MPEG-TS のシーク後にフレームが来ない〔基点でも同一の既存事象〕、GStreamer E2E が素材不足でスキップのまま）。
- 利用者は作業のため DISPLAY1（主画面）を 4K→HD に変更する予定。変更時刻を聞いて `docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md` に記録する。DISPLAY2（1920×1080／60Hz、試験の表示先）は変えない。

## 3. worktree と用途

| パス | ブランチ／状態 | 用途 |
| --- | --- | --- |
| `C:\Users\<user>\Documents\timecode-sync-player` | `main` 58ef4a5 | 正。利用者の承認を得て統合済み。以後も統合は ff のみ |
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

## 6. OpenCode（Herdr）の扱い

- 状態: `herdr pane get w5:p3`（`agent_status` は working／idle／done／blocked）。blocked は許可プロンプトのことが多い。`herdr pane read w5:p3 --source visible` で内容を見て、`herdr pane send-keys w5:p3 Enter`（Allow once）や `Right` → `Enter`（Allow always）で応答する。検証 worktree への読み取りは「常に許可」済み（OpenCode 再起動まで）。
- 監視: 30 秒ごとに `agent_status` の遷移を出す Monitor を前任が使った（`herdr pane get` を while ループで回すだけ）。後任も同じで良い。
- 報告の型: コミット SHA、変更ファイル、非E2E 件数、実機の時刻と指標、設計差異、未検証。設計差異は必ず不変条件と照合する。
- コンテキストが 80% を超えたら、次の指示は自己完結した内容にする（圧縮で失われても続けられるように）。

## 7. 未完了と次の順

1. ~~S1／S2（d347673）の検証 → main へ ff 統合~~ 完了（`fa77d0b`、2026-09-12 19:10 JST）。push は `git -c credential.helper= -c "credential.helper=!gh auth git-credential" push origin main`。
2. V2 音声（ミュート・音量・速度）→ V3 LTC 同期シーク精度（ケーブルループ E2E を GStreamer で。RDP のリモートオーディオに注意）→ V4 ギャップ 3 種 → V5 切替連打 → V6 長時間 60 分 → V7 mpv×Gpu×Spout → V8 表示先の違い → V9 起動終了復旧 → V10 旧プロジェクト読込。
3. 全合格で既定を `Gstreamer`＋`Cpu→Gpu` に切替（mpv／Cpu は退避経路として 1 リリース残す）→ HAP（`video/x-hap`、調査は `docs/HAP-GSTREAMER-INVESTIGATION-2026-09-11.md`）。
4. 積み残し: S3（MPEG-TS のシーク後にフレームが来ない。V3／V5 の前に原因を押さえる）、GStreamer E2E の素材整備（`test_720p25.mkv`・`test_720p25.avi`・`test_720p50.ts`・recv ツール）、ProRes の 60 秒素材の作り直し。軽微: L-4（lead が単発スパイクで 8ms に跳ぶ。p99＝60 標本の最大値）、4K 長尺での H-3 閾値（21ms）挙動、120Hz／複数画面、実デバイス消失。

## 8. 主要文書

- 設計: `docs/OUTPUT-GPU-DESIGN-CONFIRMED.md`、`docs/OUTPUT-PIPELINE-DESIGN.md`、`docs/OUTPUT-GPU-INTEGRATION-PLAN.md`、`docs/GPU-SOURCE-CONTRACT-SPEC.md`、`docs/CANVAS-PLACEMENT-SPEC.md`、`docs/OUTPUT-GPU-STAGE4-5-SPEC.md`、`docs/OUTPUT-GPU-INVARIANTS.md`
- 記録: `docs/OUTPUT-GPU-STAGE2-EVALUATION-2026-09-11.md`（全段階の評価、時系列）、`docs/GPU-VERIFICATION-TIMELINE-2026-09-10-12.md`、`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`（V1 結果を含む）
- 指示の写し: `docs/prompts/`
- shim: `native/gst-shim/README.md`（別デバイス・共有リング・H-3 規則）
- コード側引き継ぎ: `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`
