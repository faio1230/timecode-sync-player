# 共有フェンス同期と表示通知駆動の実証結果（2026-09-10）

## 判断

[実証仕様](GPU-FENCE-VSYNC-PROBE-SPEC.md) に従い、独立試作へ `--source-sync fence` と `--display-pacing vsync` を追加し、同一バイナリで 1080p 確認と 4K の直列比較を完了した。本体へのGPU統合は開始していない。

- **懸念1（読者同士の排他）は解消できる。** 共有フェンス方式では keyed mutex を持たないため、全画面描画と Spout コピーの衝突（`copy.keyedMutexBusy`／`present.keyedMutexBusy`）が構造的に0になった。負荷条件（位相0.5ms）で Spout の送信画像年齢の最大は keyed の 89／122ms から 23／22ms に短縮し、合成開始遅れの平均は 0.08ms から 0.007ms に、アプリCPU秒も 9.5／8.5 から 7.4／7.7 に下がった。合成・Spout 公開は 60Hz を維持。後続の比較基準を `fence` とする（`--copy-retry` は keyed 専用の緩和策として残す）。
- **懸念2（表示の時計）は構造として成立したが、60Hz 表示では利点を数値で示せない。** vsync 方式は表示通知ごとに1回表示し、表示未準備による見送りは0、表示60Hz、通知1320回に表示1320回。ただし通知が合成から約9ms後に来る起動と即時に来る起動があり（ready 方式と同じ起動依存）、Present 呼出し時点の画像年齢は 9.5ms と 0.47ms に分かれた。この年齢は走査開始時刻ではなく、tick 方式との表示遅延の優劣は現在の計測では判定できない。既定は tick のまま、vsync は候補として残し、DXGI のフレーム統計（`GetFrameStatistics`）による走査時刻の計測と、60Hz 以外の表示先での比較を次の判断材料にする。
- 表示未準備の多発（`present.notReady`）は A 系列の fence 1 で再発（275回、表示50.5Hz）。keyed／fence・retry と無関係な既知の未解決項目のまま。vsync ではこの見送り自体が発生しない。

## 実装と検証

サブエージェントが実装し、親がレビューした。`fence` は source を `Shared | SharedNTHandle`（keyed mutex なし）で作り、`IDXGIResource1.CreateSharedHandle`（Read）と `ID3D11Device1.OpenSharedResource1` で送信デバイスへ渡す。フェンスは `ID3D11Device5.CreateFence(0, Shared)`／`OpenSharedFence`。合成後に `Signal(fence, imageId)`、送信側は `CopyResource` 前に `Wait(fence, imageId)`（GPU キュー、CPU は待たない）。NT ハンドルは送信側が開いた後に作成側が閉じる。逆方向の危険は従来の CPU lease 契約で防ぐ。`vsync` は合成期限までの待機を `WaitForMultipleObjects({stop, ready})` にし、新しい画像があるときだけ通知を待つ。通知1回に表示1回、合成期限優先、停止優先。純粋な判断部は `VsyncDisplayGate` に分離して管理テストで検証した。

Debug ビルド警告0・エラー0、`--self-test` 49件、解析器 unittest 94件成功。既存の keyed／tick／signal ログは修正後も有効判定。試作 DLL SHA-256 は `77A612443C452C0F96978BB3AD2F78A3FBC3F066DEFD33830BB5F1498B3413B2`。

解析器の修正1件: vsync の待ち timeout は判断時刻で計算し、記録時刻はその直後に採取するため、ms 境界をまたぐと記録上の残りより1大きくなる。B 系列 vsync 2 の起動直後1件がこれに該当し当初無効判定となった。規則を「残り整数ms またはその +1」に緩め、境界テストを追加して再解析した（`analysis-reanalyzed-after-timeout-rule-fix`）。

## 条件

Windows／Debug／RTX 3070、GPU生成パターン、split／both／60Hz、mutex 要求8ms、present-wait 0／fixed、`--copy-retry off`、公式 WinSpoutDXreceiver、monitor index 1（DISPLAY2 1920×1080/60Hz）、DISPLAY1 3840×2160/60Hz。1080p は windowed 12秒（窓2〜10秒）、4K は生成キャンバス 3840×2160・全画面・32秒（窓5〜27秒）。直列実行、全 run の `inputs.json` が一致。

