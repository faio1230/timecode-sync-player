# v0.5.4 段 0: シーク経路の門の発火回数と遅延の基準を取る（担当 w5:p3）

作業ツリー: `C:\Users\<user>\Documents\timecode-sync-player-v053`（ブランチ `agent-a-v054`、v0.5.4 から切った）。
OpenCode はこのフォルダで起動し直すこと。git への書き込みは禁止（親がコミットする）。

根拠: `docs/design/v0.5.3-d38-seek-gates.md` の §3（24 門の表）と §9（前後比較）、`docs/GOAL-v0.6.md` の 0 節と 1 節の v0.5.4 の行、
`docs/release-0.5-plan.md` の v0.5.4 節。v0.5.4 は安定版で、門の統合の前後で「平常時の遅延が悪化しないこと」と「門の数と遅延が減ること」を示す必要がある。
この段はその「前」を固定する。

## やること

1. **門ごとの観測点の棚卸し**（`docs/design/v0.5.4-gate-baseline.md` を新規作成）
   - §3 の 24 門それぞれについて、いまのログで「発火した」ことと「足した遅延」が数えられるかを表にする
     （門の番号、ログの行（書式）、数えられる／数えられない、足りないもの）
2. **足りない観測点をログ 1 行ずつ足す（振る舞いは変えない）**
   - 数えられない門にだけ、発火したときに `Debug` のログを 1 行足す。書式は `sync.gate <門の短い名前> <値>` にそろえる
     （例 `sync.gate pending-timeout elapsedMs=…`、`sync.gate untrusted-defer …`）
   - 新しい門・新しい条件・新しい時間定数は足さない。既存の条件の分岐の中に 1 行置くだけ
   - 既存のテストの期待値は変えない（変わる場合は止めて報告）
3. **基準の測定**
   - 標準の素材で LTC シナリオ（`scripts/run-ltc-scenarios.ps1`、`-Filter 'FullyQualifiedName~LtcScenarioE2ETests&FullyQualifiedName!~L2_&FullyQualifiedName!~L3_'`）を 2 周
   - 重い素材（`-MediaDir C:\Users\<user>\Documents\timecode-sync-player-v05\artifacts\media-heavy -Media M1,M3,M4 -MediaInOffsetSeconds 5`）を 1 周
   - アプリのログから、シナリオごと・門ごとに「発火回数」と「足した遅延（中央・最大）」を表にする。数え方（どのログ行をどう対にしたか）も書く
   - あわせて §9 と同じ指標（同期シーク、着地シーク、pending の時間切れ、通り過ぎの戻し、同じ目標への 3 本目以降、着地の遅れ、黒フレーム、R-1・R-2 の一時停止の遅れ）を同じ表に載せる
4. `docs/design/v0.5.4-gate-baseline.md` に 1〜3 を書く。平常時に一度も発火しない門は「平常時 0」と明記する（統合の候補の判断材料になる）

## 確かめること

- 非E2E（`--filter "FullyQualifiedName!~E2ETests" --logger trx`）全件、E2E（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）全件
- ログを足した前後で、LTC シナリオの結果（合格数）が変わらないこと
- 生ログは親のレビューが済むまで消さない。試験データは `artifacts\analysis-data` に置く
