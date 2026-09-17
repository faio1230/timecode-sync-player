# 親（設計・検証役）の引き継ぎ（2026-09-12 13:40 JST）

前任: Claude Fable 5.1（コンテキスト上限のため交代）。後任はこの文書と `docs/HANDOVER-GPU-OUTPUT-2026-09-12.md`（コード側の引き継ぎ）、メモリ（`~/.claude/projects/C--Users-codea-Documents-timecode-sync-player/memory/`）から再開する。やり取りは日本語。

> **2026-09-18 14:30 更新（24 回目）**: 候補ビルド `0.4.2+c3f3cd3` を Taildrop で検証機へ送付済み（実素材 3 回のうち a = 25/12、b・c は実行中。詳細報告待ち）。その後に統合: D29（Single の clamp を MediaIn/MediaOut に、`79f2207`）、ランナーのログ複製（`6d71dbb`）。`w5:p6` = D30（誤デコードの単発 Jump をそのまま適用しない妥当性ゲート、`d8e9fdf`）の実機確認中（C-2 ×3、R、S-2、V3）。`w5:p3` = 新セッションで待機。D30 が通ったら統合 → **2 本目の候補ビルド**を Taildrop で送る → 検証機の実素材が通れば 0.4.3 に上げて公開（草案は `docs/release-notes/v0.4.3.md` と `CHANGELOG-0.4.3-draft.md`）。
>
> **2026-09-18 10:40 更新（23 回目）**: **製品側の既知の欠陥はすべて main に統合**（D24/D25/D25-b/D25-c、D26/D26-b、D27〜D27-d、D28、D20〜D22、D21-b、D20-b。main `d3f613d`）。`w5:p6`（新セッション）が統合後 main で全件確認中（シナリオ 22、LTC ループ 14、V4、E2E 全件）。`w5:p3` は 0.4.3 のリリースノート草案（版は上げない）。全件が通ったら親が配布物（0.4.2 のまま、SHA 識別）を作り Taildrop で検証機へ → 実素材 → 合格で 0.4.3 に上げて公開。
>
> **2026-09-18 01:15 更新（22 回目）**: 統合済み: D20〜D22、D21-b、D20-b、D26/D26-b（ジャンプ時の黒）、D27/D27-b/D27-c（保持 LTC の停止/ランスルー）、D28（合成の完了待ち）、S-1 判定、参照採取の取り直し、検証機のランナーパッチ（D23-b/c/d、-Media、-MediaInOffsetSeconds）。**残る製品欠陥は shim の D25（シーク直後の古いフレーム、一時停止シークで高頻度）と D24（長 GOP のポンプ予算）**。`w5:p3`（同期担当、新セッション）が D25 → D24 を作業中。`w5:p6`（除去担当、68%）は待機。D25/D24 が入ったら配布物（0.4.2 のまま、SHA 識別）を Taildrop で検証機へ送り、`-Media M1,M4,M6 -MediaInOffsetSeconds 5` と `M1,M3,M5` で実素材確認 → 合格なら 0.4.3。
>
> **2026-09-17 18:15 更新（21 回目）**: LTC 同期 26 項目（+黒なし、停止/ランスルー）の自動検証を進行中。行列と進捗は `docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md`（4.5 節に時系列）。統合済み: D20〜D22、D21-b、D20-b（+S-4 ゲート）、D23〜D23-d、ランナー `run-ltc-scenarios.ps1`（`-AppExe -MediaDir -Media`）、シナリオ E2E 18 本。
> 進行中: `w5:p3` = D27（保持 LTC を停止/ランスルーで扱う。利用者決定「モード依存」）→ 次に D24/D25（shim: 一時停止シークのポンプ予算、リング PTS と画素の食い違い）と D29 候補（Single の MediaOut）。`w5:p6` = D26（ジャンプ時の黒。Held を合成側所有の複製に）→ 次に D28（合成の GPU 完了待ち >100ms で再生停止）。
> 検証機（`TSP-TestMachine`、Remote Control）はテスト基盤（ランナー・生成スクリプト）を自分で直しパッチを Taildrop で送る（受信は `tailscale file get`。GUI が起動中だと `~/Downloads` へ自動保存される）。実素材の次の実行は D24〜D28 を含む次の配布物（Taildrop、版は上げない）を送ってから。
>
> **2026-09-17 09:55 更新（20 回目）**: **履歴を再度書き換えた**（素材の作品名・ファイル名の除去。それ以前の SHA は文書中のものも含めて無効。main `adcb6cb`、agent-a `5c61981`、agent-b `0763553`、タグ v0.4.0〜v0.4.2 も付け替え）。素材は記号（実素材 M1〜M7、テスト素材 A〜C）だけで書く（利用者の指示、メモリ `no-media-titles-in-public-repo`）。
> v0.4.2 (beta) 公開済み（D16 ハイブリッド GPU、D17 ロード時間）。検証機は Remote Control の `TSP-TestMachine`（Tailscale で配布物を渡す。公開は検証後）。版番号は直ってから上げる（利用者の方針）。
> 進行中: LTC 同期 26 項目の自動検証（`docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md`）。`w5:p6` = シナリオ E2E 19 件 + 参照フレーム判定 + プロジェクト生成、`w5:p3` = ランナー `run-ltc-scenarios.ps1` + D18（実装済み、実機 1 回待ち）。
>
> **2026-09-17 02:35 更新（19 回目）**: **v0.4.0 を GitHub に Pre-release として公開した**（タグ `v0.4.0`、main `6c7909d`、タイトル `v0.4.0 (beta)`、zip と setup.exe を添付。SHA-256 は `docs/release-0.4-plan.md` 2.5 節）。
> 直前に H2（`-ClickPlay` を再生保証へ、agent-a `a8fb411`）を統合（`d2a8a88`）、配布ビルドの起動確認 2 本（60fps、`already playing`、exit 0）を親が実施。main の bin は Debug の shim に戻してある。
> **v0.4 の作業はすべて完了。** 両ペインは待機中。残りは利用者の判断待ち（現場素材の一覧、V4 の実素材追試、D9、decodeMode の設定形式）で v0.4.1 の候補として `docs/release-0.4-plan.md` 4 節に残す。
> 後任が次に何かするなら、現場からの不具合報告か上記の判断が来てから。
>
> **2026-09-17 06:10 更新（18 回目）**: V6（60 分）を `w5:p3` が 00:47:55 開始（終了予定 01:49）。リリースノート本文 `docs/release-notes/v0.4.0.md`、手順 `docs/RELEASE-PROCEDURE-0.4.md`。
> Inno Setup は `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`（package-release.ps1 が自動検出）。V6 の後: `package-release.ps1` → SHA-256 記録 → 展開して起動確認 → `git tag v0.4.0` → `gh release create --prerelease`。
>
> **2026-09-17 05:40 更新（17 回目）**: **段 5 後半・D11・F1 を統合（`85f855b`）。バージョン 0.4.0。E2E 全件 63/63（agent-b の環境）、V4・V5 合格。** 残りは V6 60 分（`w5:p3` が実行中）→ 配布物作成（`scripts/package-release.ps1`）→ タグ・公開（`docs/RELEASE-PROCEDURE-0.4.md`）。
> V6 の間はビルド・実機を止める。
>
> **2026-09-17 04:10 更新（16 回目）**: 段 5 後半（文書・CHANGELOG 0.4.0・バージョン 0.4.0・配布物確認）は agent-b `f787393` で完了報告、統合待ち（E2E 全件 63/63 の確認後）。
> Q3 統合済み（`093de7e`）、main の E2E 全件 59/0/10（親のツリー）。V5 シーク連打 合格。V4 は代替プロジェクトでテストの期待値（境界 − 1 フレーム）がサンプル時計と衝突 → 境界 − 2 フレームへ直して再実行中。
> 残り: V4 再実行 → 除去担当の E2E 全件 → V6 60 分 → 段 5 後半の統合 → リリース手順（`docs/RELEASE-PROCEDURE-0.4.md`）。
>
> **2026-09-17 02:30 更新（15 回目）**: **段 4 完了・統合（`72d0ebb`）。v0.4 の本体作業（mpv 除去・CPU 合成除去・型付き API）は main で完了。** 残りは段 5 後半（文書・配布物・リリースノート、`docs/prompts/2026-09-17-STAGE5B-docs-release.md`、`w5:p6`）。
> テスト側の欠陥 Q3（`ClosesGracefullyDuringPlayback` が段 3 で消えたログ行を待ち、古いログで偽合格）を `w5:p3` が修正中。`LtcHardwareLoop` の 1 件は全件でだけ落ちる不安定さの疑い（要観察）。Q2（一時ファイルの分離）と H1、D10 も統合済み。
>
> **2026-09-17 01:10 更新（14 回目）**: 段 4 順序 1〜4 を main `9ef7913` へ統合（型付き再生 API 追加と移行。文字列経路はまだ残置）。`w5:p6` は新セッションで順序 5（削除・改名）。
> `w5:p3` は H1（ハーネスの seek 例外でアプリが残る件）の修正 `666b0f8` を実機で検証中。
>
> **2026-09-17 00:45 更新（13 回目）**: D10 を統合（`b5af872`）。段 4 は順序 1〜3 をコミット済み（`6262fd7` / `39b0a0a` / `3cebdb1`、非E2E 1709、各段の E2E 一部合格）、順序 4 は実装済みで E2E 待ち。
> 順序 5（削除・改名）の前に `w5:p6` のセッションを新しくする（コンテキスト 71%）。`w5:p3` は H1（`Invoke-AppGpuTrial.ps1 -SeekAtSeconds` の例外でアプリが残る）の修正中。
> 親の罠: .ps1 を Python で書き換えると LF 化・制御文字混入で壊れる（メモリ `ps-script-edits-crlf-and-escapes`）。
>
> **2026-09-17 00:10 更新（12 回目）**: **段 3 完了・統合（`d5af2a6`）。mpv も CPU 合成も main から消えた。** `w5:p6` は段 4（型付き再生 API、`docs/prompts/2026-09-16-STAGE4-typed-playback-api.md`）に着手。
> SH1-b で **D10** を確定（B フレームあり MP4 の accurate シーク後、qtdemux が PTS を先頭 DTS 分ずらす。shim は stream time を使うべき）。`w5:p3` が D10 を shim 側で修正中（`docs/prompts/2026-09-17-D10-qtdemux-seek-timestamp-shift.md`）。
> 残りは段 4 → 段 5 後半（文書・配布物・リリースノート）。
>
> **2026-09-16 22:50 更新（11 回目）**: U1 は再適用の古い age が原因（UI の滞りではない）と確定し修正を統合（`49bbd24`）。S4（段 4 の設計調査）を統合し 7 項目を判断済み（`docs/V04-STAGE4-TYPED-API-SURVEY-2026-09-16.md` 10 節。結果型・段階移行・osd 削除ほか）。
> `w5:p3` は段 5 前半（文書から mpv 前提を外す。`docs/prompts/2026-09-16-STAGE5A-docs-mpv-removal.md`）。`w5:p6` は段 3 実装中（最初のコミット `80dd18f`）。
> 段 4 の実装指示は段 3 統合後に出す。
>
> **2026-09-16 21:30 更新（10 回目）**: **段 2 完了。mpv は main から消えた**（`0943e2f`。再生経路・実装・DI の削除、未接続中は明示的な NotReady、v0.3 設定の互換。E2E 全件 63/63、V3 sample -28.5 / 35.0）。
> 段 2 前半の移設は `74276cf`。`w5:p6` は新セッションで**段 3（CPU 合成の除去、`docs/prompts/2026-09-16-STAGE3-cpu-compose-removal.md`）**に着手。
> `w5:p3` は新セッションで U1（コンボ切替時の age 警告 1.5 秒。前セッションの「UI の滞り」説を再適用経路の説で上書きする可能性）。
> 残りは段 3 → 段 4（型付き API）→ 段 5（文書・配布物・リリースノート）。
>
> **2026-09-16 20:40 更新（9 回目）**: **V3 の基準の時刻は sample 解析が正式**（利用者の決定。数字は変えない。1 フレーム定数は足さず `SyncOffset`）。
> sample 基準の正式記録（LTC 4 種）を親が揃え、**V3 合格**を再確定（検証記録の末尾）。Q1（`ProjectRoundTrip` の既存失敗）はテスト側の修正で解消し統合（`ffc2dde`）。
> **E2E 全件の既存失敗は 0 になった見込み**（段 2 後半の E2E 全件で確認する）。`w5:p3` は Q1 完了・次の指示待ち（コンテキスト 82%、次は `/new`）。
>
> **2026-09-16 20:00 更新（8 回目）**: 段 2 前半（移設 `10137dc`）を main `74276cf` へ統合（非E2E 2013）。`w5:p6` は段 2 後半（mpv の削除、`docs/prompts/2026-09-16-STAGE2-mpv-removal.md`。未接続中は明示的な NotReady＝案 1）。
> `w5:p3` は Q1 の単独 3 回実行中。ローカルの旧オブジェクトは reflog expire + gc で削除済み（利用者の指示）。
>
> **2026-09-16 19:30 更新（7 回目）**: **履歴を書き換えた**（公開リポジトリからローカルパスを消すため。`git filter-repo`、全コミットの SHA が変更、
> main と v0.1.0〜v0.3.0 を force push、shim の `build-debug` も履歴から除去）。**この文書や検証記録にある 19:00 以前の SHA は無効**（対応: main `0e87d81`、agent-a `b752e48`、agent-b `76417cf`）。
> T2 は段 3 まで判定して main へ統合。`w5:p3` は Q1（`ProjectRoundTrip` の既存失敗の切り分け）、`w5:p6` は段 2 前半（移設）。
>
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
| `w5:p3`（同期担当、新セッション） | Q3・V5・V4 は統合済み。**V6 60 分**を実行中 | 実機使用中 |
| `w5:p6`（除去担当） | 段 5 後半・D11・F1 は統合済み（`85f855b`）。次は V6 の後に配布物作成 | 待機（V6 中はビルド禁止） |

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