## 結果

### 1080p 確認（fence＋vsync、位相4ms）

合成・表示・Spout とも 60.000Hz。`copy.fence.wait`／`display.fence.wait` 各480、keyed mutex イベント0、skip 0、error 0。通知待ち平均 8.76ms、表示480回。CPU 0.18コア相当。正常終了。

### 4K A: 同期方式（tick、位相0.5ms＝負荷条件、keyed→fence→fence→keyed）

| run | 合成Hz | 表示Hz | Spout Hz | 異なるID | 同一ID再送 | Spout年齢 平均／p99／最大ms | Spout最大間隔ms | copy busy | 表示busy | 表示未準備 | 合成開始遅れ 平均／最大ms | CPU秒 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| keyed 1 | 60.000 | 58.864 | 60.000 | 1198 | 122 | 4.61／39.01／89.20 | 23.21 | 105 | 0 | 25 | 0.078／0.925 | 9.53 |
| fence 1 | 60.000 | 50.500 | 59.955 | 1219 | 100 | 4.43／21.83／23.08 | 34.21 | 0 | 0 | 209 | 0.007／0.358 | 7.38 |
| fence 2 | 60.000 | 60.000 | 60.000 | 1289 | 31 | 1.66／20.51／22.06 | 22.22 | 0 | 0 | 0 | 0.007／0.367 | 7.70 |
| keyed 2 | 60.000 | 59.864 | 60.000 | 1097 | 223 | 5.57／53.16／121.55 | 21.97 | 197 | 3 | 0 | 0.080／0.619 | 8.48 |

- fence の同一ID再送（100／31）は衝突ではなく、位相0.5ms が 4K の合成時間（平均0.32ms、p95 0.66ms）より短く、Spout が前の画像を `retained` で選ぶ回があるため（fence 1 の selection retained 101）。負荷条件の設定上の産物で、通常条件（位相4ms）では0。
- fence 1 の Spout 最大間隔 34.2ms は 1 回の `send.accessMutexBusy`（受信機側が送信アクセス mutex を8ms超保持）に起因し、フェンスとは無関係。
- keyed で合成開始遅れが約0.08ms あるのは、合成側が自分の領域の keyed mutex を取得する時間。fence では不要になり 0.007ms。

### 4K B: 表示駆動（fence、位相4ms、tick→vsync→vsync→tick）

| run | 合成Hz | 表示Hz | Spout Hz | 表示年齢 平均／p99／最大ms | 表示最大間隔ms | 通知待ち 平均／最大ms | 表示未準備 | Spout年齢 平均／最大ms | 合成開始遅れ 平均／最大ms | CPU秒 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| tick 1 | 60.000 | 60.000 | 60.000 | 0.45／0.67／0.97 | 17.19 | — | 2（起動時） | 5.42／10.48 | 0.006／0.233 | 6.45 |
| vsync 1 | 60.000 | 60.000 | 60.000 | 9.53／10.08／12.72 | 19.16 | 8.94／12.12 | 0 | 5.32／9.61 | 0.011／0.626 | 7.27 |
| vsync 2 | 60.000 | 60.000 | 60.000 | 0.47／1.34／5.26 | 21.53 | 0.006／4.62 | 0 | 6.27／26.09 | 0.010／3.555 | 7.80 |
| tick 2 | 60.000 | 60.000 | 60.000 | 0.53／1.45／2.00 | 18.28 | — | 1（起動時） | 5.56／9.79 | 0.006／0.378 | 7.28 |

- vsync の表示年齢は「Present を呼んだ時点」の値で、通知が遅く来る起動では大きくなる。tick は通知を待たずに Present するが、latency waitable が既にシグナル済みでも実際の走査は次の vblank なので、両者の表示遅延の差はこの計測では分からない。
- vsync 2 の合成開始遅れ最大 3.555ms（窓内1件、8.737秒）は、表示完了後の停止待ち（`WaitOne`）からの復帰が約4.8ms遅れたもので、OS のタイマー粒度に依存する。tick でも同じ待ちを使っており、この run だけで起きた。試作は `timeBeginPeriod`／高分解能タイマーを使っていない。
- vsync の CPU はやや高い（7.27／7.80 対 6.45／7.28）が、A 系列の起動間ばらつき（7.4〜9.5）の範囲内。

