# 本体統合の設計（GPU 出力層の段階導入）

状態: 2026-09-11、親が設計。実装は未着手。出力側の確定設計は [確定版](OUTPUT-GPU-DESIGN-CONFIRMED.md)、製品動作の合意は [OUTPUT-PIPELINE-DESIGN.md](OUTPUT-PIPELINE-DESIGN.md)、ソース契約は [GPU-SOURCE-CONTRACT-SPEC.md](GPU-SOURCE-CONTRACT-SPEC.md)、配置は [CANVAS-PLACEMENT-SPEC.md](CANVAS-PLACEMENT-SPEC.md)。別 worktree で進行中の GStreamer 移行（`codex/gstreamer-migration-20260911-0141`）との接続点も定める。

## 1. 現行本体（事実）

- mpv の SW 描画は専用スレッド（`RenderThreadExecutor`）で行い、`RenderedFrameSnapshot`（pool 配列の所有コピー）を `LatestRenderedFrameMailbox` に最新1枚で置く。
- UI スレッドが `_gate` 内で取り出し、世代・Gap・順序を確認し、`OutputFrame.FromSnapshot` を `RenderFramePublishPipeline.Publish` へ同期で渡す。全解像度 `WriteableBitmap` 更新→Spout `SendImage`（CPU 画素、UI スレッド、mutex 100ms・GPU 完了 100ms）→性能記録→Freeze 保存→プレビュー通知の順。
- 全画面は `FullscreenOutputWindow`（WPF `Image` に全解像度 bitmap）、主画面は `PreviewFramePresenter`（縮小、30Hz／全画面中 10Hz）。
- 終了は `MainWindow.Dispose` が `stopRender`（`RenderSession.Stop`）→ `disposeBuffer`（`RenderSession.Dispose`：context 解放→バッファ→スレッド）→ `disposeSpout` の順で呼ぶ。確認ダイアログや強制終了はない。
- `hwdec=no`。デコードは CPU。

## 2. 目標構造

```text
UI スレッド                    GPU worker（OutputEngine）              Spout worker
 ─ タイムライン状態 ──▶ TimelineOutputState mailbox（不変レコード）
 ─ 操作（キャンバス、  ──▶ コマンド queue
    カード、世代）
                              IVideoSource.TryAcquire(世代, 位置)
                              └ MpvSnapshotSource（CPU→GPU アップロード）
                              └ GStreamerSource（別 worktree、D3D11VA）
                              合成（配置、Black/Frozen/GapFreeze、カード）
                              合成 pool（3枚、lease、フェンス）
                              全画面 Present（vblank 位相）        最新画像コピー→SendTexture
                              プレビュー用縮小 staging（≤960×540、30/10Hz）
 ◀─ 診断・デバイス消失 ── イベント（Dispatcher.BeginInvoke、待たない）
```

- UI は GPU worker を同期待ちしない。GPU worker は UI を同期待ちしない。受け渡しは不変レコードの mailbox と有限 queue。
- 既存の CPU 経路（`OutputFrame`→Bitmap→`SendImage`）は設定 `OutputBackend=Cpu` で残し、既定は移行完了まで Cpu。

## 3. 本体側の新しい型（案）

