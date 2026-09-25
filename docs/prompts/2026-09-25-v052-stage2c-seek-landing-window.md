# v0.5.2 段 2c: シークの軸の着地窓を SeekLandingWindow にまとめる（w5:p3 向け）

作業ツリー `timecode-sync-player-v05`、ブランチ `v0.5.2`（`41a15c2` 以降）。**振る舞いは変えない。**
背景は設計書 `docs/design/v0.5.2-sync-state.md` の §2（シークの軸）と §10。

シークの軸のうち、`TimecodeSyncSeekState`（シークの保留）と `_positionTrust`（位置の信頼）はすでに独立した型なので触らない。
残っている着地窓（D37-b2 / D37-d / D37-e / D37-f / D37-g）を 1 つの型にまとめる。

## 対象（すべて `TimecodeSyncService`）

フィールド: `_seekLandingActive`、`_seekLandingOpenedAt`、`_seekLandingSeeks`、`_landingOrigin`、`_landingAwaitingObservation`、`_landingSeekPreDeficitSeconds`
定数: `LandingProgressEpsilonSeconds`、`SeekLandingMaxWindow`、`SeekLandingMaxSeeks`
メソッド: `OpenSeekLanding`、`EndFollowStartLanding` の中身、`CloseSeekLanding`、`IsSeekLandingWindowActive`、`ObserveArrivalWhileLanding`、`ObserveLandingProgress`、
それと着地窓のフィールドを読み書きしているほかの箇所（`ReportSeekSent` ほか。grep で全部拾う）

## やること

1. 新しいファイル `src/TimecodeSyncPlayer/SeekLandingWindow.cs` に `internal sealed class SeekLandingWindow` を作り、上のフィールド・定数・メソッドの中身を移す。
   `TimecodeSyncService` は `private readonly SeekLandingWindow _landing` を持ち、公開の入口（`NotifyLanding`、`EndFollowStartLanding` など）は今のシグネチャのまま `_landing` に委ねる
2. 時刻は今と同じく `TimeProvider` から取る（`SeekLandingWindow` にコンストラクタで渡すか、呼び出し側が `now` を渡す。今の取り方と回数を変えないほう）
3. 「閉じている／開いている＋開いている間のデータ」の形にしてよいか、**先に確かめること**:
   - 窓が閉じている（`_seekLandingActive == false`）ときに、`_landingOrigin`・`_seekLandingOpenedAt`・`_seekLandingSeeks`・`_landingAwaitingObservation`・
     `_landingSeekPreDeficitSeconds` を読む箇所があるか（`CloseSeekLanding` は `_landingOrigin` を戻さない。閉じた後の発生元が読まれていれば、発生元は開いている間のデータに入れられない）
   - 読む箇所があるフィールドは、窓の外に置いたまま（別のプロパティ）にする。無いものだけ「開いている間のデータ」（record など）に入れる
   - 確かめた結果をフィールドごとに報告に書く
4. ログの文言は 1 文字も変えない（`landing window closed at the seek cap` などは E2E と解析スクリプトが読む）。ログを出す場所と順番も変えない
5. `LatchSnapshot()` のキー（`seekLandingActive`・`followStartLanding` など）と意味は変えない
6. 触るのは `TimecodeSyncService.cs` と新しい `SeekLandingWindow.cs`（とテストの追加）だけ

## テスト

- `tests/TimecodeSyncPlayer.Tests/SeekLandingWindowTests.cs`: 開く・発生元を守る（D37-f）・追従開始を終える（D37-g）・上限（回数・時間）で閉じる・到着で閉じる・前進なしで閉じる、を 1 件ずつ
- 判定: ビルドの警告 0、非 E2E 全件合格（段 0 の 425 行と `SyncLifecycleLogTests` が緑のまま）、
  E2E 全件合格（`--filter "Category=E2E&FullyQualifiedName!~LtcScenarioE2ETests"`）

## 規則

- **git の書き込み禁止**。親がレビューしてコミットする
- 迷ったら「今の振る舞いを変えない」側を選び、報告に書く
- 終わったら、変えたファイル・3 番の確認結果（フィールドごと）・作ったメソッドの一覧・テストの件数をこの画面に書く
