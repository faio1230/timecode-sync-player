# D37-d: 着地窓を「誤差が許容内に入るまで」開く（2026-09-19）

実装: `src/TimecodeSyncPlayer/TimecodeSyncService.cs`、`SyncDecisionEngine.cs`。表示・制御則の
分岐は着地窓の閉じ方と、窓の中でのシーク優先条件だけを変える。

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
- **窓の中のシーク優先には下限を付ける（0.5× ルール）**: 残差が
  `0.5 × シーク所要見積り` を超えるときだけシークを優先し、それ以下は速度補正に任せる
  （`SyncDecisionEngine.LandingSeekPriorityFraction = 0.5`）。
- 上限で閉じたらログに残す（`landing window closed at the seek cap/age cap`）。到達も
  `landing window closed on arrival` で残す。

### 0.5× ルールが必要だった理由（実測）

L-1 を M1（4K60 10s GOP）で 3 回実行し、**3/3 失敗（追従開始 10.66 / 10.73 / 11.07 秒、
上限 5 秒）**。初期誤差が 0.344 秒と小さく、シーク所要（0.4〜1.8 秒）より小さい領域で、
窓がシークを強制すると 1 回ごとに残差が増えた（0.34 → 0.40 → 0.50 → 0.69）。3 シークで
上限に達した後も、残差が学習値を超えているため通常則がシークを続け（計 8 回）、
最後は速度補正で 10.7 秒だった。**シークは残差を減らせない（着地後残差 ≈ シーク所要）。**

- 検証機の帯（残差 3.5 秒 > 所要 1.9 秒）ではシーク優先が正しい。
- 小さい残差（数百 ms）では速度補正が正しい。
- この 2 つを分けるのが 0.5× ルール。

## 3. 0.5 の根拠（調整値。理論値ではない）

**0.5 は理論から導いた値ではない。調整値である。** 分かっているのは次の 2 点だけ:

1. 検証機の帯（残差 1,829ms / 学習値 1,866ms）で**シーク側に倒れる**こと。1/2 なら
   0.933 秒が境界で、1.829 秒は余裕をもって超える。
2. L-1 実測の小さい残差（0.344 秒 < 0.5 × 未学習の既定 1.0 秒）で**速度補正側に落ちる**こと。

「なぜ 0.5 が最適か」は説明できない。境界付近（残差 ≈ 0.5 × 所要）の挙動は未検証で、
別の素材で破綻したらこの 1 か所（`LandingSeekPriorityFraction`）を調整する。
**理論値だと思って原因を他の場所に探さないこと。**

将来「その時点の目標距離から見積もる」に置き換える候補として、次の理由が考えられる:
2 回目の実測所要（検証機 0.97 秒）が学習値（EMA 1.866 秒）より短いこと。

### 2 回目のシークが速い理由（分かった範囲）

- 学習値は EMA（初回を含む履歴の平均）なので、**初回の冷えたシーク（1.99 秒）を引きずる**。
  2 回目の実測（0.97 秒）が学習値より短いのは確か。
- それが「目標までの距離が短いから」か「1 回目で近くのキーフレームまで復号済みだから」かは、
  **手元の証跡では分離できない**。検証機の trace から `seek.issue` ごとの所要と目標距離
  （decide の delta）を突き合わせれば分離できる見込み。ここでは**見えない**と記録する。
- 参考: ローカル L-1 のシークは 0.4〜1.8 秒で、残差が所要より小さい領域では所要が縮まなかった
  （縮まない素材があることの実例）。

## 4. D37-b2（ギャップ明け・切替）も同じ扱いにする — 判断と根拠

**同じ扱いにする（統一）。** 根拠:

1. 機構はモードに依存しない。着地の残差 ≈ シーク所要 は「LTC がシーク中も進む」ことの
   帰結で、ギャップ明けでもトラック切替でも同じ。
2. 切替の実測（検証機）では、ロード成立時点の位置は目標に近い（14.983 対 15、誤差 0.017）が、
   これはロード自体がシークを内包するため。ロードが長 GOP のシークを含めば同じ残差が
   残る経路は存在し、窓を 1 回で閉じる理由がない。
3. 追従開始（D37-c）を同じ窓に載せた理由（着地の瞬間は画面が合っていない）もそのまま当てはまる。
4. モードごとに別の窓を持つと、同じ着地エピソードに対して閉じ条件が競合し、機構解析が複雑になる。

なお、別物（Smooth の速度上限を ±0.20 に上げる `SyncCorrectionController` の窓）は変更しない。

## 5. 単体テスト

| 固定する内容 | テスト |
| --- | --- |
| 1 回目の着地後も窓が開き、2 回目にシークを選ぶ（初期誤差 3.5 秒、所要/学習値 1.866 秒、残差 1.829 秒） | `TimecodeSyncServiceTests.EvaluateDecision_AfterFirstSeekLanding_KeepsWindowOpen_AndSecondSeekIsChosen` |
| 小さい残差（0.344 秒 < 0.5 × 1.0 秒）は速度補正 | `EvaluateDecision_LandingWindowWithDeficitBelowHalfSeekCost_PrefersRateCatchUp`、`SyncDecisionEngineTests.Decide_DeficitBelowHalfSeekCost_WhenRateCatchUpDisallowed_PrefersRateCatchUp` |
| 0.5× を超える帯はシーク | `SyncDecisionEngineTests.Decide_DeficitWithinSeekCost_WhenRateCatchUpDisallowed_Seeks` |
| 誤差が許容内に入ったら閉じる | `EvaluateDecision_WithinToleranceClosesLandingWindow`、`EvaluateDecision_AfterLanding_StaysOpenUntilArrival` |
| **上限の歯止め: 連続 3 シークで閉じ、通常の判断（速度補正）に戻る** | `EvaluateDecision_LandingWindowClosesAtTheSeekCap`、`EvaluateDecision_AfterTheSeekCap_ReturnsToNormalRateCatchUp` |
| **上限の歯止め: 5 秒で閉じ、通常の判断に戻る** | `EvaluateDecision_LandingWindowClosesAtTheAgeCap`、`EvaluateDecision_AfterTheAgeCap_ReturnsToNormalRateCatchUp` |
| 新しい着地で開き直す / ロード成立でも開く | `NotifyLanding_ReopensTheWindowAfterArrival`、`BeginFileLoad_StartsTheLandingWindow` |

## 6. 実機の確認（予定）

1. M3 相当（4K60 ロング GOP）で **L-1 を最低 5 回、全部 5 秒以内**。1 回でも超えたら報告。
2. V3 を 1 本（定常の平均・ばらつきが基準内）。
3. 既存シナリオ 22 + LTC ループ 14。
4. `waitedSeconds` の上限 5 秒は変更しない。これを超える結果になったら D37-d が不十分。
