# V11-h: CPU デコード経路の色変換を GPU へ移す（4K 10bit software が 24fps しか出ない件）

基点 `codex/v04-packaging-20260915` の `b533d63`。そこから新ブランチ `codex/v11h-cpu-gpu-colorconvert-20260915` を切る。
**worktree は `C:\Users\<user>\Documents\timecode-sync-player-wt-pkg-20260915`（cwd 固定）。**

不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13）。**触れる変更は実装側で決めず、必ず質問すること。**
実機の前に一声かけること。**合否判定は書かないこと。**

---

## 1. 何が起きていたか（gst-launch で再現して確定済み）

V11-b で software の HEVC 4K60 が 13〜24fps（CPU 1.45 コア）しか出なかった。
**原因は appsink の max-buffers でもデコーダでもなく、CPU の色変換（10bit → BGRA）である。**

1. 素材 `v1_h265_4k60.mp4` は **Main 10**。`avdec_h265` は `I420_10LE` を出す
2. shim の CPU 経路は `avdec_* ! videoconvert ! d3d11upload ! capsfilter(D3D11Memory,BGRA) ! appsink`。
   `d3d11upload` は形式を変えないので、**BGRA への変換は `videoconvert` が CPU で行う**
3. `videoconvert` の `n-threads` は既定 1。**10bit → BGRA には速い経路が無く、4K で 1 フレーム 38ms**
   （8bit I420 → BGRA なら 3.7ms）。**上限は 26fps**
4. 以前の gst-launch 計測（187fps）は出口が `fakesink` で caps の指定が無く、**`videoconvert` が素通ししていた**

### 再現表（`v1_h265_4k60.mp4`、600 フレーム、gst-launch 1.28.2、全て `fakesink sync=false`）

| デコーダの後ろ | fps | CPU コア |
| --- | ---: | ---: |
| なし（デコードのみ） | 187.2 | 3.85 |
| `videoconvert ! d3d11upload`（caps 無し） | 180.5 | 3.83 |
| **`videoconvert ! d3d11upload ! D3D11Memory,BGRA`（現行 shim と同じ）** | **23.2** | **1.20** |
| `videoconvert ! video/x-raw,format=BGRA`（アップロード無し） | 24.6 | 1.23 |
| `videoconvert n-threads=0 ! d3d11upload ! D3D11Memory,BGRA` | 70.6 | 5.52 |
| **`videoconvert ! d3d11upload ! d3d11colorconvert ! D3D11Memory,BGRA`（本依頼の形）** | **123.6** | **2.75** |

中身が同じでビット深度だけ違う 4K HEVC を現行の形に通すと、8bit は 124fps、10bit は 22.8fps。

**本依頼の形では `videoconvert` が素通しになる**ことを `-v` で確認済み。

### 否定された説（この方向に進まないこと）

- **「appsink max-buffers=4 で先行できず frame threading が効かない」は誤り。**
  詰まりの無い `sync=false` でも 23fps が再現する。デコーダは既定で既に 3.85 コア使っている
- **「律速はデコーダ本体」も誤り。** 段階別解析の「デコード→配信 到着間隔」は `gst.delivery`
  （`on_new_sample` = appsink 到着）の間隔で、**変換を含む**
- `TCS_DECODE_THREAD_TYPE` / `TCS_APPSINK_MAX_BUFFERS` を動かしても 4K software の fps は上がらない見込み。
  **本依頼ではこの 2 つに触らないこと**（既定のまま）

---

## 2. 依頼: CPU 経路のアップロードの後ろに `d3d11colorconvert` を入れる

| 経路 | 現行 | 変更後 |
| --- | --- | --- |
| CPU profile（`h264-cpu`〜`prores-cpu`） | `parse ! avdec ! videoconvert ! d3d11upload ! caps ! appsink` | `parse ! avdec ! videoconvert ! d3d11upload ! d3d11colorconvert ! caps ! appsink` |
| `decodebin(sysmem)` 退避 | `videoconvert ! d3d11upload ! caps ! appsink` | `videoconvert ! d3d11upload ! d3d11colorconvert ! caps ! appsink` |
| GPU profile（`*-gpu`） | `parse ! d3d11*dec ! d3d11colorconvert ! caps ! appsink` | **変えない** |

- **`videoconvert` は残す。** `d3d11upload` が受けられない形式が来たときの安全網。受けられる形式では素通しになる
- 出口の caps と appsink の設定は**変えない**

### コード上の罠（必ず読むこと。行番号は `b533d63`）

1. **`g_profiles[].conv` を `d3d11colorconvert` に書き換えてはいけない。**
   CPU と GPU の見分けが `strstr (prof->conv, "d3d11")`（**1279 行、親が確認済み**）なので、
   書き換えると **CPU profile が GPU 扱い**になり `d3d11upload` が作られない。
   `decodeMode=software` の探索順（`software_flags`）も壊れる。
   **アップロード後の変換器は別のフィールド（例 `p->vgpuconvert`）として足すこと**
