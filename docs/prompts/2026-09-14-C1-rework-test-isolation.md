# C1 差し戻し: 追加したテストが共有静的状態を壊し、既存テストを落とす

対象: `codex/c1-comparison-switches-20260913` の `b0665e5`
（main へ rebase 済みの検証用ブランチは `integrate/c1-20260914` の `09eac00`。内容は同一）

**機能そのものは問題ない。直すのはテストの隔離だけ。**

---

## 合格している項目（作り直す必要はない）

| 項目 | 結果 |
| --- | --- |
| 差分の範囲 | 7 ファイル、+326 -31。rebase 後も `b0665e5` と内容一致 |
| I13 検査 | **PASS** |
| 全 E2E | **失敗 0 / 合格 58 / スキップ 5** |
| shim 単体（TS 素材、3 方式） | `auto` / `accurate` / `keyunit` とも `failures=0`、ログに方式が出る |
| 既定の挙動 | **変わっていない**。`auto` のとき `keyunit = p->mpegts` となり従来と同一 |
| (b) のゲート連動 | **正しい**。ゲートとセグメント書き換えを方式に連動させており、accurate 強制時は張られない |

---

## 落ちている 1 件

```
TimecodeSyncPlayer.Tests.SeekTraceEventsTests.LoadFile_RecordsIssueAndReturnWithStartMicroseconds [FAIL]
  Expected Events(events, "load.issue") {empty} to have an item matching
  (e.GetProperty("value").GetInt64() == 12500000).
```

非E2E 全体では **失敗 1 / 合格 1742 / 合計 1743**。

## 原因（計測で確定済み。推測ではない）

`SeekTraceEventsTests` と `GapEnterCoordinatorTests` は**どちらも `[Collection]` 属性を持たない**ため、
xUnit が**別コレクションとして並列に実行する**。そして**両方が共有静的 `OutputTrace.Current` を差し替える**。

- `SeekTraceEventsTests` は自分のトレースを `OutputTrace.Current` に入れて `load.issue` を記録し、検証する
- **C1 が `GapEnterCoordinatorTests` にも `OutputTrace.Current = trace` を足した**（`gap.enter` の確認のため）
- 並列に走ると、`SeekTraceEventsTests` が記録している最中に `Current` が別のトレースへ差し替わり、
  `load.issue` が別のファイルへ落ちる。結果として `{empty}` になる

### 切り分けの証拠

| 実行 | 結果 |
| --- | --- |
| `SeekTraceEventsTests` 単独 | **5/5 合格** |
| `SeekTraceEventsTests` + `GapEnterCoordinatorTests` の 2 クラスのみ | **3 回とも失敗**（再現率 3/3） |
| main（C1 無し）の `GapEnterCoordinatorTests` | `OutputTrace` への参照 **0 箇所** |

つまり **C1 が持ち込んだ干渉**である。`SeekTraceEventsTests` 側は
「共有参照 `OutputTrace.Current` を使うため、対象イベントだけを値で絞って検証する」と
コメントで防御していたが、**トレース自体が別物に差し替わる場合はこの防御では足りない**。

## 直し方（方針は任せるが、次を満たすこと）

`OutputTrace.Current` を差し替えるテストクラスが**同時に走らない**ようにすること。例えば:

- `[CollectionDefinition("OutputTrace", DisableParallelization = true)]` を新設し、
  `OutputTrace.Current` を触る**全ての**テストクラスに `[Collection("OutputTrace")]` を付ける
  （`SeekTraceEventsTests` と `GapEnterCoordinatorTests` の両方。他にもあれば全て）
- あるいは `GapEnterCoordinatorTests` が静的を触らずに済む形にする
  （`OutputTrace` を注入可能にする等。ただし製品コードの変更は最小に）

**どちらを選んだか、なぜかをコミットメッセージに書くこと。**

## 確認してほしいこと（提出前に）

1. 非E2E が **1743 件すべて合格**すること
2. **2 クラスだけを指定した実行**でも合格すること:
   `dotnet test --filter "FullyQualifiedName~SeekTraceEventsTests|FullyQualifiedName~GapEnterCoordinatorTests"`
   これを **3 回連続**で回して 3 回とも合格すること（今は 3/3 で落ちる）
3. `python scripts/check-shim-lock-rule.py` が PASS すること
4. 機能側（shim・GapEnterCoordinator の実装）は**変えないこと**。変える必要はない

---

## 併せて確認したい点（差し戻しの理由ではない。回答だけください）

`ApplyGapPause()` は 4 箇所から呼ばれている:

- `EnterBlackGap` / `EnterForceBlack`（黒）
- `StartGapFreezeCaptureForCurrentTrack` / `EnterNoTracksFreeze`（**フリーズ**）

`compose-black` ではこの 4 つすべてで `PauseForGap()` が呼ばれなくなる。
**フリーズのギャップでもプレイヤーが止まらなくなるため、保持フレームが保持されない可能性がある。**

意図的にそうしたのか、`GapBehavior.Black` のときだけ `compose-black` を効かせるつもりだったのかを教えてほしい。
**測定で何が起きるかは親が確認するので、実装を変える必要はまだ無い。**
