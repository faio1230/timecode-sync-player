# D37-d: 着地窓を「誤差が許容内に入るまで」開く（前進ガード付き）（2026-09-19）

実装: `src/TimecodeSyncPlayer/TimecodeSyncService.cs`、`SyncDecisionEngine.cs`。表示・制御則の
分岐は着地窓の閉じ方と、窓の中でのシーク選択だけを変える。

## 1. 検証機が特定した機構

どちらの run も「着地窓が開く → 1 回目のシーク（delta 3.5 秒）」までは同一。1 回目のシークが
1.8〜2.0 秒かかり、**着地した瞬間に同じだけの誤差が残る**（LTC はシーク中も進むため、
着地時の誤差 ≈ シーク所要）。D37-b2 の着地窓は 1.0 秒固定だったので、**着地した時点で
窓が閉じている**。次の判断は通常の「残差 vs 学習済みシーク所要」に戻り、

- 残差 2,059ms > 学習値 1,866ms → シーク → 3.77 秒で収束（9 回中 1 回）
- 残差 1,829ms < 学習値 1,866ms → 速度補正 → 19 秒（9 回中 8 回）

と、ほぼ五分五分の分岐になる。これが「穴 1 の再発」の正体。

## 2. 変更（最終形）

`TimecodeSyncService` の着地エピソードを「回数・時間で無条件に閉じる」から
「**誤差が許容内に入るまで、前進が確認できる限り開く**」に変更する。

- 窓は `NotifyLanding()`（ギャップ明け・切替ロード開始/成立・追従開始）で**新規に開く**。
- 閉じる条件は 4 つ:
  1. **到達**: エンジンが誤差 ≦ 許容を観測（`SyncDecision.WithinTolerance`）→ 即座に閉じる。
  2. **前進なし（D37-d 前進ガード）**: 窓の中で発行したシークの着地で、不足が実際に減って
     いなければ（`post >= pre - ε`）閉じる。**事前予測ではなく観測**なので、シークが効かない
     帯でも 1 回で止まる。
  3. **上限 1**: 窓が開いてから **5 秒**（`SeekLandingMaxWindow`）。
  4. **上限 2**: 連続シーク **3 回**（`SeekLandingMaxSeeks`）。
- **窓の中のシーク選択には 0.5× しきい値も併用**: 残差が
  `0.5 × シーク所要見積り` を超えるときだけシークを優先し、それ以下は速度補正に任せる
  （`SyncDecisionEngine.LandingSeekPriorityFraction = 0.5`）。
- 上限・前進なしで閉じたらログに残す（`landing window closed ...`）。到達も同様。

## 3. 前進ガードだけにできるか（実測で確認した）

親の提案「1 回目は無条件シーク、2 回目以降は前進ガード」を、**前進ガードのみ（0.5× なし）**で
L-1 ×5 を実測して確かめた。

| 方式 | L-1 ×5 の追従開始 | 判定 |
| --- | --- | --- |
| 0.5× のみ（前進ガードなし） | 1.77 / 1.78 / 2.24 / 2.56 / 2.85 秒 | 5/5 だが境界帯は未カバー |
| **前進ガードのみ（0.5× なし）** | 0.86 / 2.55 / 4.43 / 4.78 / **5.15 秒（失敗）** | **4/5。止まり切らない** |
| **0.5× + 前進ガード（採用）** | **0.85 / 0.85 / 0.87 / 0.98 / 1.16 秒** | **5/5。最良** |

**結論: 0.5× を残し、前進ガードを併用する。** 前進ガードだけでは 1 件が 5.146 秒で上限を
超えた（「1 回でも超えたら報告」の条件に該当）。前進ガードは境界帯（0.5〜1.0 倍）と、シークが
縮まらない素材で効く最後の観測であり、0.5× は無駄な初回シークを減らす。役割が違うため両方入れる。

証跡: `TestResults/d37d/l1-both-1..5`（両方）、`l1-guard-1..5`（前進ガードのみ）、
`l1-fixed-1..5`（0.5× のみ）、`l1-run1..3`（D37-d 以前の 3/3 失敗）。

## 4. 0.5 の根拠（調整値。理論値ではない）

**0.5 は理論から導いた値ではない。調整値である。** 分かっているのは次の 2 点だけ:

1. 検証機の帯（残差 1,829ms / 学習値 1,866ms）で**シーク側に倒れる**こと。1/2 なら
   0.933 秒が境界で、1.829 秒は余裕をもって超える。
2. L-1 実測の小さい残差（0.344 秒 < 0.5 × 未学習の既定 1.0 秒）で**速度補正側に落ちる**こと。

「なぜ 0.5 が最適か」は説明できない。境界付近（残差 ≈ 0.5 × 所要）は前進ガードが
観測で塞ぐが、0.5 と 1.0 の間の選び方は未検証で、別の素材で破綻したら
`LandingSeekPriorityFraction` を調整する。**理論値だと思って原因を他の場所に
探さないこと。**

### 2 回目のシークが速い理由（分かった範囲）

