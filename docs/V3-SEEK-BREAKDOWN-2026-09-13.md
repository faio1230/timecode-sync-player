# V3 シーク時系列の計測（2026-09-13、GStreamer×Gpu / mpv×Gpu）

計測のみ。**合否判定は含まない。** 数値はすべて QPC（同じ時計）。

- 測定ビルド: `a69ab71`（TimecodeSyncPlayer.exe SHA256 `0B427757…CBBE7AD`、tcs_gstreamer.dll `D40E11D5…ACA043F`）
- 生データ: `TestResults/v3-seek-20260913/`（git 管理外）。集計は同 `analysis/seek-breakdown-{gst,mpv}.md` と `seek-rows-*.csv`。
- 実機: 2026-09-13 12:42〜12:57 JST、1 プロセスずつ。全 run 成功（アプリ残留 0）。

## 1. 計測方法

| 項目 | 内容 |
| --- | --- |
| 標準ハーネス | `SyncAccuracyE2ETests`（VB-CABLE、6 フェーズ×約 90 秒）を gst 6 run / mpv 6 run |
| 再同期ハーネス | `SyncSeekResyncE2ETests`（計測専用・新規）。LTC 連続送出のまま SeekBar で -0.5 秒ずらし、SyncDecisionEngine に戻させる。gst 1 run / mpv 2 run |
| バックエンド | run ディレクトリの `settings.json` に `{"backend":0\|1,"outputBackend":1}` |
| トレース | `TIMECODE_ACCURACY_REPORT_DIR` と `TIMECODE_SYNC_PLAYER_OUTPUT_TRACE` を同一 run ディレクトリに指定 |
| 追加イベント（計測専用） | `seek.decide` / `seek.issue` / `seek.return` / `load.issue` / `load.return` / `player.seeking`。トレース無効時は何もしない |
| 既存イベント | `gst.delivery` / `mpv.frame` / `compose.publish` / `present.scanout` |

区間の同定:

- a = `seek.decide`（同値 decide の先頭。issue 直前の decide も併記）
- b = `seek.issue` / `load.issue`、b' = `seek.return` / `load.return`
- c = 新位置の最初のフレーム。gst は **pts が目標 −0.2 秒以上**の最初の `gst.delivery`、mpv は **position が目標 −0.15 秒以上**の最初の `mpv.frame`。preroll（pts=0）を拾わない。**次の操作（seek/load）までに無ければ「到着なし」**
- d = その c 以降の最初の `compose.publish`。e = `present.scanout` は本ハーネスで **0 件**（全画面表示を開かないため）

## 2. 標準ハーネスで実際に起きた操作（1 run あたり）

| イベント | gst（6 run 各） | mpv（6 run） |
| --- | --- | --- |
| `seek.decide` | **0**（6 run 計 0） | 1〜5（計 12。decide と一致した issue は計 8） |
| `seek.issue` | 4（すべてフリーズ・ギャップの最終フレームへ） | 5〜6 |
| `load.issue`（トラック切替） | 10 | 10 |
| `gst.delivery` / `mpv.frame` | 3175〜3182 | 3161〜3175 |
| `player.seeking` | 46（すべて `raw=<empty>` / true） | `no`/false 45〜46、`yes`/true 8〜9、`<empty>`/true 0〜5 |

シーク位相（seek-a/b/c/back）で発生した操作は **すべて load（トラック切替）で、seek.issue は 0**。
フェーズ跨ぎの LTC は Jump 判定（±100ms 超）で同期対象外になり、Continue モードのギャップ/トラック切替が処理していた。

## 3. 区間ごとの差（ms、中央値 / p95 (n)）

### GStreamer×Gpu（6 run + resync 1 run、操作 110 件）