| 型 | 責務 | 置き場所 |
| --- | --- | --- |
| `IVideoSource`／`ISourceImageLease`（試作から移す） | ソース契約 | `Contracts/IVideoSource.cs`（GStreamer 側もこれを実装） |
| `MpvSnapshotSource : IVideoSource` | mailbox の `RenderedFrameSnapshot` を GPU worker で `UpdateSubresource`／staging へアップロードし、リング（3枚）で供給。世代は `RenderSession` の generation、位置は snapshot 取得時の `time-pos` | 新規 |
| `TimelineOutputState`（record） | Gap 判定（None/Black/Hold/GapFreeze）、Freeze 要求、テストカード ON/OFF、キャンバス設定、クリップ配置、世代 | 新規（`GapFreezeHandler` の判定結果を写す） |
| `OutputEngine` | GPU device／context の所有、合成、pool、全画面、Spout worker、プレビュー縮小、診断、終了 | 新規（試作 `ProbeEngine` の構造を移植） |
| `ComposeLayer` | 配置計算（`CanvasPlacement`）、Black/Frozen/GapFreeze の解釈、カード合成、Freeze 保存（合成前のソース画像を GPU コピー） | 新規 |
| `FullscreenD3DHost : HwndHost` | 全画面ウィンドウ内の子 HWND に swapchain | 新規（`FullscreenOutputWindow` に切替） |
| `SpoutTextureSender` | 別デバイス・別スレッドで `SendTexture` | 新規（`SpoutOutput` は初期化・有効切替・診断のまま。`ISpoutOutput` に `SendTexture` 経路を追加せず、GPU 経路では `SendImage` を呼ばない） |
| 出力トレース | 試作と同じ `events.jsonl` スキーマ（compose/present/scanout/send）を任意で出力し、`analyze_probe.py` を再利用 | 新規（`SyncAccuracyTrace` とは別） |

## 4. 段階と合格条件

各段階は切替可能な状態で終え、実機は 1080p 確認→4K 比較→既存の本体 E2E（FlaUI）の順。

| 段階 | 内容 | 管理テスト | 実機の合格条件 |
| --- | --- | --- | --- |
| 0 | 設定 `OutputBackend`（Cpu 既定／Gpu）、起動時の D3D11.4 可否検出、Gpu 不可なら Cpu へフォールバックしログ | 設定の読み書き、フォールバック | 既存 1,503 件が全て通る（回帰なし） |
| 1 | `OutputEngine` 骨格: デバイス、pool、フェンス、全画面（HwndHost）、vblank 位相・合成整列、Spout worker、プレビュー縮小 staging。ソースなし（黒キャンバス＋カード）。終了順序 | 判断クラス（vblank／整列／pool／終了順序）を試作の self-test から xUnit へ移植 | 4K キャンバスで合成・表示・Spout 60Hz、表示落ち 0、正常終了・強制終了、プロセス残存なし |
| 2 | `MpvSnapshotSource`（CPU アップロード）と `TimelineOutputState` の受け渡し。Black/Frozen/GapFreeze/Hold を合成層で解釈。Freeze 保存はソース画像の GPU コピー | 世代排除、Gap 判定の写像、Freeze の元画像が合成前であること、`afterFrameProcessed` の呼出し条件 | 1080p 実動画で LTC 同期・シーク・Freeze/Black が既存と同じ挙動。4K でアップロード時間の分布と合成 60Hz 維持を測る（ここで CPU 経路の上限が出る） |
| 3 | Spout の GPU 送信を既定化（Gpu backend 時）。`SendImage` の CPU 経路は Cpu backend 専用 | 有効／無効切替、失敗時の再送と手動再試行 | 公式受信機で 4K 60Hz、異なる画像 ID、送信年齢 |
| 4 | キャンバス設定（`ProjectData.Canvas`、`TrackData.Fit`、未設定時の初回選択）、テストカード（図柄は最小: 外周枠・十字・番号）、サイズ変更は停止中のみ | 保存形式の後方互換、配置の継承・上書き、停止中判定 | 素材切替でキャンバス不変、カード ON/OFF で再生状態不変 |
| 5 | 終了ダイアログ（キャンセル既定／通常／強制）、GPU デバイス消失の一度だけ自動復旧＋手動再試行 | 状態遷移 | 復旧後に現在位置の映像へ復帰、二重確認なし |
| 6 | `GStreamerSource`（別 worktree の shim を `IVideoSource` に載せる）。`video/x-hap` 分岐は後続 | ソース契約テストに合格 | H.264/HEVC 4K60 の実素材で 2 と同じ確認 |

段階 2 の結果で「CPU アップロード経路で足りるか」が決まる。足りなければ段階 6 を前倒しし、CPU 経路は 1080p 以下の互換経路として残す。

## 5. スレッドと所有権の契約