5. **shim の API を変えたコミットを取り込んだ作業ツリーは、shim を自分で再ビルドするまで E2E が壊れる**（D8 修正 2 の `tcs_player_ring_epoch`）。
   症状はアプリログの `EntryPointNotFoundException: Unable to find an entry point named 'tcs_player_ring_epoch' in DLL 'tcs_gstreamer.dll'` と、
   一時停止待ちなどのタイムアウト。他ツリーの DLL は流用せず `build-shim.ps1 -Config Debug` → `dotnet build` で bin を更新する
6. **E2E を複数まとめて回すとき `TIMECODE_ACCURACY_REPORT_DIR` を共有すると 2 本目が即失敗し、アプリが孤児になって出力パイプを掴む**（親が 3 時間止まった原因）。
   run ごとに別ディレクトリか、変数を設定しない。孤児は自分が起動した PID だけ止める
7. **実機ハーネスに他ツリーの素材を相対パス（`..	imecode-sync-playerrtifacts\media\...`）で渡すと、アプリが解決できず素材なしで起動し、ハーネスは終了を待ち続けて 1 時間止まる**
   （D10 の 1080p run、2026-09-16 20:56〜21:54。アプリログ `loadfile 失敗 err=all video profiles failed`）。素材は自分の作業ツリーに置き（`scripts\make-e2e-media.ps1`）、作業ツリー内のパスで渡す。
   親は 1 時間監視の watcher が切れてから気づいた。**ハーネスの `-Seconds` を過ぎても runner が返らないときは、まずアプリログの loadfile を見る**
