# 共有フェンス同期と表示通知駆動の実証仕様

状態: 2026-09-10、実装・実機比較を完了。結果は [実証結果](GPU-FENCE-VSYNC-RESULTS-2026-09-10.md) を参照。本体へのGPU統合は開始しない。

## 目的

[契約の突き合わせ](OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md) のレビューで挙げた懸念1・2を、独立試作で切り分ける。

1. **読者同士の排他をなくす。** 現在の共有sourceは keyed mutex で、全画面描画（GPU worker）と Spout コピー（送信 worker、別デバイス）が読者同士で衝突する。[保持区間実証](GPU-MUTEX-HOLD-RETRY-RESULTS-2026-09-10.md) で衝突は全件この読者同士の重なりだった。D3D11.4 の共有フェンス（`ID3D11Fence`、`FenceFlags.Shared`）で「合成の書き終わり→読者」の順序だけを GPU キュー上で保証し、CPU 側の排他を持たない構成を比較する。
2. **表示を自由走行の合成tickから切り離す。** 現在の tick 方式は合成直後に表示準備を確認し、未準備ならその枠を飛ばす。ready 方式は1枠1回の待ちに限定している。表示側が swapchain の表示通知（frame latency waitable object）を自分の時計として、新しい画像がある限り通知ごとに1回表示する方式を比較する。合成60Hz・Spout60Hzは変えない。

両者は独立したスイッチにし、効果を個別に測る。固定位相や固定待ちの調整は行わない。

## 1. `--source-sync keyed|fence`（既定 keyed）

- `keyed` は現行のまま（`SharedKeyedMutex`、待ちなし `AcquireSync(0,0)`、`display.mutex.*`／`copy.mutex.*` 記録、`--copy-retry`）。
- `fence` は split かつ Spout を含む出力でのみ許可（common は単一デバイスで対象外）。source texture は `ResourceOptionFlags.Shared | SharedNtHandle` で作り、keyed mutex を付けない。`IDXGIResource1.CreateSharedHandle`（`SharedResourceFlags.Read`）で NT ハンドルを作り、送信デバイスは `ID3D11Device1.OpenSharedResource1<ID3D11Texture2D>` で開く。開いた後、作成側は自分の NT ハンドルを `CloseHandle` する（legacy ハンドルと異なり所有する）。
- 合成デバイスで `ID3D11Device5.CreateFence(0, FenceFlags.Shared)`。`ID3D11Fence.CreateSharedHandle` で NT ハンドルを作り、送信デバイスは `ID3D11Device5.OpenSharedFence`。同様に作成側はハンドルを閉じる。デバイスが `ID3D11Device5`／`ID3D11DeviceContext4` を提供しない場合は起動時に `fence` を拒否し、理由を manifest に記録する。
- 合成: 描画命令の後に `ID3D11DeviceContext4.Signal(fence, imageId)`（画像IDを単調増加のフェンス値に使う）。既存の CPU 側 EVENT クエリ完了確認と `compose.publish` の順序は維持する（pool の公開契約を変えない）。stamp にフェンス値を持たせる。
- 表示（同一デバイス）: keyed mutex 取得の代わりに何もしない（immediate context の順序で十分）。記録は `display.fence.wait` を value=フェンス値で1件出し、区間記録は作らない。
- Spout コピー（別デバイス）: `CopyResource` の前に送信側 context4 で `Wait(fence, stamp.Id)`。CPU はブロックしない。記録は `copy.fence.wait`（value=フェンス値）。keyed mutex がないので `copy.keyedMutexBusy` は発生せず、`--copy-retry signal` は `fence` と併用不可（拒否）。
- 逆方向（読者→書き手）の危険は現行どおり CPU lease で防ぐ。合成は読者0かつ最新でない領域にしか書かず、読者は GPU 完了確認後に lease を返す。この契約は変えない。
- 終了: 送信 worker は開いた texture とフェンスを解放してから終了し、その後に合成側が source・フェンス・デバイスを解放する。既存の join 順序を維持する。
- 異常: `Signal`／`Wait` の失敗、デバイス消失は既存の Fault 経路。フェンス値の逆行は self-test で拒否する。

## 2. `--display-pacing vsync`（tick／ready に追加）

