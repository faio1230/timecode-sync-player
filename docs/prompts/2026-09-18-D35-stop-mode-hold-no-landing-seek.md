# D35: 停止モードの保持で、一時停止までの 250〜480ms 分だけ再生が進み、保持値への着地シークが出ない

作成: 2026-09-18、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: main の最新（D34 統合後 `bb7d289` 以降）を `agent-a` へ通常マージし、shim をビルドしてから。
仕様: 停止モードで LTC が保持されたら動画は保持位置で一時停止（利用者決定「モード依存」、R-1）。D27-d は「保持として届いた値」に着地する設計。
**製品コードの変更。Smooth/Jump の補正量には触らない。停止モードの着地と、ロード後再適用の条件だけ。**

## 1. 証跡（検証機、実素材 A = M1 ProRes 4K60 CPU、R-1 の 2 回。時刻は検証機ローカル）

ケース 1（reason="SignalLoss"、超過 0.183 秒）
```
05:05:45.705 Continue mode: sync seek ltc=8.403 … success=true（着地シーク。以後 accurate シークはこの 1 回だけ）
05:05:46.081〜49.126 Smooth correction rate=1.20000→1.00873 residualMs=210.6→8.7（rate.instant 79 回）
05:05:49.378 [WRN] LTC frame diagnostic status="Reverse" tc=00:00:11:21 rawSeconds=11.840 deltaFrames=-2.00
05:05:49.380 Timecode sync: reapplying the last accepted timecode once after file load ltc=11.963   ← ロードの 6.4 秒後
05:05:49.416 LTC signal lost: playback paused timeoutMs=250 reason="SignalLoss"
05:05:49.581〜 LTC frame diagnostic status="Duplicate" tc=00:00:12:00（0.05 秒間隔、テストの保持）
journal hold-pause 05:05:49.648: position=12.183 / ltc=12.000（着地シークなし）
```

ケース 2（reason="TimecodeHeld"、超過 0.43 秒 → 判定時 13.000）
```
04:15:08.146 Continue mode: sync seek ltc=8.406 … success=true
04:15:08.503〜 Smooth correction rate=1.18348→1.00770
04:15:11.810 [WRN] LTC frame diagnostic status="Reverse" tc=00:00:11:21 deltaSeconds=-0.080
04:15:11.812 Timecode sync: reapplying the last accepted timecode once after file load ltc=11.932
04:15:11.813 Smooth correction rate=0.90000 residualMs=-223.8
04:15:12.295 LTC signal lost: playback paused timeoutMs=250 reason="TimecodeHeld"   ← 最後の受理から 483ms
journal hold-pause pauseLatencySeconds=0.387 position=12.55 → 判定時 13.000（着地シークなし。shim ログにも accurate シークは最初の 1 回のみ）
```

## 2. 調べて直すこと

1. **着地シークが出ない**: 停止モードで損失（理由を問わず）に入ったあと、保持値（Duplicate の値、ケース 1 では損失宣言の後に届く）が分かった時点で、再生位置が保持値から許容（同期の tolerance）を超えて離れていれば **1 回着地シーク**する。ケース 1 は損失宣言時に `_lastHeldEffectiveSeconds` が無く（Duplicate が損失の後に来た）、D31-b の変化判定も基準が null で偽になる経路の疑い。ケース 2 は TimecodeHeld なのに着地が出ていない理由を `ReapplyHeldValueOnPause` / D27-d の経路（tolerance 判定、`_heldReapplyDone`、`_fileLoadReleasePending` との干渉）で特定する。生成素材の R-1 が通る理由（超過が小さく許容内？）も書く
2. **一時停止までの走り過ぎ**: 損失タイムアウト 250ms（+ ケース 2 の 483ms）の間は rate ≈1.0 で進む。着地シークが出れば結果は正しくなるので、まず 1 を確実にする。停止モードでは pause の直前に rate を 1.0 に戻す（`rate.instant` の残り）
3. **「reapplying the last accepted timecode once after file load」がロードの 5〜7 秒後に走る**: `_fileLoadReleasePending` が解除されずに残り、`Reverse` 1 枚（テスト信号の保持開始の 2 フレーム戻り）で発火した疑い。意図は「ロード解除直後の 1 回」。ロード完了後の一定時間（または最初の受理フレーム）で必ず解除する
4. `Reverse`（−2 フレーム）が保持開始のたびに出る点はテスト信号側（除去担当へ親が別途）。製品側はそれで壊れないこと

## 3. 検証

- 単体: `LtcSyncController` に「停止モード: SignalLoss で一時停止 → その後の Duplicate 12.0 で位置 12.18 なら着地 1 回」「TimecodeHeld で一時停止 → 位置 12.55 なら着地 1 回」「保持値と位置が許容内なら着地しない」「ロード後再適用はロード完了から N 秒/最初の受理で解除」
- E2E（実機は一報のうえ親の合図。除去担当が実機使用中）: R-1〜R-4 を生成素材と 4K 3 本で各 3 サイクル、S-2、C-1/C-2。hold-pause の超過（position − 保持値）を報告

## 4. 報告

コミット（項目ごと）、変更の要約、単体の増減、E2E の結果、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。版は上げない。**
