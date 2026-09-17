# D31: 未確認 Jump の確認窓を壁時計ではなくサンプル時計（ストリーム順）で取る

作成: 2026-09-17 18:40、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`、新セッション）。
基点: **main の最新（`ce34740` 以降。D29/D30 統合済み）** を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
**製品コードの変更。D30 の確認規則（同値 Duplicate または +1 フレーム）と、同期の目標・補正には触らない。確認窓の時計だけ。**

## 1. 観測（統合後 main、除去担当の解析、S-2 の s2-19、2026-09-17 18:03）

- 18:03:41.135 s2-19 の最初のフレーム `status=Jump tc=00:00:07:21 resolved=7.840 detectedFps=24` → `holding unconfirmed Jump frame ltc=7.840 reason=detected-fps`（D30）。
- 次に処理されたフレームまで 203ms 空き、7.88 / 7.92 / 7.96 / 8.00 が 41.24〜41.34 にバースト処理された。`applying the confirmed Jump` は無し = **確認不成立**。
- 確認窓 `JumpConfirmationPolicy.IsWithinConfirmationWindow` は `receivedAtMilliseconds` の差 ≤ max(2.5 フレーム, 100ms)。`receivedAtMilliseconds` は `MainWindow.LtcMonitor_FrameReceived` で音声コールバック内に `Environment.TickCount64` を打った値。WASAPI 捕捉が滞ると 1 回のコールバックに複数フレーム分の PCM が来て、同一時刻の連続フレームが前の保留から 100ms 以上離れる（着地・EOF 処理の負荷でこれが起きる）。
- 確認が落ちると保留は破棄され、以後は 8.000 の保持（Duplicate）しか来ないので D27-b の Jump 1 枚復帰も、無音 5 枚復帰（保持は進行フレームに数えない）も成立せず、位置 20.000 のまま失敗。
- 証跡: 除去担当の `TestResults/postmerge2/01-ltc-scenario/scenarios/S-2-20260917-180316/harness.jsonl`（除去担当のツリー）。

## 2. 直すこと

1. **確認窓の時計をサンプル時計にする**: `LtcFrameReceivedEventArgs.FrameEndTimestamp`（同期ワード末尾の QPC。サンプル位置由来でコールバックの遅延に影響されない）の差で `IsWithinConfirmationWindow` を判定する。保留時に `FrameEndTimestamp` を記憶し、次フレームの `FrameEndTimestamp` との差（`Stopwatch.Frequency` で ms 化）で窓を見る。窓の幅（2.5 フレーム、下限 100ms、上限 500ms）は変えない。
2. **フォールバック**: どちらかの `FrameEndTimestamp` が 0（サンプル時計無効・テスト経路の `ReceiveProcessedFrame(processed, receivedAt)`）のときだけ今の `receivedAtMilliseconds` で判定する。
3. **確認不成立時の記録**: 窓外で保留を捨てるとき `Log.Information` を 1 行（保留値・次フレーム値・ストリーム差 ms・壁時計差 ms）。今は黙って捨てており解析に手間取った。
4. 検討のうえ報告（実装は親の合図後）: 確認が落ちたあと、保持損失中に **last applied から許容の 4 倍以上離れた保持 Duplicate** を復帰・着地のトリガとして扱う第二の防御が必要か。`_lastAppliedLtcSeconds` が既に 8.000 だった理由（前サイクルの残り？）も併せて。

## 3. 検証

- 単体: `JumpConfirmationPolicy` / `LtcSyncController` のテストに「壁時計は 203ms 離れているがサンプル時計は 1 フレーム差 → 確認成立」「サンプル時計で 600ms 空き（無音を挟む）→ 不成立」「FrameEndTimestamp=0 → 壁時計で判定」を追加。非E2E 全件。
- E2E（実機は一報のうえ親の合図。除去担当と重ねない）: `LtcScenarioE2ETests` の S-2 を `TIMECODE_LTC_SCENARIO_CYCLES` 既定で 3 回連続、C-1/C-2、R-1〜R-4。合格していた本数が崩れないこと。

## 4. 報告

コミット、変更の要約、単体の増減、E2E の結果、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。バージョンは上げない。**
