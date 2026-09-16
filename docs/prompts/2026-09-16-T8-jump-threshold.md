# T8: Jump モードが残差の揺れでシークし続ける問題を直す

**利用者の承認済み（2026-09-16）の方向で実装する。** 数値の根拠は T7 の Jump 1 本。

---

## 1. 何が起きているか（親がログで確認済み）

`t7jump-184281d-ltc25-gst`（T7、`184281d`）:

- `Jump correction seek` が **61 回**。クリップ内で **250〜300ms ごと**に出続ける
- 各シークの `residualMs` は **±20〜40ms**（例: 25.0 / -29.2 / 31.1 / -20.4 / 39.9 …）で、符号が交互に変わる
- 定常誤差: 平均 **-141.4ms**、p95-p5 158.9ms（Smooth は -26.0ms / 73.0ms）

### 原因

`SyncCorrectionController.EvaluateJump`:

```csharp
if (abs <= DeadbandSeconds)          // 20ms
{
    _consecutiveJumpSeeks = 0;       // ← 揺れで一瞬 20ms 以内に入るたびに 0 に戻る
    return SyncCorrectionDecision.Idle("jump-idle");
}
if (_consecutiveJumpSeeks >= MaxConsecutiveJumpSeeks)   // 3
    return SyncCorrectionDecision.Idle("jump-limit");
_consecutiveJumpSeeks++;
return SyncCorrectionDecision.Seek(targetSeconds, "jump");
```

- 残差には、ずれていなくても **LTC の粒度と音声コールバック（50ms ごと）による ±20〜40ms の揺れ**が乗る
- Jump のしきい値は Smooth と同じ 20ms なので、**揺れだけで越える**
- 連続回数は揺れが 20ms 以内に入った瞬間に 0 に戻るので、**上限 3 回が効かない**
- フラッシュシークのたびに絵は着地まで遅れる（約 100ms）ので、平均が大きく遅れ側に寄る

## 2. 直すこと

1. **Jump がシークするしきい値を 80ms にする**（LTC 2 フレーム分。揺れの幅 ±40ms を越える最小の値）。
   定数として `SyncCorrectionController` に置き、Smooth のデッドバンド（20ms）とは別の名前にする。コメントに根拠（揺れの幅）を書く
2. **連続回数を 0 に戻す条件を「残差が一定時間しきい値の内側に留まったとき」にする。**
   一瞬内側に入っただけでは戻さない。時間は **1 秒**を初期値とする（定数。理由をコメントに書く）
3. 上限 3 回に達したら、戻す条件を満たすまでシークしない（現行どおり `jump-limit`）
4. `Reset()`（トラック切替・ギャップ出入り・手動操作）では、現行どおり連続回数も時計も捨てる
5. **Smooth の挙動は一切変えない**（デッドバンド 20ms、戻りバンド 10ms、±10%、2 秒の無効化判定）
6. 時刻は `Evaluate` に渡される `now` を使う（テストで時刻を進められるように。Smooth と同じ）

## 3. テスト（非E2E、`SyncCorrectionControllerTests`）

- 残差 ±40ms 以内の揺れ（例: +35 / -30 / +25 / -38 を 50ms 間隔）では **1 回もシークしない**
- 残差 +100ms で 1 回シークする
- +100ms が続いても、**連続 3 回で止まる**。その間に一瞬 +10ms が挟まっても回数は戻らない
- しきい値の内側に **1 秒留まった後**なら、再び +100ms でシークできる
- 内側に 0.9 秒だけ留まって外へ出た場合は、回数は戻らない
- `Reset()` 後はすぐシークできる
- Smooth の既存テストがすべて変わらず通る

## 4. 実機での確認

条件は T7 と同じ（gst / `outputBackend=1` / LTC 25 / 出力トレース有効 / Debug）。先行補償は **親の指示があるまで off のまま**（別に測定中のため）。

1. `scripts/run-v3-accuracy.ps1 -Backends gst -SyncCorrectionMode jump -Label t8jump-<SHA> -Repeats 2`
2. 出すもの（合否は書かない）:
   - 定常誤差の平均・p50・p5・p95・p95-p5
   - `Jump correction seek` の回数（クリップ別・フェーズ別）と、各シークの `residualMs` の分布
   - `jump-limit` に達した回数
   - 収束時間（`scripts/summarize-v3-accuracy.py` の recovery 表）
3. 比較として T7 の Jump 1 本（`t7jump-184281d-ltc25-gst`）を並べる

## 5. 守ること

1. 変えるのは Jump の判定だけ。Smooth、粗いデッドゾーン（`SyncDecisionEngine`）、補正の配線（T7）は触らない
2. `docs/SETUP.md` の Jump の説明（「連続 3 回で諦めてログを出します」の行）を新しい挙動に合わせて直す
3. 合否判定は書かない。親が出す
4. 実機は直列に 1 本ずつ。自分が起動した PID だけ終了する