- GPU worker: D3D11 device／immediate context／swapchain／合成 pool／source テクスチャ／高分解能タイマー。Present と `GetFrameStatistics` もこのスレッド。
- Spout worker: 送信用デバイス（同一アダプター）、開いた共有テクスチャ、送信用テクスチャ、`spoutDX` オブジェクト、アクセス mutex。
- UI: `TimelineOutputState` の生成、コマンド発行、プレビュー bitmap の受け取り（縮小 staging の CPU 読み戻しは GPU worker が行い、完成した配列を UI へ渡す。UI は BackBuffer を待たない）。
- mpv 専用スレッド: 変更なし。`MpvSnapshotSource` は mailbox から取り出した snapshot の lease を GPU worker のアップロード完了まで保持し、アップロード後に返す。
- 停止順序: UI が新規受付停止 → `RenderSession.Stop`（mpv 側の新規描画停止） → `OutputEngine.Stop`（合成停止、Spout worker join、表示停止、pool の lease 返却待ち） → `OutputEngine.Dispose`（Spout 側資源→合成側資源→デバイス） → `RenderSession.Dispose` → `SpoutOutput.Dispose`。強制終了は待たずにプロセス終了。

## 6. GStreamer worktree との接続

- `Contracts/IVideoSource.cs` は本 worktree が定義する。GStreamer 側は shim の `acquire(gen)` リース API をこの契約へ適合させる薄いアダプター（`GStreamerSource`）を書く。契約テスト（`SourceImageRing` 相当の規則）は本 worktree のものを使う。
- デバイスは `OutputEngine` が作り、GStreamer 側へ渡す（`GstD3D11Device` のラップ）。GStreamer 側が別デバイスを要する場合は共有フェンスの責任をソース側に置く。
- 統合時の衝突予測: `MainWindow.xaml.cs`（バックエンド切替・全画面）、`RenderSession.cs`（世代・mailbox の露出）、`App.xaml.cs`（起動時検出）。GStreamer 側は再生制御・`Mpv*`・`PlaybackController` 周辺を触る見込みで、重なりは `MainWindow` の再生開始／切替部分に限られる。両者とも `ProjectSerializer.cs` は段階 4 まで触らない。

## 7. 検証の方針

- 管理テスト: 試作の self-test（pool、vblank、整列、ソース契約、配置）を xUnit へ移植し、本体の非E2E に加える。GPU なしで動くこと。
- 実機: 本体からも試作と同じ `events.jsonl` を出し、`analyze_probe.py` で合成／表示／Spout／走査の指標を比較する。基準は試作の fence＋vblank＋align の値。
- 既存 E2E（FlaUI）は Cpu backend で従来どおり、Gpu backend では全画面・Spout 有効化・終了の 3 経路を追加。

## 8. リスクと決め事

- WPF と D3D の airspace: 全画面は HwndHost の子 HWND（操作 UI を重ねない）。主画面プレビューは当面 CPU 読み戻し（≤960×540、30/10Hz）で `WriteableBitmap` を維持し、GPU プレビューは後段。
- CPU 読み戻しはプレビューだけに限定し、全画面・Spout 経路には入れない。
- mpv の 4K SW 描画自体は GPU 化で速くならない。段階 2 の測定で判断する。
- 合成周期は主表示の実周期に追従する（整列時）。表示がない構成では 60Hz 自由走行。Spout はどちらでも合成に対する位相で従う。
- 過去の `OutputFrame` 基盤（CPU）は Cpu backend として残し、削除しない。

## 9. 実装単位（依頼順）

1. `Contracts/IVideoSource.cs`＋契約テスト移植（本体テストプロジェクト）。
2. `OutputBackend` 設定とフォールバック。
3. `OutputEngine` 骨格（試作からの移植、黒キャンバス）＋ `FullscreenD3DHost`＋終了順序＋トレース。
4. `MpvSnapshotSource`＋`TimelineOutputState`＋`ComposeLayer`（Black/Frozen/GapFreeze、Freeze 保存）。
5. Spout GPU 送信の既定化。
6. キャンバス設定・テストカード。
7. 終了ダイアログ・デバイス消失復旧。
8. `GStreamerSource`。

各単位はサブエージェントへ「変更ファイル・契約・テスト・実機の合格条件」を指定して依頼し、親がレビューと実機評価を行う。
