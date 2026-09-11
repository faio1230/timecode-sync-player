# 走査時刻の計測結果（2026-09-10）

## 判断

[仕様](GPU-SCANOUT-STATS-PROBE-SPEC.md) に従い DXGI フレーム統計（`GetFrameStatistics`／`GetLastPresentCount`）を試作へ追加し、全画面では毎フレームの走査時刻（`SyncQPCTime`）を Present と1対1で対応付けられた（4K 4run とも 1320件、対応付けエラー0、連続 refresh 差はすべて1＝表示落ち・重複なし）。windowed（DWM 合成）では統計が約0.5秒ごとにしか更新されず、走査比較には使えない。

走査時刻で比較すると、**表示通知の直後に Present する現在の vsync 方式は、tick より約1フレーム遅い**。

| 4K run（fence・位相4ms） | 生成→走査 平均／p99 ms | Present開始→走査 平均 ms | 通知待ち 平均 ms |
| --- | ---: | ---: | ---: |
| tick 1 | 5.77／6.21 | 5.38 | — |
| vsync 1 | 29.77／30.22 | 16.33 | 12.97 |
| vsync 2 | 25.50／25.98 | 16.34 | 8.59 |
| tick 2 | 7.95／8.39 | 7.47 | — |

- latency waitable object（最大 latency 1）は、前の画像が走査に入った直後（vblank の直後）にシグナルされる。その直後に Present すると次の vblank まで丸ごと待つため、Present開始→走査が常に約16.3ms になる。
- tick は合成直後（vblank に対する位相は起動ごとに異なる）に Present するため、Present開始→走査は今回 5.4／7.5ms だった。これは起動依存で、0〜16.7ms のどこにでもなりうる。
- 生成→走査は tick が 5.8／7.9ms、vsync が 29.8／25.5ms。vsync では通知を待つ間（9〜13ms）に合成済み画像が古くなり、さらに1フレーム分の Present 待ちが加わる。
- 全 run 正常終了・解析有効・error 0・プロセス残存なし。`scanoutPending` は終了時 3〜4件（最後の数フレームが統計に現れる前に終了するため）。

**結論: 「表示は自分の時計（vblank）に従う」という方針自体は維持するが、通知直後に表示するのではなく、通知を vblank の位相基準として使い、次の vblank の直前（余裕 margin）で最新画像を選んで Present する方式にすべき。** これなら表示遅延は起動位相に依存せず margin 程度に収まり、合成・Spout の60Hzは変えない。既定は引き続き tick。現在の `vsync` 方式は候補から外し、記録として残す。

## 実装と検証

`ScanoutTracker`（presentCount→画像の有界表、容量16）、`present.return.value`=presentCount、`present.scanout`（value=SyncRefreshCount、deadlineQpc=SyncQPCTime、detail=`PresentCount:PresentRefreshCount[:unmapped]`）、`present.stats.disjoint`（1回）、summary の `scanoutPending`／`scanoutDisjoint`、解析器 `scanout` セクション。Debug ビルド警告0・エラー0、`--self-test` 53件、解析器 100件成功。試作 DLL SHA-256 `07B220DE727A938FD5410B5FD929242F65CC884AA7F7C8AEC4EED5C3B610D860`。`SyncQPCTime` と `Stopwatch.GetTimestamp` は同じ QPC で、統計の観測遅れ（観測時刻−SyncQPCTime）は tick で平均約11ms（次の合成 tick で観測するため）。

## 証跡

`TestResults/gpu-scanout-20260910/`（1080p windowed 1、4K tick→vsync→vsync→tick）。集計 `TestResults/gpu-mutex-retry-session-20260910T0752Z/eval-scanout/`。

| run | ディレクトリ |
| --- | --- |
| 1080p windowed vsync | `20260910T105703357Z-split-both-8dc84f5b9ae243debf2c0ab9af41df10` |
| tick 1 | `20260910T105735168Z-split-both-886f9378ad38442c88af159f92ad75f4` |
| vsync 1 | `20260910T105831691Z-split-both-e53bab82f4154d3d8ece24242496a226` |
| vsync 2 | `20260910T105912870Z-split-both-0921e7e0491f4eab8dcf8e3d3ad93f3d` |
| tick 2 | `20260910T110002367Z-split-both-b03d537db65e4512be873c30e2f55191` |

## 次の実証

`--display-pacing vblank`（仮称）: 通知で vblank 位相を得た後、`SyncQPCTime` と refresh 周期から次の vblank を推定し、その `margin` 前に最新画像を選んで描画→Present する。margin の根拠は描画＋Present の実測（p99 約0.4ms）と OS 起床遅れ（高分解能タイマーなしで最大約3.5〜4.8ms を観測）。起床遅れを抑えるため、この待ちには `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` を使い、起床遅れ自体も記録する。評価は生成→走査の平均・p99・最大、Present開始→走査、連続 refresh 差、合成60Hz維持、CPU。60Hz で tick と比較したあと、120Hz 表示先で比較する。
