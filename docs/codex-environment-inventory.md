# Codex 環境の棚卸し

## 整理後の運用（2026-09-08）

- `AGENTS.md` を161行から30行、7,316バイトから1,838バイトへ縮小。作業規約、ビルド・テスト、実装上の要点と必要時の参照先を残した。
- 専用レンダースレッドと DLL 名を現状に合わせた。過去の計画書・棚卸しを一括で読み込む運用はしない。
- `.codex/config.toml` で、このプロジェクトのプラグイン、リモートプラグイン、アプリ連携、フックを無効化。直接登録の Playwright / node_repl MCP も無効化した。
- Superpowers はプラグイン全体の無効化に含まれる。既存計画書の Superpowers 必須指定も外した。
- ユーザーの希望に合わせ、`~/.agents/skills/` の16種類を正本として残した。`~/.codex/skills/` の重複コピー11件はスキル探索対象外のバックアップへ移動し、共通 config.toml に一時追加していた `[[skills.config]]` も削除した。`~/.codex/skills/.system/` は変更していない。

確認には PATH 上の Codex CLI 0.144.6 とアプリ同梱の 0.153.4 を使用した。プロジェクト設定でプラグインを無効化した後、重複スキルを共通設定で無効化し、アプリ同梱版の `debug prompt-input` でユーザースキル16種類とシステムスキル5種類の計21件が重複なく表示されることを確認した。設定値の読み取りでも `plugins` / `remote_plugin` / `apps` / `hooks` が false であることを確認した。

削除前のバックアップは `~/.codex/backups/skill-dedup-20260908/`。ZIP、移動済みフォルダ、変更前の config.toml を保存している。戻す場合は `removed-folders` の11フォルダを `~/.codex/skills/` へ戻す。全プロジェクトで `~/.agents/skills/` の16種類を引き続き使用できる。

現在の会話に既に渡されたスキル一覧・履歴は、この変更では消えない。Codex を再起動し、このリポジトリで新しい会話を始めて運用する。アプリやサービス側が別途注入する指示・ツールまでゼロになることは保証しない。

