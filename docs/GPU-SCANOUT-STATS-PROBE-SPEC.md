# 走査時刻の計測（DXGI フレーム統計）の実証仕様

状態: 2026-09-10、実装・実機比較を完了。結果は [計測結果](GPU-SCANOUT-STATS-RESULTS-2026-09-10.md) を参照。

## 目的

[フェンス・vsync実証](GPU-FENCE-VSYNC-RESULTS-2026-09-10.md) では、表示の画像年齢を Present 呼出し時点で測っていたため、tick と vsync の表示遅延の優劣を判定できなかった。`IDXGISwapChain::GetFrameStatistics` と `GetLastPresentCount` で、各 Present がどの vblank で走査されたかを記録し、画像生成から走査までの時間で比較できるようにする。表示方式・同期方式・周期は変えない。

## 記録

- Present 成功直後に `GetLastPresentCount` を呼び、`presentCount → (imageId, generatedQpc, present.start qpc)` を GPU worker 内の小さな表（直近 16 件程度）に保持する。`present.return` イベントの `value` にその presentCount を入れる。
- GPU worker のループの各反復（合成 tick と、vsync／ready の待ち復帰の後）で `GetFrameStatistics` を呼ぶ。返った `PresentCount` が前回記録より進んでいれば、表から対応する画像を引き、`present.scanout` イベントを1件記録する: `qpc`=観測時刻、`imageId`／`generatedQpc`=その画像、`value`=`SyncRefreshCount`、`deadlineQpc`=`SyncQPCTime`（QPC 値、vblank 時刻）、`detail`=`"{PresentCount}:{PresentRefreshCount}"`。同じ PresentCount を二重に記録しない。表に無い PresentCount（表の容量超過や起動前）は `detail` に `unmapped` を付けて記録する。
- `DXGI_ERROR_FRAME_STATISTICS_DISJOINT` は1回だけ `present.stats.disjoint` を記録して続行する（異常扱いしない）。それ以外の失敗は既存の Fault 経路。
- 終了時、未観測の Present を `present.scanout.pending`（件数）として manifest か summary に記録する。
- 呼出しは GPU worker（swapchain 所有スレッド）だけ。keyed mutex／フェンス／lease を持ったまま呼ばない。

## 解析

- `analyze_probe.py` に `scanout` セクションを追加: 窓内の `present.scanout` 件数、`present.start → SyncQPCTime`（表示待ち）、`generatedQpc → SyncQPCTime`（生成から走査）、`compose.publish → SyncQPCTime` の平均／p95／p99／最大、SyncRefreshCount の連続差（1 でない件数＝表示落ち／重複の指標）、unmapped 件数、disjoint 件数。`present.return` の value と scanout の PresentCount の対応を検証し、対応の逆行や重複はエラーにする。古いログ（scanout なし）は `available=false` で従来どおり有効。
- 判定に使う主指標は `generatedQpc → SyncQPCTime` の分布。tick と vsync を同じ指標で比較する。

## 管理テスト

- 統計の対応付け（PresentCount の進み方、二重記録禁止、unmapped、disjoint の1回記録）をフェイクで検証。
- 解析器の synthetic テスト: 正常、逆行、重複、古いログ。

## 実機

同一バイナリで、fence・位相4ms・retry off・monitor index 1（60Hz）。1080p windowed 確認1本（vsync）→ 4K tick→vsync→vsync→tick 各32秒。合格条件: 全 run 有効・error 0、scanout の対応付けエラー 0、unmapped が起動時以外0、SyncRefreshCount 連続差が窓内でほぼ 1。判断: `generatedQpc → SyncQPCTime` の平均・p99 と、連続差≠1 の件数で tick／vsync を比較する。

その後、利用者が DISPLAY1 を 1920×1080@120Hz にした状態で monitor index 0 の tick／vsync を比較する（主画面のため操作窓の重なりを記録）。

## 範囲外

表示方式の変更、タイマー分解能の変更、mpv・CPU アップロード・本体変更。