2. 新しい要素は既存の `p->vupload` と同じ扱いを**全箇所**で受けること:
   - `give_device_context`（1366 / 1408 付近）。**shim のデバイスで動かす**
   - `chain[]` の連結順（1411）: `vconvert → vupload → vgpuconvert → vcaps → appsink`
   - **退避経路（1352〜1375）は別のコード**。`gst_bin_add_many` / `gst_element_link_many` にも足す
   - teardown での `nullptr` 化（1886 付近）
   - load 失敗時の診断ログの配列（2206 の `els[]` / `nms[]`。要素数 5 → 6）
3. I13: チェーン構築は既存と同じ場所・同じロック条件。**`frame_lock` / `state_mutex` の保持範囲を変えない**

---

## 3. 確認すること（判定は親が出す）

### 3-1. 実機の前（自分で回してよい）

1. 非E2E を全て通す（件数を報告）
2. `python scripts/check-shim-lock-rule.py` が PASS
3. **CPU/GPU の分類が変わっていないこと**: `software_flags` が profile 4〜8 だけ 1 のままであることをテストに固定する。
   構造上テストにできない場合は理由と代わりの確認方法を報告する
4. **組んだチェーンをログに出す**: load 成功時に実際の要素名の並びを 1 行出す。
   hardware の run で**この行が変更前と同じ**（`d3d11colorconvert` が 1 個だけ）なら GPU 経路を変えていない証拠になる
5. **色が変わっていないこと**: `v1_h265_4k60.mp4` の同じフレーム（例 120 枚目）を 3 経路で PNG に落とし、
   チャンネルごとの平均と最大の絶対差を報告する:
   - (a) 現行 CPU: `avdec_h265 ! videoconvert ! video/x-raw,format=BGRA`
   - (b) 変更後 CPU: `avdec_h265 ! videoconvert ! d3d11upload ! d3d11colorconvert ! BGRA ! d3d11download`
   - (c) hardware: `d3d11h265dec ! d3d11colorconvert ! BGRA ! d3d11download`

   (b) と (c) は同じ変換器なので近いはず。(a) との差は記録するだけでよい

### 3-2. 実機（1 本ずつ。自分の PID のみ終了。開始前に時刻を報告）

**Release の shim を自分の作業ツリーで建て直し、SHA256 を記録すること。他のツリーの DLL を流用しない。**
手順は `docs/V11-DECODE-MODE-VERIFICATION.md` と同じ（50 秒 run、窓 8〜48 秒、ProRes は 8〜28 秒、
GStreamer×Gpu、Spout ON、DISPLAY2 全画面）。
素材は `C:\Users\<user>\Documents\timecode-sync-player-wt-integrate-20260912\artifacts\media\v1`。

| # | decodeMode | 素材 | 見るもの |
| --- | --- | --- | --- |
| 1 | software | **HEVC 4K60** | **dist/s**、CPU コア、minPr、spout、合成 p99、err、exit |
| 2 | software | V1 の残り 10 素材 | 同上。**1080p が素材の fps を保つか**（非回帰） |
| 3 | hardware | HEVC 4K60 | 同上＋チェーンのログ行（GPU 経路の対照） |
| 4 | hardware | H.264 1080p60 AAC | 同上 |

1 の 4K run では V11-e と同じ**段階別解析**も出すこと。

| 段階（中央値 / p95、ms） | 変更前 software | 変更後 | hardware |
| --- | --- | --- | --- |
| デコード→配信 到着間隔 | 44.08 / 64.02 | ? | 16.58 / 17.53 |
| 配信→取得 | 8.05 / 15.02 | ? | 15.41 / 16.69 |
| 合成 start→complete | 0.32 / 0.88 | ? | 0.25 / 0.49 |
| deliveries（窓内） | 832 | ? | 2400 |
| skip | compose.sourceNotReady 1569 | ? | vblank 2 |

**「デコード→配信 到着間隔」が 16.67ms に近づき `compose.sourceNotReady` が消えれば、原因の除去を実機で確認できたことになる。**

---

## 4. 結果を読むときの注意

- **V1 の HEVC 素材は 3 本とも Main 10**（`v1_h265_1080p60.mp4` も `yuv420p10le`）。
  V1 表の「HEVC 8bit」の行は実際には 8bit を測っていない。**本依頼では素材を作り直さない**（別件）
- H.264 4K60 は 8bit なら現行の形でも変換が軽い（1 フレーム 3.7ms）。
  「H.264 は出るが HEVC は出ない」を**コーデックの差と読まないこと**。差はビット深度

## 5. 守ること

1. **GPU profile のチェーンを変えない**（V11-a の非回帰が崩れる）
2. `TCS_DECODE_THREAD_TYPE` / `TCS_APPSINK_MAX_BUFFERS` とその既定値に触らない
3. 不変条件（I1〜I13）に触れる変更が必要になったら、実装せず「選択肢＋根拠＋計測値」で質問する
4. `main` へ書き込まない。統合は親が行う
5. **合否判定は書かないこと**

## 提出時に書いてほしいこと

- コミット SHA、変更ファイル
- 非E2E の件数と `check-shim-lock-rule.py` の結果
- 3-1 の色差の数値と、チェーンのログ行（software / hardware 各 1 例）
- 3-2 の表と段階別解析
- 設計差異、未検証の項目
