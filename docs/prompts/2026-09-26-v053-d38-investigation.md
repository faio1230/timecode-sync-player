# v0.5.3 D38 の調査: ランスルーで保持後のジャンプの着地が約 2 秒遅れる。シークの経路の時間ベースの門の棚卸し（w5:p3 向け、製品コードは 0 のテスト以外変えない）

作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v053-3a`。背景は `docs/release-0.5-plan.md` の「D38」の節（先に読む）。
**実装はしない。** 親が設計を見てから直す。

## 事実（親が確かめたログ）

```
14:37:21.791 Continue mode: sync seek ltc=20.000 playback=5.863 target=15.000 ... success=true   ← 保持中の着地シーク（15 へ）
14:37:22.734 Sync lifecycle: "SignalRecovered" source=held-jump / applying the first Jump frame once ltc=8.000
14:37:22.735〜23.76 sync.apply result="Deferred" ltc=8.045（約 110ms ごとに 11 回）
14:37:23.867 Timecode sync pending "TimedOut" playback=17.075
14:37:24.413 Timecode sync seek gated medianMs=-14576.6 consecutive=1 samples=1
14:37:24.633 Continue mode: sync seek ltc=8.045 playback=17.841 target=3.045 ... success=true   ← Jump の到着から 1.9 秒後
```
（`timecode-sync-player-v05` の tests の bin の logs、2026-09-25 のファイル。E2E の C-1）

## やること

0. **先に、着地の遅れを記録する判定を「記録のみ」で E2E に足す**（TSP-Fable の提案、2026-09-26）: E2E の `hold-landing` のジャーナルに
   `landingLatencySeconds`（LTC の新しい値がアプリに届いた時刻からシークの発行まで。ジャーナルの経過か、アプリのログの `applying the first Jump frame` / `held value change` と `sync seek` の差）と
   `latencyOverBudget`（ジャンプの距離が 4×tolerance を超え、かつ 1.0 秒を超えたら true）を残す。**失敗にはしない**（今の v0.5.3 を赤にしない）。
   D38 の修正のコミットで失敗の条件に切り替える。この 0 は**テストのコードだけ**の変更なので、ここで一度この画面に報告して手番を終える（親がコミットしてから 1 以降へ進む）。
   以降の二分探索は、この値で判定する
1. **仮説を確かめる**（TSP-Fable がコードで追った当たり。これを出発点にし、合っているかをコードとログで確かめる）:
   - 着地シークの発行時に `_positionTrust.InvalidateForPendingSeek()`（D37-b、`947796d` 2026-09-18）。信頼が戻るのは `TrackSeekStatusTransition` で保留が Settled になったときだけ
   - Settled の判定は `ShouldSuppressSeek` の中（`HasReachedSeekTarget`: target −tol〜+2×tol、200ms の cooldown）で、**同期を適用するフレームでしか呼ばれない**
   - ランスルーで LTC が保持されている間は Duplicate で `ApplySync` が走らず、**着地したのに Settled を判定する機会がない**。次の Jump の時点では位置が窓の外
   - 信頼が無いので `WhilePositionUntrusted` でシークを出さず、置き換えの規則は要求の target が NaN で効かない → 2 秒の時間切れ → 再取得（約 330ms）→ D37-a のゲート（約 220ms）→ シーク
   - 二分探索は `947796d` の前後 1 本ずつで確かめる（0 で足した `latencyOverBudget` で判定）
2. **シークの経路の時間ベースの門の棚卸し**（利用者の方針、2026-09-26: 門を 1 つ足すのではなく棚卸しして統合する。「守りすぎも欠陥」）:
   保留の時間切れ、位置の信頼の再確認、シークのゲート（中央値・連続）、Jump の確認窓、着地窓、デバウンス、ロード中の抑止ほか、シークを遅らせうる門を**全部**表にする:

   | 門 | 場所 | 守っている欠陥（D 番号） | 発火条件 | 平常時に足す遅延 | 同じ質問に答えるほかの門 |

3. **統合の案**: 「着地の判定は 1 つ、ジャンプの雑音の除去は 1 つ、シークしてよいかの門は 1 つ」にまとめる案を出す。TSP-Fable の直し方の候補
   (a) 保留の着地判定を毎フレーム（保持の Duplicate でも）行う、(b) 距離が 4×tol を超える新しい要求は位置が未信頼でも置き換える、(c) ランスルー保持中は動く窓で判定する、
   を表の上で評価し、ほかのシナリオ（S-2 の停止モード、C-2 の別トラック、D37 の着地窓、D30 の Jump 確認）に何が起きうるかを書く。**直す範囲は親が表を見て決める**
4. 書き出し: `docs/design/v0.5.3-d38-seek-gates.md`（新規）に 1〜3 を書く

## 規則

- **製品コードは変えない。git の書き込み禁止**（md を作るのと、一時的な作業ツリーを作って消すだけ）
- 終わったら、仮説が合っていたか・入ったコミット・門の数・統合の案の要約をこの画面に書く