| phase | kind | n | a→b | b→b' | b'→c | b→c | c→d |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| black-sweep | load | 12 | - | 9.1 / 15.6 | -1.1 / -0.9 | 7.9 / 13.6 | 9.9 / 15.7 |
| freeze-sweep | seek | 18 | - | 0.1 / 0.2 | **到着なし (n=0)** | **到着なし (n=0)** | - |
| freeze-sweep | load | 18 | - | 8.7 / 38.9 | -0.9 / -0.9 | 7.8 / 38.0 | 11.4 / 16.3 |
| seek-a | load | 6 | - | 8.9 / 10.3 | 82.9 / 83.7 | 91.7 / 93.6 | 8.8 / 14.8 |
| seek-b | load | 6 | - | 7.6 / 10.4 | 111.8 / 112.7 | 119.4 / 123.1 | 5.8 / 13.9 |
| seek-c | load | 6 | - | 8.6 / 14.0 | 251.5 / 257.0 | 259.7 / 265.9 | 8.8 / 16.7 |
| seek-back | load | 6 | - | 8.3 / 12.3 | 82.3 / 83.9 | 91.5 / 96.2 | 4.6 / 12.3 |
| resync | seek | 23 | **なし（decide=0）** | 0.1 / 0.2 | 180.2 / 243.2 (n=20) | 180.3 / 243.3 (n=20) | 15.8 / 35.1 (n=20) |

- freeze-sweep の seek 18 件は `seek.issue`→`seek.return` が 0.1〜0.2ms で戻る一方、**次の操作（load）まで pts 一致の配信が 1 件も無い**。次の操作は中央値 1981.7ms 後（12 件、min 1977 / max 2030）。
- resync の 23 件はエンジンが決めた seek ではなく **手動ずらしの SeekTo**（decide=0）。shim の seek 呼び出し→新位置フレーム到着は中央値 180ms。

### mpv×Gpu（6 run + resync 2 run、操作 171 件）

| phase | kind | n | a→b | b→b' | b'→c | b→c | c→d |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| black-sweep | seek | 7 | 1.0 / 1.1 | 0.0 / 0.0 | 220.3 / 232.0 | 220.3 / 232.0 | 10.8 / 12.2 |
| black-sweep | load | 12 | - | 0.0 / 0.1 | 129.5 / 172.3 | 129.5 / 172.3 | 5.9 / 16.1 |
| freeze-sweep | seek | 18 | - | 0.0 / 0.1 | 13.5 / 430.9 | 13.5 / 430.9 | 7.5 / 14.9 |
| freeze-sweep | load | 18 | - | 0.0 / 0.1 | 136.0 / 164.3 | 136.0 / 164.3 | 9.2 / 16.5 |
| seek-a | load | 6 | - | 0.0 / 0.1 | 108.2 / 226.8 | 108.2 / 226.8 | 6.0 / 9.4 |
| seek-b | seek | 1 | 327.7 (n=1) | 0.0 / 0.0 | 26.4 | 26.4 | 15.3 |
| seek-b | load | 6 | - | 0.0 / 0.0 | 164.2 / 217.5 | 164.2 / 217.6 | 6.3 / 16.3 |
| seek-c | load | 6 | - | 0.0 / 0.2 | 111.9 / 166.8 | 112.0 / 166.9 | 6.3 / 15.0 |
| seek-back | load | 6 | - | 0.0 / 0.1 | 166.0 / 229.2 | 166.1 / 229.3 | 6.2 / 15.1 |
| resync | seek | 72 | 0.0 / 0.9 (n=26) | 0.0 / 0.0 | 19.0 / 76.9 | 19.0 / 76.9 | 9.3 / 16.5 |

- 再同期 run の seek 72 件のうち **26 件がエンジン決定**（resync01: 14、resync02: 12）。エンジン決定の内訳は a→b ≈ 0ms、b→b' ≈ 0ms、b'→c 中央値 19ms、c→d 中央値 9ms。
- 標準 run でも mpv はエンジン決定が計 8 件（black-sweep のギャップ離脱 7、seek-b 1）。

## 4. IsNativeSeeking と `seeking` 生値の実測

`MainWindow.IsNativeSeeking()` は `_mpvApi.GetPropertyString(_mpv, "seeking") != "no"` を返す。計測イベント `player.seeking` は戻り値（value=1/0）と生値（detail）を変化時+2 秒ごとに残す。

