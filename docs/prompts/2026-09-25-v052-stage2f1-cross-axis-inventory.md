# v0.5.2 段 2f-1: 軸をまたぐ条件の棚卸し（w5:p3 向け、調べるだけ）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`340e62c` 以降）。**コードは変えない。表を 1 つ書くだけ。**

## 背景

v0.5.2 は同期の状態を見える形にする作り直し（設計書 `docs/design/v0.5.2-sync-state.md`。まず §2・§3 の段 2・§10 を読む）。
段 2b〜2e で、状態を 4 つの軸の型にまとめた:

| 軸 | 型（ファイル） |
| --- | --- |
| 入力（LTC） | `LtcInputState`、信号断は `LtcSignalLossPolicy` |
| シーク | `TimecodeSyncSeekState`、`SeekLandingWindow`、`PlaybackPositionTrust` |
| 速度補正 | `RateCorrectionState`、`SyncCorrectionController` |
| 位置の持ち主 | `FileLoadState`（`TimecodeSyncService`）、`BoundaryHoldState`（`SingleModeSyncCoordinator`）、`GapFreezeHandler` |

段 2f では、**ある軸の状態を見て、別の軸の操作をするかどうかを決めている条件**を 1 か所の判定関数（純関数）に集める。
その前に、今どこにどんな条件があるかを漏れなく表にする（この段）。親が表を承認してから、2f-2 で実装する。

設計書の例: 境界ホールド中は保持着地を出さない（D35-b）、ロード中はシークを保留する、位置が不安定な間は速度補正を止める（0.4.8）、ギャップ中は信号断の復帰を出さない。

## やること

`src/TimecodeSyncPlayer/` の同期まわり（`LtcSyncController`・`TimecodeSyncService`・`SingleModeSyncCoordinator`・`ContinueOnTrackCoordinator`・
`LtcSignalLossPolicy`・`GapEnterCoordinator`・`SyncDecisionEngine`、ほか必要なら）を読み、次の条件をすべて拾う:

- `if` やガード節で、**別の軸の状態**（上の表の型の値、または `LtcSyncContext` の `IsSeeking`・`IsGapActive`・`IsPlaybackPaused` など）を見て、
  操作（シーク・速度の変更・一時停止／再開・着地・同期の適用・ログだけの分岐は除く）をする／しないを決めているもの
- 同じ軸の中だけで閉じている条件は**拾わない**（例: 速度補正の段階だけを見て速度を戻す）

書き出し先: `docs/design/v0.5.2-cross-axis-rules.md`（新規）。1 行 1 条件の表:

| # | 操作（何をする／しない） | 見ている状態（軸と値） | 場所（ファイル:メソッド、行番号） | 由来（コメントの D 番号など） | 書き方（ガード節 / 条件式 / 分岐） |

- 同じ条件が複数の場所に写してある場合は、場所を全部書いて 1 行にまとめる
- 迷ったもの（軸をまたぐか分からない、操作か分からない）は、表の下に「保留」として理由つきで分けて書く
- 最後に、条件を「1 つの判定関数にまとめやすいもの」と「まとめると振る舞いが変わりそうなもの（評価の順番や副作用に依存）」に分けた所見を 5 行以内で書く

## 規則

- **コードは変えない。git の書き込み禁止**（新しい md ファイルを作るだけ。コミットは親がする）
- テストは回さなくてよい
- 終わったら、表の行数・保留の数・所見をこの画面に書く
