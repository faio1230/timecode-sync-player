# 合成位相の vblank 整列の実証結果（2026-09-10〜11）

## 判断

[仕様](GPU-COMPOSE-ALIGN-PROBE-SPEC.md) の `--compose-align vblank` を実装し、途中で2つの改善（Loop の待ちを高分解能タイマー化、lead を 1.5ms→3ms）を加えて、4K・fence・vblank・60Hz 表示先で比較した。最終条件（DLL `BBA96618B82962650C3E6F8A92500CA6B1C2EDA11B119F26A40698186E58666E`、margin 3ms、lead 3ms）:

- **総遅延（生成→走査）が起動位相に依存せず約 5.6〜5.7ms で一致し、表示落ち・画像飛びが 0 になった。** 整列なし（同一バイナリ）は 13.5ms、前段では 4.6〜11.5ms と起動依存だった。
- 合成の位相誤差は解析窓で p99 0.003ms 以下。合成周期は主表示の実周期に追従した（差 1µs 未満）。Spout は合成に対する位相 4ms を維持。
- 高分解能タイマーを Loop の待ちにも使った結果、合成開始遅れの最大が 3〜4ms → 0.9〜1.3ms、アプリ CPU が 7〜12秒 → 2.4〜4.9秒に下がった（空回りの Yield 消滅）。
- lead 1.5ms では 4K で 22秒あたり 2〜3件の画像飛びが残った。合成の起床遅れ（最大約1ms）と 4K の合成時間（最大約0.9ms）の合計が lead を超える回があるため。lead 3ms（実測に基づく）で 0 件。
- 後続の比較基準を **fence＋vblank（margin 3ms）＋align（lead 3ms）** とする。試作 CLI の既定（align off、lead 1.5）は変えない。
- 設計上の合意が必要な点: 整列中は合成・Spout の周期が主表示の実周期に追従する。設計文書の「Spout を表示周期へ暗黙に従属させない」は、ここでは「周期の基準を主表示に置く」と読み替える必要がある。複数画面では1面にしか整列できない。

## 経過

| 回 | 条件 | 4K 生成→走査 平均 ms | 画像飛び／22秒 | 備考 |
| --- | --- | ---: | ---: | --- |
| 第1回（`gpu-align-20260910/`） | lead 1.5、Loop 待ちは従来 | off 4.65／11.51、align 4.37／4.40 | align 2／1 | 合成待ちの起床遅れ 2〜4ms が原因（トレース済み） |
| 第2回（`gpu-align2-20260910/`） | lead 1.5、Loop 待ちを高分解能タイマー化 | align 4.12／4.13、off 13.50 | align 3／2 | 合成開始遅れ最大 0.97ms、CPU 大幅減。lead 不足が残る |
| 第3回（`gpu-align3-20260910/`） | lead 3.0 | align 5.70／5.60 | 0／0 | 合格 |

## 最終条件の結果（`gpu-align3-20260910/`）

| run | 表示Hz | 生成→走査 平均／p99／最大 ms | Present→走査 平均 ms | refresh 連続差≠1 | 画像飛び | 位相誤差 p99 ms | 合成開始遅れ 平均／最大 ms | Spout Hz／異なるID | Spout 送信アクセス busy | CPU秒 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1080p 全画面（12秒） | 60.000 | 5.20／5.57／5.63 | 1.97 | 0 | 0 | 0.001 | 0.31／1.29 | 60.000／480 | 0 | 2.44 |
| 4K align 1 | 60.000 | 5.70／5.96／5.98 | 2.47 | 0 | 0 | 0.003 | 0.30／0.87 | 59.364／1306 | 18 | 4.61 |
| 4K align 2 | 60.000 | 5.60／6.19／6.29 | 2.37 | 0 | 0 | 0.001 | 0.30／1.35 | 59.909／1318 | 2 | 3.06 |

- 4K align 1 の Spout 59.36Hz は、受信機側が送信アクセス mutex を 8ms 超保持した回（18件）による再送で、整列とは無関係の受信機側事象。本日の他 run では 0〜2件で、発生数は run ごとに変わる。
- 全 run 正常終了・解析有効・error 0・プロセス残存なし。実行時に別 worktree のビルド等の重い並行処理がないことを確認した（CPU 負荷 15%）。

## 実装

- `ScheduleOffset`（Interlocked）を GPU／Spout 両 worker の `TickSchedule` が共有し、`DueQpc` で標本化した値を `Take` でも使う（読み取り途中の変更で「未到達」例外が出ない）。予定時刻は単調増加を保証し、起点を大きく戻す場合は tick を飛ばす。
- `ComposeAlignGate`: 次の `vblank − margin − lead` と次の合成予定の位相誤差を ±半周期に wrap し、±0.5ms に clamp した補正を `present.scanout` 観測ごとに1回適用。補正量は µs 単位で `compose.align` に記録し、解析器が全 tick の予定時刻を再構成して検証する。
- Loop の待ちは `VblankWaitTimer`（`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`）で期限まで待ち、残り 50µs 以下だけ Yield。manifest に `loopTimerHighResolution`。
- 管理テスト: 試作 72件、解析器 114件。

## 限界

- 60Hz 表示先のみ。120Hz、複数画面、表示周期と 60Hz の差が大きい場合（59.94Hz 等）の追従は未測定。追従は slew 0.5ms/tick の範囲内で成立する見込みだが実測していない。
- margin 3ms・lead 3ms は実測に基づく試作値。合計約 6ms の総遅延は、合成 0.9ms・起床遅れ 1ms・描画＋Present 0.4ms の余裕を含む。
- GPU 生成画像。mpv・CPU アップロード・本体統合は含まない。

## 証跡

`TestResults/gpu-align{,2,3}-20260910/`、集計 `TestResults/gpu-mutex-retry-session-20260910T0752Z/eval-align{,2,3}/`、時系列は同 `environment.md`。

| run | ディレクトリ |
| --- | --- |
| 第3回 1080p | `gpu-align3-20260910/20260910T183817190Z-split-both-a48b86dc5ea34b96a99a658e5e748f40` |
| 第3回 4K align 1 | `gpu-align3-20260910/20260910T183838364Z-split-both-6d2d853cd7194249a79846bf0c9fc09c` |
| 第3回 4K align 2 | `gpu-align3-20260910/20260910T183936782Z-split-both-0cf01f072b794b6b84c2eb869d3bcd8b` |
| 第2回 4K off（対照） | `gpu-align2-20260910/20260910T144722016Z-split-both-f167d4f591bb4e358e6f755dac235c4a` |

再現: `Invoke-Trial.ps1 -DisplayPacing vblank -SourceSync fence -ComposeAlign vblank -ComposeLeadMs 3 -CopyRetry off -SendPhaseMs 4 ...`。

## 次の設計項目

1. 出力側の実証はここで一区切り。素材側（CPU 画像アップロードの上限、`hwdec=d3d11va-copy`、HAP→BC テクスチャ）へ移る。別 worktree で進行中の GStreamer 置き換えとは、ソース側の契約（[契約の突き合わせ](OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md)）で整合させる。
2. 120Hz 表示先と複数画面での整列・非整列画面の挙動。
3. 本体契約: 合成の周期基準は主表示（存在すれば）、Spout は合成に対する位相で従う。表示がない構成では自由走行 60Hz。
