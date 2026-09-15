# V11-e: 4K の退避経路がどこまで成立するかを実測で埋める

利用者の指示（2026-09-15）。**V11 は既に合格しているので、これは合否ではなく判断材料を作る作業。**

---

## 1. なぜやるか

**4K は HEVC しか測っていない。** V1 のコーデック行列の時点で H.264 4K が入っていないので、
**software どころか hardware でも未検証**である。

mpv 除去後は `decodeMode=software` が唯一の退避経路になるため、
**「4K で hardware が効かない素材に逃げ道があるか」**が答えられない状態になっている。

埋めたい表:

| 素材 | hardware | software 現状 | software + frame threading |
| --- | --- | --- | --- |
| HEVC 4K60 | 60（実測済み） | **13..24**（実測済み） | **?** |
| **H.264 4K60** | **?** | **?** | **?** |

## 2. 分かっている事実（推測しないための前提）

段階別解析（`TestResults/v11-b` の 4K run）で**律速はデコーダ本体**と確定している。

| 段階（中央値 / p95、ms） | hardware | software |
| --- | --- | --- |
| **デコード→配信 到着間隔** | **16.58 / 17.53** | **44.08 / 64.02** |
| 配信→取得 | 15.41 / 16.69 | 8.05 / 15.02 |
| 合成 start→complete | 0.25 / 0.49 | 0.32 / 0.88 |
| deliveries（窓内） | 2400 | **832** |
| skip | vblank 2 | **compose.sourceNotReady 1569** |

下流は暇で待っている。アップロードや合成の問題ではない。

**そして CPU は 1.447 コアで、24 論理コアに対し飽和していない。**
`gst-inspect-1.0 avdec_h265` で確認したところ:

```
max-threads : Maximum number of worker threads to spawn. (0 = auto)   Default: 0
thread-type : Multithreading methods to use   Default: 0x00000000 "auto"
                (0x1) frame / (0x2) slice
```

**どちらも auto。** gst-libav は低レイテンシ条件で frame threading を避ける挙動があり、
HEVC では slice 並列の効きが弱い。**CPU 1.447 コアはこれと整合する。**

shim は `avdec_*` を profile 表（`tcs_gstreamer.cpp:1206-1210`）から
**明示的に `gst_element_factory_make` している**ので、生成直後にプロパティを設定できる。

## 3. 依頼

### 3-1. H.264 4K60 素材を作る

既存と同じ手順で作る（`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の V1 節）:

> `videotestsrc pattern=ball motion=wavy` ＋ `timeoverlay`、NVENC、GOP 1 秒、60 秒

**解像度だけ 4K（3840×2160）、60fps、H.264。** 名前は `v1_h264_4k60.mp4` に揃える。
他の素材と条件を変えないこと（変えると比較にならない）。

### 3-2. 既定のまま 2 本測る

| run | decodeMode | 素材 |
| --- | --- | --- |
| 1 | hardware（既定） | `v1_h264_4k60.mp4` |
| 2 | `software` | `v1_h264_4k60.mp4` |

50 秒 run、窓 8〜48 秒、これまでと同じ条件。

### 3-3. frame threading を環境変数で足す（**既定は変えない**）

shim の CPU profile で `avdec_*` を生成した直後に、**環境変数が設定されているときだけ**
`thread-type` と `max-threads` を設定する。

- 環境変数名は `TCS_DECODE_THREAD_TYPE`（`frame` / `slice` / `auto`）と
  `TCS_DECODE_MAX_THREADS`（整数）を提案する。名前は変えてよい
- **既定（未設定）では現行と完全に同一**。プロパティを触らない
- **要素に当該プロパティが無い場合は黙って飛ばす**（`g_object_class_find_property` で確認してから設定）
- 設定したときはログに残す

`TCS_SEEK_LATENCY_COMPENSATION` と同じ「測定用の切替」の扱い。

### 3-4. frame threading ありで 2 本測る

| run | decodeMode | 素材 | 環境変数 |
| --- | --- | --- | --- |
| 3 | `software` | `v1_h265_4k60.mp4` | `thread-type=frame` |
| 4 | `software` | `v1_h264_4k60.mp4` | `thread-type=frame` |

### 3-5. 副作用の確認（1 本）

frame threading は**出力が数フレーム遅れる**。いま通っている 1080p が壊れないかを見る。

| run | decodeMode | 素材 | 環境変数 |
| --- | --- | --- | --- |
| 5 | `software` | `v1_h265_1080p60.mp4` | `thread-type=frame` |

**dist/s が 60 を保つか**と、**`compose.acquire` の遅れが増えていないか**を見る。

## 4. 報告してほしいもの

1. **6 マスの表**（dist/s、cpu、minPr、spout、cp99、err、exit）
2. **CPU の使用コア数**。frame threading で増えたか。増えていなければ効いていない
3. **デコード→配信の到着間隔**（段階別解析と同じ指標）。ここが 16.67ms に近づけば成功
4. 3-5 の副作用の有無
5. `max-threads` も動かした方が良さそうなら、その所見

## 5. 守ること

1. **既定の挙動を変えないこと。** 環境変数が未設定なら現行と完全に同一
2. 合否判定は書かないこと（判定は親が出す）
3. 実機は 1 本ずつ。自分の起動 PID のみ終了
4. `main` へ書き込まない
5. shim を変更したら**自分の作業ツリーで建て直す**こと。他のツリーの DLL を流用しない
6. 素材の生成条件を他の素材と変えないこと
