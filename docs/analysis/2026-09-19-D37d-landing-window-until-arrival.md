# D37-d: 着地窓を「誤差が許容内に入るまで」開く（2026-09-19）

実装: `src/TimecodeSyncPlayer/TimecodeSyncService.cs`、`SyncDecisionEngine.cs`。表示・制御則の
分岐は着地窓の閉じ方だけを変える（シーク可否・速度補正の条件式は不変）。

## 1. 検証機が特定した機構

どちらの run も「着地窓が開く → 1 回目のシーク（delta 3.5 秒）」までは同一。1 回目のシークが
1.8〜2.0 秒かかり、**着地した瞬間に同じだけの誤差が残る**（LTC はシーク中も進むため、
着地時の誤差 ≈ シーク所要）。D37-b2 の着地窓は 1.0 秒固定だったので、**着地した時点で
窓が閉じている**。次の判断は通常の「残差 vs 学習済みシーク所要」に戻り、

- 残差 2,059ms > 学習値 1,866ms → シーク → 3.77 秒で収束（9 回中 1 回）
- 残差 1,829ms < 学習値 1,866ms → 速度補正 → 19 秒（9 回中 8 回）

と、ほぼ五分五分の分岐になる。これが「穴 1 の再発」の正体。

## 2. 変更

`TimecodeSyncService` の着地エピソードを「回数・時間で無条件に閉じる」から
「**誤差が許容内に入るまで開く**」に変更する。

- 窓は `NotifyLanding()`（ギャップ明け・切替ロード開始/成立・追従開始）で**新規に開く**。
- `ReportSeekSent` は窓が開いている間のシークを数える。
- 閉じる条件は 3 つ:
  1. **到達**: エンジンが誤差 ≦ 許容を観測（`SyncDecision.WithinTolerance`）→ 即座に閉じる。
  2. **上限 1**: 窓が開いてから **5 秒**（`SeekLandingMaxWindow`）。
  3. **上限 2**: 連続シーク **3 回**（`SeekLandingMaxSeeks`）。
- 上限で閉じたらログに残す（`landing window closed at the seek cap/age cap`）。到達も
  `landing window closed on arrival` で残す。

### 上限を付ける理由

上限が無いと、シーク所要が縮まらない素材で D37 のシーク連鎖に戻る。実測では 1 回目 1.99 秒 →
2 回目 0.97 秒と縮んで 2 回で収束するが、縮まない素材はあり得る。5 秒は L-1 の
`waitedSeconds` 上限と同じ値で、これを超えたら合格しないため粘る意味がない。

## 3. D37-b2（ギャップ明け・切替）も同じ扱いにする — 判断と根拠

**同じ扱いにする（統一）。** 根拠:

1. 機構はモードに依存しない。着地の残差 ≈ シーク所要 は「LTC がシーク中も進む」ことの
   帰結で、ギャップ明けでもトラック切替でも同じ。
2. 切替の実測（検証機）では、ロード成立時点の位置は目標に近い（14.983 対 15、誤差 0.017）が、
   これはロード自体がシークを内包するため。ロードが長 GOP のシークを含めば同じ残差が
   残る経路は存在し、窓を 1 回で閉じる理由がない。
3. 追従開始（D37-c）を同じ窓に載せた理由（着地の瞬間は画面が合っていない）もそのまま当てはまる。
4. モードごとに別の窓を持つと、同じ「着地エピソード」に対して閉じ条件が競合し、D37-d の
   機構解析が複雑になる。1 本の窓・1 本のカウンタの方が検証しやすい。

なお、別物（Smooth の速度上限を ±0.20 に上げる `SyncCorrectionController` の窓）は変更しない。
あちらは「どこまで速度で詰めるか」の窓で、シーク/速度補正の分岐には関与しない。

## 4. 単体テスト

| 固定する内容 | テスト |
| --- | --- |
| 1 回目の着地後も窓が開いていて、2 回目にシークを選ぶ（初期誤差 3.5 秒、シーク所要/学習値 1.866 秒、着地後残差 1.829 秒） | `TimecodeSyncServiceTests.EvaluateDecision_AfterFirstSeekLanding_KeepsWindowOpen_AndSecondSeekIsChosen` |
| 誤差が許容内に入ったら閉じる | `EvaluateDecision_WithinToleranceClosesLandingWindow`、`EvaluateDecision_AfterLanding_StaysOpenUntilArrival` |
| 連続シーク 3 回で閉じる | `EvaluateDecision_LandingWindowClosesAtTheSeekCap` |
| 開いてから 5 秒で閉じる（境界） | `EvaluateDecision_LandingWindowClosesAtTheAgeCap` |
| 新しい着地で開き直す | `NotifyLanding_ReopensTheWindowAfterArrival` |
| ロード成立でも開く | `BeginFileLoad_StartsTheLandingWindow`（既存） |

## 5. 実機の確認（予定）

1. M3 相当（4K60 ロング GOP）で **L-1 を最低 5 回**。検証機の実測で 9 回に 1 回の分岐なので、
   1 回では判定できない。2 回目のシークが `seek.issue` / `Continue mode: sync seek` に出て、
   収束が 19 秒側に落ちないこと。
2. V3 を 1 本（定常の平均・ばらつきが基準内）。
3. 既存シナリオ 22 + LTC ループ 14。
4. `waitedSeconds` の上限 5 秒は変更しない。これを超える結果になったら D37-d が不十分。