戻す場合は `.codex/config.toml` の該当する false を true にするか、該当設定を除く。スキル・プラグイン本体やメモリーは削除していない。設定ファイルは trusted なプロジェクトで適用される。[設定の適用順](https://learn.chatgpt.com/docs/config-file/config-basic)

GPT-6 Astra の公式ガイドは、スキルや AGENTS.md の指示への感度を踏まえ、曖昧・競合する指示の棚卸しを勧めている。「スキルなしが常に高性能」という保証ではない。[Astra のガイド](https://developers.openai.com/api/docs/guides/latest-model)

以下は整理前の記録。件数・未追跡状態などは整理前のスナップショットとして読む。

## 整理前の棚卸し

確認日: 2026-09-08（このセッションで確認できた状態）

対象: timecode-sync-player の作業規約、ユーザー共通のスキル・プラグイン設定、ローカルのメモリー保存状況。
「設定で有効」「ファイルが存在」「現在のセッションに公開」「過去の開発で使用済み」は別の状態として扱う。
過去の全会話を調べた使用履歴ではない。設定変更、インストール、削除は行っていない。

## 1. プロジェクト専用の設定・知識

| 項目 | 確認結果 | 開発への影響 |
|---|---|---|
| `AGENTS.md` | 存在し、このセッションにも指示として提供。調査開始時から Git 未追跡 | 日本語コミット、Windows / Debug、ビルド・テスト手順、mpv の注意点を指定 |
| `CLAUDE.md` | Git 追跡済み。AGENTS.md とほぼ同内容だが DLL 説明に差 | 別エージェント用の文書。Codex の自動適用対象とは断定しない |
| `.agents/skills` / `.codex` | プロジェクトルートに存在しない | プロジェクト専用スキル・config.toml は確認できない |
| 上位の `AGENTS.md` / `AGENTS.override.md` | ドライブ直下からプロジェクトまでとユーザーの `.codex` で該当する追加ファイルなし | この経路で追加される作業規約は確認できない |
| `docs/ARCHITECTURE.md` | Git 追跡済み | 現在の専用レンダースレッド構成などを記載 |
| `docs/SETUP.md` / `docs/verification-checklist.md` | 存在 | セットアップ・実機検証の参照先 |
| `docs/test-coverage-progress.md` / `docs/test-coverage-roadmap.md` | Git 追跡済み | 実施記録・次の作業・実行環境の注意点 |
| `docs/superpowers/plans/2026-07-18-v0.3-review-fixes.md` | Git 追跡済み | Superpowers のスキルを使う計画書が残っている |
| `.superpowers/plans/` | 計画書2件。`.gitignore` により除外 | 別環境への clone ではこの計画書を引き継げない |

`.superpowers/plans/` にあるファイル:

- `mainwindow-sync-extraction-plan.md`
- `test-coverage-plan.md`

AGENTS.md の更新候補:

- スレッドモデルに専用レンダースレッドの説明がない。現在の `docs/ARCHITECTURE.md` と `MainWindow.xaml.cs` の `RenderThreadExecutor` 使用に合わせる。
- DLL の表は `mpv-2.dll` のみ。`native/README.md` と `CLAUDE.md` は上流名 `libmpv-2.dll` と従来名の互換対応を説明している。
- 内容を確認して Git 追跡対象にする。今回の棚卸しではステージ・コミットしていない。

## 2. スキル

現在のスキル一覧に WPF / .NET / LTC / mpv / Spout 専用スキルは見当たらない。
通常開発の中心は AGENTS.md、ソース、開発ドキュメント、PowerShell / dotnet になる。

スキルは一覧に出ているだけで全本文が常時使用されるわけではなく、必要時に SKILL.md が読み込まれる。同名スキルも自動統合されない。[公式説明](https://learn.chatgpt.com/docs/build-skills)

### ユーザー共通スキル

次の11種類が `~/.agents/skills/` と `~/.codex/skills/` の両方に存在し、このセッションの一覧にも二重に載っている。いずれもシンボリックリンクではない。

| スキル | 2つの SKILL.md の比較 | このプロジェクトとの関係 |
|---|---|---|
| agents-sdk | 同一 | Cloudflare 開発用 |
| cloudflare | 同一 | Cloudflare 開発用 |
| cloudflare-email-service | 差分あり | メール連携用 |
| cloudflare-one | 同一 | Zero Trust / SASE 用 |
| cloudflare-one-migrations | 同一 | ネットワーク移行用 |
| durable-objects | 同一 | Cloudflare 開発用 |
| sandbox-sdk | 同一 | Cloudflare 開発用 |
| turnstile-spin | 差分あり | CAPTCHA 導入用 |
| web-perf | 同一 | Web 性能分析用 |
| workers-best-practices | 同一 | Cloudflare 開発用 |
| wrangler | 同一 | Cloudflare CLI 用 |

比較は SKILL.md 本文の SHA-256 と差分による。付属スクリプト・参照資料全体の同一性は未検証。
差分は `cloudflare-email-service` のエージェント例が Codex / Claude Code、`turnstile-spin` の保存先例が `.Codex/skills/...` / `.claude/skills/...` となっている点。

`~/.agents/skills/` のみにある5種類:

| スキル | 記録されている導入元 | 用途 |
|---|---|---|
| find-skills | vercel-labs/skills | スキル探索 |
| grill-me | mattpocock/skills | 設計への質問 |
| grill-with-docs | mattpocock/skills | 設計への質問と文書化 |
| grilling | mattpocock/skills | 設計への質問 |
| herdr | ogulcancelik/herdr | Herdr の明示的な操作依頼時 |

導入元は `~/.agents/.skill-lock.json` による。3つの grill 系は説明上の用途が重なるため、整理時に使い分けを決める候補。

### システム・プラグイン由来で公開されているスキル

| 提供元 | このセッションのスキル一覧 |
|---|---|
| システム | imagegen、openai-docs、plugin-creator、skill-creator、skill-installer |
| Computer Use | computer-use |
| Deep Research | deep-research |
| Documents | documents |
| Frontend Design | frontend-design |
| Google Drive | google-drive、google-docs、google-sheets、google-slides、google-drive-comments |
| PDF | pdf |
| Plugin Management | plugin-management |
| Presentations | Presentations |
| Product Design | index、audit、ideate、image-to-code、url-to-code |
| Sites | sites-building、sites-hosting |
| Spreadsheets | Spreadsheets、excel-live-control |
| Template Creator | template-creator |
| Visualize | visualize |

今回の棚卸しで手順として使用したスキルは OpenAI Docs と Plugin Management。
Web、Cloudflare、文書作成、デザイン系の多くは本アプリの通常の C# 修正には直接必要ないが、ユーザー共通の別用途があり得るため不要と断定しない。

## 3. プラグイン・MCP

`~/.codex/config.toml` にはプラグイン24エントリーがあり、20が有効、4が無効。
これはローカル設定の件数であり、アカウント側を含む全インストール数ではない。
同ファイルの本プロジェクトの項目は `trust_level = "trusted"`。

### config.toml にある全エントリー

| 提供元 | 有効 | 無効 |
|---|---|---|
| openai-curated | superpowers、google-calendar、slack | — |
| local-marketplace | — | frontend-design、context7 |
| openai-bundled | sites、browser、visualize、computer-use、chrome、codex-app-tools、unified-computer-use | — |
| openai-primary-runtime | documents、pdf、spreadsheets、presentations、template-creator | — |
| claude-plugins-official | code-review、code-simplifier、context7、frontend-design、playwright | rust-analyzer-lsp、superpowers |

### 現在のセッションとの照合

| 項目 | 観測した状態 |
|---|---|
| Context7 | ドキュメント解決・取得ツールが公開されている |
| Playwright | ブラウザー操作ツールが公開されている。config.toml に直接登録された MCP とプラグイン設定の両方がある |
| Frontend Design | スキルが公開されている |
| Superpowers | 旧 openai-curated 版は設定上有効だが、このセッションのスキル一覧にない |
| Google Calendar / Slack | 旧設定では有効だが、このセッションの該当ツールは確認できない |
| Code Review / Code Simplifier | 設定上有効。キャッシュの構成はそれぞれ `commands/code-review.md` と `agents/code-simplifier.md`。専用スキル・ツールとしての公開は確認できない |
| Google Drive | 上記 config.toml にエントリーはないが、スキルとツールが公開されている |
| Product Design / Deep Research / Plugin Management | 上記 config.toml にエントリーはないが、スキルが公開されている |
| Sites / 文書系 / Visualize / Computer Use | スキルまたはツールが公開されている |

ツール公開は外部サービスの認証が正常であることまでは保証しない。外部データの取得や認証テストは今回実施していない。
ユーザーに提示されていた「推奨・未インストール」リストも使用中の一覧には含めていない。

Superpowers のキャッシュには次の3系統が存在する:

- `openai-curated/superpowers/3fdeeb49`：manifest の版は 5.1.3。設定上有効。
- `claude-plugins-official/superpowers/6.3.0`：設定上無効。
- `openai-curated-remote/superpowers/6.3.0`：キャッシュあり。このセッションへの公開は確認できない。

キャッシュの存在だけでは利用可能と判断できない。過去の計画書に Superpowers の参照があっても、現在の環境でそのまま実行できるとは限らない。

### 関連する共通設定

- 直接登録された MCP は `playwright` と `node_repl`。Playwright は `@playwright/mcp@latest` 指定。
- `hooks = true`。`~/.codex/hooks.json` に Herdr の SessionStart フックが1件ある。
- フックのスクリプトは `HERDR_ENV=1` かつ `HERDR_PANE_ID` がある場合に動作する。今回の環境では `HERDR_ENV=1` ではない。
- Playwright と各種ブラウザー操作機能は、WPF の FlaUI テストとは別用途。ブラウザー機能だけで WPF UI テストを置き換える構成ではない。

## 4. メモリー

| 保存先・分類 | 確認結果 |
|---|---|
| `~/.codex/memories/` | `.dotnet-home` のみ。メモリー文書は見つからない |
| `~/.codex/memories_1.sqlite` | 読み取り専用で確認。`stage1_outputs` 0件、`jobs` 0件 |
| `~/.codex/state_5.sqlite` | メモリー・stage1 名のテーブルは見当たらない |
| プロジェクトの `MEMORY.md` 等 | 主作業ツリーで該当なし。入れ子の worktree は別範囲 |
| AGENTS.md・docs・計画書 | 作業規約や開発知識を保持する明示的な文書として存在 |
| 会話履歴・ChatGPT アカウント側のメモリー | ローカル自動メモリーと別扱い。過去会話の全件調査・アカウント側の保存内容確認はしていない |

確認したローカル保存先では生成済みの自動メモリーを確認できなかった。「あらゆる場所にメモリーがない」「機能が無効」とまでは断定できない。
今後の開発で再利用できる知識は、まず AGENTS.md と現在の docs を参照する運用が確実。

## 5. 整理する場合の優先順

1. AGENTS.md のレンダースレッド・DLL 記述を更新し、Git 管理に載せる。
2. 11種類の二重スキルを整理する。9種類は本文同一、2種類は差分を確認して残す側を選ぶ。ユーザー共通なので他プロジェクトへの影響も考慮する。
3. Superpowers を継続利用するか決め、設定・実際の公開状態・計画書の依存を揃える。
4. Playwright の直接 MCP 登録とプラグイン登録の役割を確認し、重複が不要なら一本化する。現時点で2サーバーが実際に動いているとは断定しない。
5. `.superpowers/plans` のうち今後も必要な内容を Git 管理の docs へ移すか判断する。
6. プロジェクト専用スキルは、ビルド・ログ調査・LTC/Spout 実機検証などの繰り返し手順が固まってから切り出す。

この文書以外のファイル・設定・プラグイン・メモリーは変更していない。棚卸しのみのためビルド・テストは実施していない。