| run | raw | result | 件数 |
| --- | --- | --- | ---: |
| gst-accuracy01〜06 | `<empty>` | true | 各 46 |
| gst-resync01 | `<empty>` | true | 15 |
| mpv-accuracy01 | `no` / `yes` / `<empty>` | false / true / true | 45 / 8 / 1 |
| mpv-accuracy02 | `no` / `yes` / `<empty>` | false / true / true | 46 / 9 / 1 |
| mpv-accuracy03 | `no` / `yes` / `<empty>` | false / true / true | 46 / 8 / 3 |
| mpv-accuracy04 | `no` / `yes` | false / true | 45 / 8 |
| mpv-accuracy05 | `no` / `yes` / `<empty>` | false / true / true | 45 / 8 / 5 |
| mpv-accuracy06 | `no` / `yes` / `<empty>` | false / true / true | 46 / 8 / 2 |
| mpv-resync01 | `no` / `yes` | false / true | 33 / 29 |
| mpv-resync02 | `no` / `yes` | false / true | 35 / 31 |

アプリログ（`logs/timecodesyncplayer-20260913.log`）にも変化点が出る（run あたり 1 行の gst と、seek ごとに yes/no が変わる mpv）:

```
2026-09-13 12:42:26.288 [INF] player.seeking raw=<empty> isNativeSeeking=true     ← GStreamer run 開始時。以降変化なし
```

- GStreamer: 全 run・全サンプルで `raw=<empty>`、戻り値 true（変化なし）。
- mpv: 再生中は `raw=no` → false、シーク中は `raw=yes` → true が観測された。`<empty>` は run あたり 0〜5 件。

## 5. 代表行（詳細表は `analysis/seek-breakdown-{gst,mpv}.md`）

gst-accuracy01（phase 開始からの ms）:

| kind | phase | target s | a | b issue | b' return | c frame | d publish | 差 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| load | seek-b | 3.080 | - | 211.4 | 218.2 | 329.7 (pts 3.080) | 343.6 | b→b' 6.8 / b'→c 111.5 / c→d 13.9 |
| seek | freeze-sweep | 9.958 | - | 10188.4 | 10188.5 | **到着なし** | - | b→b' 0.1、次の load は 2006.8ms 後 |
| load | black-sweep | 0.000 | - | 12195.2 | 12203.8 | 12202.6 (pts 0) | 12211.9 | b→b' 8.7 / b→c 7.4 / c→d 9.3 |

mpv-resync01 のエンジン決定 seek（resync-23、target 7.36s）:

| kind | a decide | b issue | b' return | c frame | d publish | 差 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| seek | 62.832 | 62.866 | 62.881 | 70.444 | 74.025 | a→b 0.034 / b→b' 0.015 / b'→c 7.563 / c→d 3.581 |

## 6. 設計差異・未計測

- b の境界は managed の player API 呼び出し（`PlaybackOperationsCoordinator`）。GStreamer では `GstMpvApiAdapter.CommandString` → `tcs_player_seek` を同期で通る。shim 内の seek 実装（`seek_locked`）自体の内訳は別計測しないと分からない。
- c の同定は pts / position による。世代番号は `gst.delivery` に無いため、load 直後に旧世代の配信が混ざる可能性は原理的に残る（今回のデータでは seq リセットで判別できた）。
- `present.scanout`（e）は全画面表示を開かないため 0 件。e が必要ならフルスクリーン表示付きの run が別途必要。
- A1（GPU 経路の画素マーカー読み戻し）は main 未マージのため、精度 CSV（`accuracy-samples.csv`）は更新していない。本表は画素ではなくイベント時系列。
- 3 の (a) 黒ギャップ中の PauseForGap と (b) シーク方式（FLUSH|ACCURATE 対 KEY_UNIT|SNAP_BEFORE）の切替比較は未実施。(b) は shim の変更が必要で、`/code-review ultra native/gst-shim/` 待ちの制約に該当する。

## 7. run 一覧

| ディレクトリ | 内容 |
| --- | --- |
| `gst-accuracy01`〜`06` | GStreamer × Gpu、標準 6 フェーズ、各 113 秒 |
| `mpv-accuracy01`〜`06` | mpv × Gpu、標準 6 フェーズ、各 112〜115 秒 |
| `gst-resync01` | GStreamer × Gpu、再同期 26 サイクル（50 秒） |
| `mpv-resync01` / `02` | mpv × Gpu、再同期 26 サイクル（各 50 秒） |
| `pilot/` | 追加計測前の予備 run（解析から除外） |