## 合格条件との対応

- A: 衝突0、年齢最大の短縮、合成60Hz、CPU 同等以下は満たした。同一ID再送は「keyed より減る」を満たすが、上記のとおり負荷条件の設定上の産物。fence 1 の最大間隔悪化は受信機側の mutex 保持による。→ **`fence` を後続の比較基準に採用**。
- B: 表示未準備0、表示 60Hz、合成開始遅れ（1件の外れ値を除く）は満たした。表示年齢「同等以下」は計測点の定義上判定不能、最大間隔はやや悪化。→ **既定 tick を維持し、vsync は候補**。

## 限界

- GPU 生成画像の試作。mpv・CPU アップロード・GPU プレビュー・本体統合は含まない。受信画像の一意性、実受信時刻、物理走査は検証していない。
- 各条件2本ずつの ABBA。表示先は 60Hz のみで、60Hz 以外の表示周期に対する vsync の挙動は未測定。
- フェンス方式の逆方向の安全は CPU lease に依存しており、lease を返す前の GPU 完了確認（EVENT クエリ）は残る。読者の GPU 側待ちを増やさずに CPU 側の完了待ちを減らす設計は次段階。

## 証跡

- 原始ログ: `TestResults/gpu-fence-vsync-20260910/`。集計: `TestResults/gpu-mutex-retry-session-20260910T0752Z/eval-fv-{smoke,A,B}/summary.{json,md}`。

| run | ディレクトリ |
| --- | --- |
| 1080p fence＋vsync | `20260910T095532295Z-split-both-cb6cfbb245b44c27b62c5cb335f0efc8` |
| A keyed 1 | `20260910T095631045Z-split-both-6d095a20154c4b52ac52a1cc1fd8d5db` |
| A fence 1 | `20260910T095721319Z-split-both-36eaa05f3c0346aba76dec86ae2f2c43` |
| A fence 2 | `20260910T095805521Z-split-both-0653ae19dbd74ac8bfa7f9ffd4552789` |
| A keyed 2 | `20260910T095849178Z-split-both-eb6c9ac69c794ee093bcd1db0c6aa6b6` |
| B tick 1 | `20260910T100001374Z-split-both-637dd1018d714ab8a3b876a3c29987b5` |
| B vsync 1 | `20260910T100046669Z-split-both-a2a8e2a38e6641b98000fbc0f383f0e6` |
| B vsync 2 | `20260910T100129259Z-split-both-a3494f09321d4746a0f7a00ce9427742`（再解析 `analysis-reanalyzed-after-timeout-rule-fix`） |
| B tick 2 | `20260910T100442519Z-split-both-18b42ca0cf5848a89ad51deb1947225a` |

再現: `Invoke-Trial.ps1 -SourceSync keyed|fence -DisplayPacing tick|vsync -CopyRetry off -SendPhaseMs 0.5|4 ...`（Windows PowerShell 5.1）。

## 次の設計項目

1. 走査時刻の計測: `IDXGISwapChain::GetFrameStatistics`（PresentCount／SyncRefreshCount／SyncQPCTime）で Present と vblank の対応を記録し、tick／vsync の表示遅延を走査基準で比較する。
2. 60Hz 以外の表示先: DISPLAY1 を 1920×1080@120Hz にして monitor index 0 で tick／vsync を比較（利用者の表示設定変更が必要。主画面のため操作窓の重なりを記録）。
3. タイマー粒度: 試作の待機に高分解能 waitable timer（`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`）または `timeBeginPeriod(1)` を導入し、停止待ちからの復帰遅れを測る。
4. 本体契約への反映: 共有 source は「フェンスで書き終わり順序を保証し、読者同士は排他しない」「読者→書き手は lease で防ぐ」を採用候補とする。keyed mutex＋再取得は代替案として記録に残す。