- 全画面出力を含む場合のみ許可。split／common 両方で可。Spout と合成の周期・位相は変えない。
- GPU worker のループを次にする。合成 tick は従来どおり 60Hz のスケジュールで期限管理する。
  1. 次の合成期限までの待機で、`WaitForMultipleObjects({stop, ready}, timeout=次の合成期限まで)` を使う。ただし **ready を待つのは「最後に表示した画像IDより新しい最新画像がある」ときだけ**。新しい画像がなければ stop だけを timeout 付きで待つ（通知が立ったままの busy loop を避ける）。
  2. ready で復帰したら、その時点の最新 lease を取り、ID が最後に表示した ID より大きければ描画→GPU完了確認→source返却→Present。同じ ID なら表示せず通知は消費しない（`display.vsync.skip` detail `noNewerImage`）。
  3. 合成期限に達したら合成を優先し、合成後は表示をインラインで試みない（表示は通知経由のみ）。表示の遅れた枠を貯めない。
  4. 通知1回につき表示は最大1回。表示の予定時刻という概念を持たず、`display.vsync.wait.start/end`（detail `native`、end detail `ready`／`timeout`／`cancelled`、value=待った ms 相当は既存規則に合わせる）と、既存の `display.select.*`、`display.draw.*`、`present.start/return` を記録する。`present.notReady` は vsync では発生しない。
- 停止通知は index0 で優先し、復帰後に停止・期限を再確認する。待機中に lease・keyed mutex・フェンス待ちを持たない。
- Present は `Present(1)` のまま。swapchain の設定（flip-discard、buffer 2、latency 1）は変えない。
- 期待: 60Hz 表示では表示頻度≈60、`notReady` 由来の表示落ちがなくなる。120Hz 表示では新画像ごとに1回（合成60Hz上限）。これは表示先の周期に表示を従属させ、Spout・合成は従属させない、という設計文書の区別に沿う。

## 記録・解析

- manifest.options に `sourceSync`、`displayPacing` を記録。フェンス非対応で拒否した場合は `startupFailed` と理由。
- 解析器 `analyze_probe.py`: `sourceSync=fence` では `display.mutex.*`／`copy.mutex.*` の不在と `copy.keyedMutexBusy` 0 を要求し、`copy.fence.wait` が各 `copy.start` に先行することを検証。`displayPacing=vsync` では、各 `present.start` に先行する `display.vsync.wait.end`(ready) が1対1で対応し、表示画像 ID が単調増加、同一 ID の再表示なし、`present.notReady` なし、を検証する。既存 tick／ready ログの判定は変えない。
- `analyze_mutex_holds.py`: fence ログでは hold 統計を null、`copy_busy_first` 0 として動く。
- 管理テスト: オプションの組み合わせ検証（fence は split＋Spout、retry と排他、vsync は表示必須）、フェンス値の単調性、vsync の「新画像がないときは ready を待たない」「通知1回に表示1回」「合成期限優先」「停止優先」を、GPU なしのフェイク時計・フェイク待ちで検証する。

## 比較条件と手順

同一バイナリ。split／both／60Hz／mutex 8ms／present-wait 0／fixed／公式受信機／monitor index 1（DISPLAY2 1080p/60Hz）。直列実行、`Invoke-Trial.ps1` に `-SourceSync` を追加して使う。

| 段階 | 条件 | 目的 |
| --- | --- | --- |
| 1080p確認 | windowed 12秒、fence＋vsync、位相4 | 新経路2つの正常終了・ログ整合・解析有効 |
| 4K A: 同期方式 | tick、位相0.5ms（負荷）、retry off、keyed→fence→fence→keyed、各32秒 | 読者同士の衝突が fence で消えるか。Spout 再送・年齢、逆方向 busy、合成60Hz、CPU |
| 4K B: 表示駆動 | 位相4ms、A で合格した同期方式、tick→vsync→vsync→tick | 表示頻度・表示画像年齢・最大間隔・未準備件数・合成開始遅れ・CPU |
| 追加（任意） | DISPLAY1 を 1920×1080@120Hz にして monitor index 0 で vsync | 表示周期≠60Hz での挙動。主画面のため操作窓の重なりを記録。利用者の設定変更が必要 |

## 合格条件

- 全 run 正常終了・解析有効・error 0・プロセス残存なし。
- A: `fence` で窓内 `copy.keyedMutexBusy` と `present.keyedMutexBusy` が0、Spout の同一ID再送が `keyed` の負荷条件（前回 175／354）より減り、年齢最大が短縮、合成60Hz維持、CPU が `keyed` と同等以下。ABBA 両組で同方向。
- B: `vsync` で `present.notReady` 0、表示頻度が 60Hz 表示で 59.9 以上、表示画像年齢の平均が tick と同等以下、表示最大間隔が悪化しない、合成開始遅れが悪化しない。
- 満たした場合、後続の比較基準を `fence`＋`vsync` に更新する。CLI 既定は変えない。本体採用・GPU統合の判断ではない。

## 範囲外

mpv・CPUアップロード・GPUプレビュー・本体変更、hwdec、フェード、Spout 無効化問題、OBS 問題。
