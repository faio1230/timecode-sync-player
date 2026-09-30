# 報告: 停止モードで保持の損失のまま読み込むと 0 から走る件（規則 4 の読み込みの入口）

作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v054`（起点 01319b9）。指示書は `docs/prompts/2026-09-28-stop-load.md`。

## 原因（コードで確かめたこと）

- アプリの位置なしの読み込み（`PlaybackOperationsCoordinator.LoadFile`）は、読み込み → `SetPaused(false)` →
  `ApplyPauseState(false)` の後に `BeginSyncFileLoad(0)`（= `TimecodeSyncService.BeginFileLoad`）を呼ぶ。
  同期の読み込みの入口に来た時点で再生は既に動いている。
- 損失（`IsLost`）のまま次の `Evaluate` / `ObserveHeldFrame` が `ObservePlaybackState` を通ると、
  「持ち主が信号断で、再生が動いた」を利用者の再開とみなし、`_pausedByPolicy = false`・
  `_manualResumeSuppressesPause = true` にする。損失のままなので `EvaluatePause` は抑止の印で何もしない。
  その結果、新しいトラックが 0 から走り続けていた。

## 変更の要約

1. `LtcSignalLossPolicy.OnFileLoad(context)` を追加。監視中・損失中・停止モード・読み込み後に再生が動いている
   ときだけ、読み込みが始めた再生を利用者の再開と取り違えないよう `_pausedByPolicy` を下ろし、
   `_lastIsPlaybackPaused` を今の状態にしてから、規則 4 の入口と同じ `EvaluatePause` を返す。
   利用者が損失中に再開していた（抑止の印がある）ときは印を残すので一時停止しない。読み込みが一時停止のまま
   （位置つきの読み込みで `keepPaused`）のときは何もしない（持ち主は今のまま）。
2. `LtcSyncController.OnSyncServiceLifecycle`（`FileLoad`）の最後で `PauseOnFileLoadDuringLoss` を呼ぶ。
   `Pause` が返ったら保持着地の記録（`HeldLossLandingSeconds`）を下ろし、`ApplySignalLossAction(Pause,
   landOnHeldValue: false)` で一時停止する（持ち主 = 信号断）。この時点では着地しない。
3. 着地は D35 の経路（`ShouldLandOnFirstHeldValueDuringPause`、保持のフレーム）に任せる。この判定に
   `!_syncService.IsWaitingForLanding` を足した（規則 3 と同じく、読み込み・シークの着地を待っている間は
   判定しない）。着地先は今どおり `TryGetHeldLandingTarget`（尺と fps が分かってから、保持に 1 回）。
4. 復帰（新しい値のフレーム）は変更なし。`ObserveValidFrame` の N 枚で `ResumeAndSync` → 規則 2〜3。
5. RunThrough は `OnFileLoad` が停止モード以外で何もしないので今どおり 1.0 で走る。定数と仕組みは足していない。

変更したファイル: `src/TimecodeSyncPlayer/LtcSignalLossPolicy.cs`、`src/TimecodeSyncPlayer/LtcSyncController.cs`。

## テスト

- `StopModeLoadDuringHeldLossTests`: 2 件とも成功（Single の手動の読み込みの 1 本、Continue の切替の 1 本）。
  赤であることは親が修正前に確認済み（このツリーでは修正前の実行はしていない）。
- 非E2E（`--filter "Category!=E2E"`）: 合計 2769、成功 2769、失敗 0、スキップ 0。
  ffmpeg を使う 2 本の間欠はこの回では出なかった。

### 既存テストの区分

| 区分 | 件数 | 内容 |
|---|---|---|
| (a) 仕組み | 0 | - |
| (b) 再発 → 新しい状態で緑 | 0 | - |
| (c) 赤のまま調査 | 0 | - |

既存テストの期待は変えていない。

## 実機でこの状態になる操作（コードで追ったもの。E2E の候補）

前提はどれも: 停止モード、同期オン、LTC 監視中、LTC を一定値で保持（または無音）にして損失の一時停止に入った後。

| # | 操作 | 経路 | この修正の対象か |
|---|---|---|---|
| 1 | 「次へ」ボタン | `BtnNextTrack_Click` → `LoadCurrentPlaylistTrack` → 位置なし `LoadFile` → `BeginSyncFileLoad(0)` | 対象（テストの Single の 1 本と同じ型） |
| 2 | 「前へ」ボタン | `BtnPreviousTrack_Click` → 同上 | 対象 |
| 3 | プレイリストの項目のダブルクリック | `PlaylistList_MouseDoubleClick` → 同上 | 対象 |
| 4 | 再生中のトラックをプレイリストから削除 | `TrackRemoved` → 次のトラックがあれば `LoadCurrentPlaylistTrack` | 対象 |
| 5 | プレイリストをファイルで置き換え（ドロップ等） | `StopPlayback` → `ReplaceWithFiles` → `LoadCurrentPlaylistTrack` | 対象（`StopPlayback` の後なので、読み込みの時点の損失の状態は実機で確かめる必要あり） |
| 6 | Continue のトラック切替（`ContinueOnTrackCoordinator`） | 停止モードの損失中は同期が抑止されるので切替は起きない。損失の前に切り替わり直後に保持に入る型はテストの Continue の 1 本（修正前から緑） | 守り |
| 7 | 終端での自動の次へ（Single／Continue） | `TryAdvancePlaylistAtEnd` は一時停止中は何もしない | 起きない |
| 8 | GPU 復旧の読み直し | 位置つき `LoadFile` は `keepPaused` で一時停止のまま → `OnFileLoad` は何もしない（持ち主はそのまま） | 対象外（今どおり） |
| 9 | プロジェクトを開く | `LoadFilePaused`（一時停止の読み込み）→ `OnFileLoad` は何もしない | 対象外（今どおり） |

E2E にするなら 1（次へ）が最短。確かめる点: 読み込みの後に一時停止のまま（信号断の理由の表示）、保持値の位置へ
1 回着地（`held-landing` のシーク 1 回）、LTC を進め直すと再開して relocate。

## 未解決の疑問

1. 実機では読み込みの直後の尺が 0 で、着地は UI タイマーが尺を取れた後の保持のフレームになる。読み込みの
   直後に一時停止した場合に、shim が新しい世代の最初のフレームを出して着地待ちが明けるか（明けなければ
   着地の安全タイムアウトまで着地が遅れる）はコードだけでは確かめられない。実機のログで
   `landing seek issued` までの時間を見る必要がある。
2. `ShouldLandOnFirstHeldValueDuringPause` に着地待ちの条件を足したので、D35 の経路（無音の損失で止めた後に
   初めて保持値が届く）でも、シークの着地待ちの間は着地が後ろへずれる。既存テストに赤は出ていないが、
   実機でこの型の遅れを測ったものは無い。
3. 表の 5（置き換え）は `StopPlayback` を挟むので、読み込みの時点の再生の状態と損失の状態を実機で確かめて
   いない。

## 追補（E2E）

親の依頼（2026-09-28）で、実機で確かめる E2E を 1 本足した。書いただけで、実機では回していない。

- 追加: `tests/TimecodeSyncPlayer.Tests/E2E/LtcScenarioE2ETests.cs` の `R5_StopMode_NextTrackDuringHeldLoss_StaysPausedAndLandsOnHeldValue`（R-5）。
  R-1〜R-4 の後ろに置いた。`run-ltc-scenarios.ps1` の既定のフィルタ（`FullyQualifiedName~LtcScenarioE2ETests` を含む）で回る
  （`--list-tests` で R-4 の次に出ることを確かめた）。
- ビルド: テストのプロジェクトのビルド 1 回、警告 0・エラー 0。非E2E の全件と E2E は回していない。

### 流れと判定

Single（`continueMode: false`）で回す。表の 1（「次へ」ボタン）の形。

1. A を読み込んで再生、同期オン、停止モード。LTC を `A.MediaIn + 3` から 4 秒進め、位置が `A.SingleTarget(LTC)` の ±0.3 秒に入るのを待つ。
2. `A.MediaIn + 7` で保持（`PlayHeld` を 40 秒。復帰の `Play` が止める）。一時停止を待ち（R-2 と同じ上限）、
   `hold-pause` をジャーナルに書く（`ltc-run-report.ps1` の hold-pause の集計にも入る）。A の位置が保持値の 1 フレーム以内になるのを待つ。
3. `BtnNextTrack` を直接押す（`LoadTrack` は準備待ちで再生・一時停止を揺らすので使わない）。ログの
   `Playlist track loaded index=<B>` を 10 秒待ち、2 秒以内に一時停止であることを待つ。
4. B の位置が `B.SingleTarget(保持値)` に入るのを 10 秒待つ（幅は B の範囲の中なら 1 フレーム、範囲外なら境界ホールドの ±2 フレーム）。
   1.5 秒後に次を判定する: 一時停止のまま、`LtcSignalLossPauseReason` が空でない、位置が 1 フレームを超えて動かない、
   B の範囲の中なら「次へ」以降の `LTC timecode held: landing seek issued` がちょうど 1 回。B の絵（参照一致）を 3 秒待つ。
   観測値は `load-during-hold` に書く（着地までの秒数、同期のシークの本数を含む）。
5. LTC を進め直す（保持値が B の範囲の中で 8 秒の余裕があれば保持値から、無ければ `B.MediaIn + 3` から）。
   4 秒以内に再開し、8 秒以内に `B.SingleTarget(LTC)` の ±0.3 秒に入るのを待つ。`resume-follow` に書く。

新しい待ち時間の定数は足していない（上の秒数はどれも R-1・R-2・S-4 の待ちの値）。

### 気にしている点

1. 既定のプロジェクト（色素材の A/B/C、どれもクリップ [0, 20]）では保持値 7.0 は B の範囲の中で、着地のシークは 1 回の判定になる。
   実素材（M1〜M7）で保持値が B の範囲の外になると、端は境界ホールド（D33）が受け持ち、着地のシークが出ないことがある。
   このため範囲外では着地の本数を判定せず、ジャーナルに残すだけにした。
2. 着地までの時間は、実機では尺の取得（UI タイマー）と読み込みの着地を待つ（報告本体の未解決 1）。10 秒の待ちで足りない場合は、
   `load-during-hold` の `secondsToLanding` と、アプリのログの `landing deferred` の行を見る。
3. `ltc-run-report.ps1` の先頭のコメントは「R-1〜R-4 の hold-pause」のままにした（集計は test ID を問わないので R-5 も入る）。
