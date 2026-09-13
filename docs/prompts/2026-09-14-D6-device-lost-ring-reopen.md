# D6: 解像度変更をまたぐトラック切替で GPU デバイス消失 → 旧サイズのリング再オープン → 黒のまま

2026-09-14 記録。**未修正。再現手順と観測した事実のみ。**

D5（GStreamer の lease 返却漏れ）の再現系を 720p → 1080p の切替で動かしたときに
見つかった別件。D5 の失敗 run（`TestResults/d4v/d4-gst`）にはデバイス消失の記録が
無く、**D5 とは別の欠陥**である。

---

## 再現手順

E2E（`tests/TimecodeSyncPlayer.Tests/E2E/D5BlackAfterSwitchReproE2ETests.cs`）を使う場合:

1. `RunSpoutObservation` の対象メディアを解像度の異なる組にする
   （例: `media = artifacts/media/test_720p60_long.mp4`（1280x720）、
   `nextMedia = artifacts/media/d1-60s.mp4`（1920x1080））
2. `GpuCompositor_TrackSwitchWithoutHook_PublishesContentAfterSwitch`（フック無し）を実行。
   settings は `{"backend":1,"outputBackend":1}`（GStreamer × GPU 合成）、
   起動 10.5 秒後に `BtnNextTrack`、Spout 受信 18 秒。

手動で行う場合:

1. settings `{"backend":1,"outputBackend":1}` でアプリを起動:
   `TimecodeSyncPlayer.exe --open "<...>\test_720p60_long.mp4" --playlist "<...>\d1-60s.mp4"`
2. `BtnSpout` を ON。
3. Spout 受信を開始:
   `tcs-gst-proto.exe recv <sender> 18 <prefix>`（BMP を書き出す）
4. 起動 10.5 秒後に `BtnNextTrack` を押す。
5. 保存された BMP の黒比率（RGB 各 < 16 の割合）を見る。

## 観測した事実

- 切替前の受信フレームは映像（黒比率 0.00）。
- 切替直後（+0.1〜0.2 秒）に保存された最後のフレームが完全な黒（黒比率 1.00）。
  以降、受信は 18 秒間ハッシュが変化しない（黒のまま）。
- アプリログ（2026-09-14 06:28 の実行、テスト出力の exe ディレクトリのログ）:

  ```
  [ERR] OutputEngine: GPU デバイス消失
  TimecodeSyncPlayer.Output.GpuDeviceLostException: compose.drain: device removed 0x887A0005
     at TimecodeSyncPlayer.Output.GpuFence.Wait(String stage) ...
     at TimecodeSyncPlayer.Output.OutputEngine.ComposeTick(Int64 scheduled, Int64 nextScheduled) ... (finally の gpu.Fence.Wait("compose.drain"))
     at TimecodeSyncPlayer.Output.OutputEngine.Loop() ...
  [WRN] OutputEngine: GPU 復旧を開始
  [INF] GStreamerSource: 共有リングを開きました 1280x720 slots=3
  [INF] OutputEngine: GPU 復旧が完了
  ```

  切替先の clip は 1920x1080 だが、復旧直後に開いたリングは **1280x720**。
  以降、リングの開き直しログは無い（`EnsureRing` は `ring != null` で再利用する）。
- 同一解像度の切替（720p → 720p、1080p → 1080p）では、デバイス消失も黒も起きない
  （同じテストで計 3 回確認）。
- 親の D5 失敗 run `d4v/d4-gst` の実行ログには、デバイス消失の記録は無い。

## 関連するコード位置（確認した範囲）

- `OutputEngine.ComposeTick` の finally: `gpu!.Fence.Wait("compose.drain")`
  （device removed 0x887A0005 の発生箇所）
- `GStreamerSource.TryReopenOn` / `EnsureRing`: 復旧時にリングを開き直すが、
  以後 `ring != null` のため `player.TryGetRingInfo` を再取得しない
- 復旧フロー本体: `OutputEngine` の GPU 復旧（`GpuRecoveryPlan` / `GpuRecoveryState`）

## 未確認（次に確かめること）

- デバイス消失の一次原因（解像度変更時の shim 側処理か、ドライバの TDR か）
- 旧サイズのリング再利用が黒の直接原因であることの確定
