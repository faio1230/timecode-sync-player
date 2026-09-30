# 指示書: v0.6.0 段 5b 試験素材の ffmpeg と、ランナーの記録の追加

担当: サブエージェント B。作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v060`（`v0.6.0` の先頭まで進めてある）。
設計書: `docs/design/v0.6.0-prores-gpu.md` の 7 節の段 5b（「ランナーの記録の追加」と「試験素材の ffmpeg」）、8 節（検証）。先に読むこと。
スクリプトは段 5 で PowerShell 7 に寄せてある（`#requires -Version 7.0`）。**`scripts/inspect-gop.ps1` だけは 5.1 互換のまま**（利用者に直接実行させるスクリプト）なので触らない。

## 範囲

### A. 試験素材の ffmpeg（TSP-Fable の承認済みの案 1〜4）

1. ffmpeg の解決の順を 1 つにする: 環境変数 `TCS_FFMPEG`（ffmpeg.exe のフルパス）→ 引数 `-FfmpegDir` → PATH。対象: `scripts/make-e2e-media.ps1`、`scripts/make-heavy-media.ps1`、`scripts/capture-setup.ps1`、試験の `tests/.../Helpers/AccuracyVideoFixture.cs`（`ffmpeg` を名前で呼んでいる。ffprobe も同じ規則で `TCS_FFPROBE` か、ffmpeg と同じフォルダ）。共通の解決は、スクリプトは小さな共有関数（`scripts/` の `.psm1` か dot-source）、C# は試験のヘルパーに 1 つ
2. 使った ffmpeg の版（`ffmpeg -version` の 1 行目）を記録する: スクリプトはログの先頭の行と、素材のフォルダのサイドカー（`ffmpeg-version.txt`）。`AccuracyVideoFixture` は試験の出力（ITestOutputHelper か素材のサイドカー）
3. **新しく作る素材だけ**、版が 6 未満なら警告 1 行（既存の素材の再利用では出さない）
4. `make-heavy-media.ps1` に ProRes の素材を足す: 4K 59.94（`prores_ks` の profile 3・`yuv422p10le`、20 秒）と 1080p 59.94（同じ）。どちらも `-color_primaries bt709 -color_trc bt709 -colorspace bt709 -movflags +write_colr`。タグ無しの 4K を別名で 1 本（フォールバックの確認用）。作った素材を ffprobe で読み、色の 3 タグが bt709 になっていることを確かめる手順をスクリプトに入れる（違えば止める）
5. **既存の素材は作り直さない**（既存の E2E の素材のファイルの中身を変えない）

### B. ランナーの記録の追加（`scripts/run-ltc-scenarios.ps1` と結果 JSON `ltc-run-result/1`）

1. 事前確認で、システムのコミットの空き（`Win32_OperatingSystem.FreeVirtualMemory`）を見る。**4 GB 未満なら始めない**（C: の空き 20 GB の確認と同じ扱い・同じ書き方）。開始時の空きを JSON に `commitFreeGbAtStart`
2. 各シナリオの終わりのアプリの終了で、「終了を押してからプロセスが消えるまでの秒数」を集める。試験の側（E2E のヘルパー）が終了を押して待っているなら、その時刻を試験の成果物（journal など）に出し、ランナーが集計する。JSON に `appExit`: `{ count, medianSeconds, maxSeconds, over15s: [ { test, seconds, logLines } ] }`。15 秒を超えた回は、アプリのログの終了の段の行（`tcs_player_destroy` の段ごとの時刻の行と、`ExitCoordinator` の行）を添える
3. ProRes のロードの内訳: tcs-gst のログの `load.summary … profile=prores-gpu|prores-cpu` の数と、`decoder-adapter-mismatch` の発火の回数を JSON に `prores: { gpu, cpu, adapterMismatch }`
4. 既存の JSON の項目の名前と意味は変えない（追加だけ）。`schema` は据え置き（`ltc-run-result/1`）で項目を足す

## してはいけないこと

- push、`main`・`v0.6.0` への書き込み、stash・reset・clean
- **実機の試験**（アプリの起動、E2E、LTC シナリオ、ランナーの実行、`-PreflightOnly` も含む）。非E2E とスクリプトの構文の検査まで。素材の生成は、作業ツリーの一時フォルダへなら実行してよい（ProRes の 2 本と既存の 1 本で、版の記録とタグの確かめが働くことを見る）
- **空振りで `-?` を使わない**。構文は `[System.Management.Automation.Language.Parser]::ParseFile` で errors=0 を見る（実行しない）。引数なしで実機まで進むスクリプトの一覧は `docs/reports/2026-09-30-agent-a-v060-stage5.md` の 13 節
- 製品のコード（`src/`、`native/gst-shim/src`）の変更
- `scripts/inspect-gop.ps1` の変更
- 素材の作品名・ローカルの絶対パス（利用者名）をコミットするファイルに書くこと

## 完了の条件

- 全 `.ps1` の構文 errors=0（pwsh の Parser）、CRLF、制御文字なし
- 素材の生成を一時フォルダで実行して: 版のサイドカーができる、ProRes の 3 本（4K タグ付き・1080p タグ付き・4K タグ無し）ができて、タグ付きは ffprobe で bt709、タグの確かめが効く（わざとタグを付けない呼び方で止まることを 1 回）
- `TCS_FFMPEG` の優先が効くこと（開発機の 2 つの ffmpeg: PATH の先頭の古い版と `Program Files\ffmpeg\bin` の版のどちらを指しても、記録された版が指した方になる）
- ランナーの JSON の追加の項目は、既存の実行の成果物（`timecode-sync-player-v06\artifacts\analysis-data\pwsh-preflight`、読むだけ）か、試験のための小さな偽の成果物で、集計の関数を単体で確かめる（ランナー本体は回さない）
- 非E2E が全件合格
- 日本語のコミット（A と B で分ける）
- **終わったら、そのツリーでビルドやファイルの書き換えをしない**

## 報告

`docs/reports/2026-09-30-agent-b-v060-stage5b.md` に事実だけ。コミットの一覧、項目ごとの変更、素材の生成の結果（版・タグ）、JSON の追加の項目の例、未解決の点、親が実機で確かめる手順（ランナーを 1 周したときに見る JSON の項目）。