8. ~~**`Invoke-AppGpuTrial.ps1 -SeekAtSeconds` はランナーが返らない**~~ **H1 で修正（`666b0f8`、main 統合済み）**: SeekBar は 0..1 の正規化値なので 15 を渡すと UIA の SetValue が例外になり、終了シーケンスが飛んでアプリが残っていた。今は起動前に値域を検証し、mark ごとに try/catch、finally で所有アプリを必ず閉じ、`-OverallTimeoutSeconds`（既定 Seconds+90）で打ち切る。旧記述:（2026-09-16 21:57 の run、素材読込は成功・アプリは一時停止のまま・`harness.jsonl` 空のまま 38 分）。
   `-ClickPlay` なしで起動した一時停止中のアプリに対する seek の UIA 操作で固まる見込み（未調査。親のスクリプト）。当面 `-SeekAtSeconds` は使わない
9. **`Invoke-AppGpuTrial.ps1 -ClickPlay` は「再生を保証」ではなく BtnPlay のトグル**（`PlayPauseCommand.TogglePlayPause`）。`--open` で読み込んだアプリは即再生するので、
   `-ClickPlay` を付けると約 10 秒後に**一時停止**する（V6 の 1〜2 回目、2026-09-17 00:47〜01:06。perf 行が 2 本で止まり、読み取りバイトが 9 分間 0）。
   V5 の 1 回目と D10 の 1080p run も一時停止状態だった疑い（シーク着地の証拠は有効、公開継続の証拠は無効）。V5 は `-ClickPlay` なしでやり直す。
   ハーネス側は「ボタンの状態を見て再生を保証する」に直す（H2、V6 の後）

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
