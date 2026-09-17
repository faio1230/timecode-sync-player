# D33: Single モードで LTC が MediaOut を越えても終端で止まらない／速いロードでメタデータ取得が抜ける

作成: 2026-09-17 20:19、親。担当: 同期担当（`w5:p3`、作業ツリー `timecode-sync-player-wt-a`、ブランチ `agent-a`）。
基点: main の最新（D32 統合後 `adc8200` 以降）を `agent-a` へ通常マージし、shim を自分のツリーでビルドしてから。
根拠: `docs/analysis/2026-09-17-D33-single-mediaout-and-fetchmetadata.md`（同期担当の解析）。仕様: 利用者の検証項目 7「Single で範囲外の LTC → その動画の終端で止まる」（`docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md` 1 節）。
**製品コードの変更。Smooth/Jump の補正量・サンプル時計には触らない。Single の終端ホールドと、メタデータ取得の予約だけ。**

## 1. 欠陥

| # | 観測（検証機、実素材、MediaIn=5 / MediaOut=25 / 素材全長 2 分超） | 仕様 | 当たり |
| --- | --- | --- | --- |
| D33-a | Single + 同期 ON で LTC 40 のとき position=31.4（D29 で 25 へ着地したあと再生が続く。生成素材は尺 = MediaOut なので EOS で止まり気づかない） | 範囲外の LTC では **MediaOut の最終フレームで静止**。LTC が範囲に戻れば追従再開。MediaIn より前も同様に MediaIn で静止 | `SingleModeSyncCoordinator.Apply` に終端ホールドが無い。`LtcSyncController` の Single の残差・Jump シーク先が [MediaIn, MediaOut] に clamp されていない |
| D33-b | 速いロード（キャッシュ済みプロファイル、<250ms）が次のロードに 1 tick 以内で置き換わると `FetchMetadata` が走らず、ログ行が出ず `MetaLine` が前トラック表示のまま残り得る（383 回中 5 回） | ロードごとにメタデータが更新される | 取得が 100ms タイマーの tick 依存（`MainWindow.xaml.cs` 1802-1807）で、`ResetPlayerStateForNewTrack` がフラグを戻す競合 |

## 2. 直すこと

1. **D33-a 終端ホールド**: Single + 同期 ON で、目標（D29 の clamp 後）が `clipOut`（`MediaOut ?? 尺`）または `clipIn`（MediaIn）に貼り付いており、再生位置がその ±許容内なら、シークも補正もせず一時停止して「終端ホールド」をラッチ。LTC が範囲内（許容分だけ内側）へ戻ったらラッチを解除して再開・追従。ホールド中は Smooth の残差補正が動かないこと（`IsPlaybackPaused` ガードで止まる想定だが、ラッチで明示する）。生成素材（尺 = MediaOut）では EOS と二重にならないこと
2. **D33-a Jump の clamp**: `SyncCorrectionController.EvaluateJump` が返す生の目標と `LtcSyncController` の Single 残差計算を、D29 と同じ [MediaIn, MediaOut ?? 尺] に clamp する（越えたシークを封じる）
3. **D33-b**: ロード完了側（`PlaybackOperationsCoordinator.LoadFile` 成功後、位置つきロードは `RefreshPositionDisplayAfterLocatedLoad`）で `FetchMetadata` を 1 回予約する（`Dispatcher.BeginInvoke`）。サイズ未取得なら従来どおりタイマーが再試行。shim からロード結果でサイズ・fps を返す根本策は今回は見送り（報告に候補として書く）
4. 検証機の S-3 は「A の終端に止まる」の判定が **テスト側でも素材全長で clamp**されている（`LtcScenarioE2ETests.SingleTarget`、TrackInfo.Duration = MediaDuration）。テスト側の修正は除去担当が別に行う（[MediaIn, MediaOut] で clamp）。あなたは製品側だけ

## 3. 検証

- 単体: `SingleModeSyncCoordinator` / `LtcSyncController` に「clipOut 越えの LTC → 終端ホールド（シークなし・一時停止）」「範囲に戻ると解除して追従」「MediaIn より前 → MediaIn でホールド」「Jump の目標が clamp される」「生成素材（尺 = MediaOut）で二重停止しない」。`FetchMetadata` の予約は既存のテスト形式で 1 本
- E2E（実機は一報のうえ親の合図。除去担当と重ねない）: `LtcScenarioE2ETests` の S-1〜S-5、R-1〜R-4 を生成素材で。さらに **MediaOut < 尺** を作るため、`make-ltc-scenario-project.ps1 -SegmentSeconds 8`（生成素材 12 秒なら MediaOut=8 < 尺）で S-3 を 1 回。V3（Smooth、LTC25）を 1 本（補正には触らないが Single 経路を通るため）

## 4. 報告

コミット（欠陥ごと）、変更の要約、単体の増減、E2E の結果、証跡パス、設計差異。**合否は書かない。素材名・絶対パスを書かない。版は上げない。**