- 学習値は EMA（初回を含む履歴の平均）なので、**初回の冷えたシーク（1.99 秒）を引きずる**。
  2 回目の実測（0.97 秒）が学習値より短いのは確か。
- それが「目標までの距離が短いから」か「1 回目で近くのキーフレームまで復号済みだから」かは、
  **手元の証跡では分離できない**。検証機の trace から `seek.issue` ごとの所要と目標距離
  （decide の delta）を突き合わせれば分離できる見込み。ここでは**見えない**と記録する。
- 参考: ローカル L-1 の display-check3（境界帯、不足 0.579 秒）では 3 シークで上限に達し、
  残差が 0.4 → 0.5 → 0.7 と増えて 11.35 秒だった。前進ガードはこの「増える」着地を
  観測で切る。

## 5. D37-b2（ギャップ明け・切替）も同じ扱いにする — 判断と根拠

**同じ扱いにする（統一）。** 根拠:

1. 機構はモードに依存しない。着地の残差 ≈ シーク所要 は「LTC がシーク中も進む」ことの
   帰結で、ギャップ明けでもトラック切替でも同じ。
2. 切替の実測（検証機）では、ロード成立時点の位置は目標に近い（14.983 対 15、誤差 0.017）が、
   これはロード自体がシークを内包するため。ロードが長 GOP のシークを含めば同じ残差が
   残る経路は存在し、窓を 1 回で閉じる理由がない。
3. 追従開始（D37-c）を同じ窓に載せた理由（着地の瞬間は画面が合っていない）もそのまま当てはまる。
4. モードごとに別の窓を持つと、同じ着地エピソードに対して閉じ条件が競合し、機構解析が複雑になる。

なお、別物（Smooth の速度上限を ±0.20 に上げる `SyncCorrectionController` の窓）は変更しない。

## 6. 単体テスト

| 固定する内容 | テスト |
| --- | --- |
| 1 回目の着地後も窓が開き、2 回目にシークを選ぶ（初期誤差 3.5 秒、所要/学習値 1.866 秒、残差 1.829 秒） | `TimecodeSyncServiceTests.EvaluateDecision_AfterFirstSeekLanding_KeepsWindowOpen_AndSecondSeekIsChosen` |
| **前進なしの着地で窓を閉じる（境界帯 0.579 → 0.600）** | `EvaluateDecision_AfterASeekWithoutProgress_ClosesTheLandingWindow` |
| **前進ありの着地では窓を開いたまま（3.5 → 1.8）** | `EvaluateDecision_AfterASeekWithProgress_KeepsTheLandingWindow` |
| 小さい残差（0.4 秒 <= 0.5 × 1.0 秒）は速度補正 | `EvaluateDecision_LandingWindow...`（エンジン: `Decide_DeficitBelowHalfSeekCost_...`） |
| 0.5× を超える帯はシーク | `SyncDecisionEngineTests.Decide_DeficitWithinSeekCost_WhenRateCatchUpDisallowed_Seeks` |
| 誤差が許容内に入ったら閉じる | `EvaluateDecision_WithinToleranceClosesLandingWindow`、`EvaluateDecision_AfterLanding_StaysOpenUntilArrival` |
| **上限の歯止め: 連続 3 シークで閉じ、通常の判断（速度補正）に戻る** | `EvaluateDecision_LandingWindowClosesAtTheSeekCap`、`EvaluateDecision_AfterTheSeekCap_ReturnsToNormalRateCatchUp` |
| **上限の歯止め: 5 秒で閉じ、通常の判断に戻る** | `EvaluateDecision_LandingWindowClosesAtTheAgeCap`、`EvaluateDecision_AfterTheAgeCap_ReturnsToNormalRateCatchUp` |
| 新しい着地で開き直す / ロード成立でも開く | `NotifyLanding_ReopensTheWindowAfterArrival`、`BeginFileLoad_StartsTheLandingWindow` |

## 7. 実機の確認（結果）

1. **L-1 ×5（M1 = 4K60 10s GOP）: 5/5、追従開始 0.85 / 0.85 / 0.87 / 0.98 / 1.16 秒**（上限 5 秒）。
   証跡 `TestResults/d37d/l1-both-1..5`。
2. **V3 ×1**: SyncAccuracy 20/20、定常 **-27.8ms / p95-p5 38.2ms**（従来同水準）、
   回復 120〜400ms。証跡 `TestResults/v3/d37d-final-ltc25-gst`。
3. **既存シナリオ 22 + LTC ループ 14**: 22/22 + ループ 14/14。統合 run で
   `CableLoop_WhenSignalIsLost_TimecodeStopsProgressing` が 1 件タイムアウトしたが、
   ループ 14 本の単独再実行で **14/14 合格**（VB-CABLE の信号断タイミングのフレーク）。
   証跡 `TestResults/d37d/scenarios-and-loop-final`（統合）と `TestResults/d37d/loop-rerun`（再実行）。
4. `waitedSeconds` の上限 5 秒は変更しない。
