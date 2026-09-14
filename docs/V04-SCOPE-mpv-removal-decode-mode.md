# v0.4 再スコープ: mpv の除去と、デコード方式の切替オプション

状態: 2026-09-13、親が作成。利用者の指示（同日）に基づく v0.4 の範囲変更。
前提文書: `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md`、`docs/OUTPUT-GPU-INVARIANTS.md`。

## 出力側の CPU 合成も v0.4 で落とす（2026-09-14、利用者の決定）

**`outputBackend=Cpu`（CPU 合成経路）は mpv と一緒に除去する。** v0.4 は **GStreamer × GPU 合成の一本**になる。

理由と影響:

- CPU 合成は 1 フレームごとに GPU から同期読み戻しをしている
  （`tcs_player_leased_cpu_copy`: `frame_lock` 保持中に staging へコピー → `Flush` →
  `Map(D3D11_MAP_READ)` → 1080p で 8.3MB の `memcpy`）。60fps 素材では毎秒約 500MB の
  読み戻しと 60 回の強制同期点が UI スレッドに乗り、**公開が約 320ms 止まる**ことを実測している。
  コードのコメント自身が「暫定プレビュー通路」と書いている
- **この決定でその残課題は消える**（直す対象でなくなる）
- **検証すべき組み合わせが半分になる。** 以後 `outputBackend` の分岐を持つ検証は不要
- V3 の CPU 合成での測定値（表 1・表 2 の CPU 側）は**参考記録**となり、判定には使わない

**注意**: GPU 合成が使えない環境の退避手段が無くなる。除去の前に、
GPU 合成の初期化失敗時の挙動（何が起きるか、利用者に何が見えるか）を確認しておくこと。

## 1. 変更の要旨

当初の v0.4 は「既定を GStreamer×Gpu にし、mpv／Cpu を退避経路として残す」だった。これを変更する。

- **mpv を v0.4 で完全に除去する**（v0.5 以降ではなく）。
- 退避経路の役割は、**GStreamer 内のデコード方式切替**が担う。
  ハードウェアデコード（`d3d11*dec`）とソフトウェアデコード（`avdec_*`）を**全体に効く 1 つのスイッチ**で選ぶ。
- **名前の整理も v0.4 に含める**（`IMpvApi` など mpv 形状の名前を中立な名前へ）。

### なぜこの設計が妥当か（親の評価）

退避経路を持つ目的は「ある素材が再生できないときの逃げ道」である。実際の失敗要因は
「そのコーデックにハードウェアデコーダが無い／効かない」であって、「mpv か GStreamer か」ではない。
したがって逃げ道は同じ GStreamer の中に置く方が直接的で、経路の数も減る。

**土台は既にある。** shim の profile 表には同じコーデックの GPU 版と CPU 版が既に両方あり、
S2 で「CPU デコード出力も `d3d11upload` で共有リングへ載せる」経路を作ってある:

```
h264-gpu / h265-gpu / vp9-gpu / av1-gpu     ← d3d11*dec
h264-cpu / h265-cpu / vp9-cpu / av1-cpu     ← avdec_* / dav1ddec
prores-cpu                                   ← avdec_prores
（最終退避）decodebin(sysmem) + d3d11upload
```

**つまり切替は新しい配管ではなく profile の探索順の変更で足りる。** 出力側（共有リング・合成・Spout）は
一切変わらないので、不変条件 I1〜I12 に触れない。

## 2. 実施順序（**この順序を守ること**）

1. **V3 を mpv と GStreamer の両方で測る**（VB-CABLE のループ。`SyncAccuracyE2ETests`）。
   V3 の合格条件は「mpv 経路と同等以下」であり、**mpv を消すと比較対象が永久に失われる**。
   mpv の基準値を記録に残してからでなければ削除に進めない。
2. デコード方式の切替を実装し、検証する（下記 3・5）。
3. mpv 実装を削除する（コミット A）。
4. 名前を整理する（コミット B）。
5. 既定値を切り替え、文書とインストーラーを更新する。

## 3. デコード方式の切替（仕様）

### 設定

- `AppSettings` に `decodeMode` を追加。値は `hardware`（**既定**）と `software`。
- **settings.json のみ。UI は v0.4 では設けない**（`backend` / `outputBackend` と同じ扱い）。
  本番中に切り替えたい運用があるなら UI 追加を別途検討するが、まずは設定で足りると判断する。
- 不正値は `hardware` として扱い、警告ログを出す。

### shim への伝達

C ABI に**明示的な経路**を設ける。環境変数で渡す方法は採らない（製品の設定が環境変数に化けるのは追跡しにくい）。
`tcs_player_create` の引数追加か `tcs_player_set_decode_mode(player, mode)` の新設かは実装側が提案し、
**親が承認してから実装する**（C ABI は不変条件の管轄）。

### profile の探索順

| モード | 探索順 |
| --- | --- |
| `hardware`（既定） | GPU profiles → CPU profiles → `decodebin(sysmem)`（現行と同一） |
| `software` | **CPU profiles → `decodebin(sysmem)` → GPU profiles（最後の手段）** |

`software` で GPU に落ちた場合は**必ず警告ログを出す**（「ソフトウェアデコーダが無いためハードウェアを使った」）。
黙って意図と違う経路を使わないこと。既定 `hardware` の挙動は現行から一切変えない。

## 4. mpv 除去の範囲

**2 コミットに分ける**（削除と改名を混ぜると差分が読めなくなる）。

### コミット A: mpv 実装の削除

- `Mpv.cs`、`MpvApi.cs`、`MpvRenderApi.cs`、`MpvRenderNative.cs`、`MpvLibraryNameResolver.cs`、
  `MpvPlaybackCommandBuilder.cs`、`MpvRenderFrameExecutor.cs`、`MpvSessionInitializer.cs`、
  `MpvStartupPropertyApplier.cs`、`Output/MpvSnapshotSource.cs`
- `AppSettings` の `backend`（`PlayerBackend`）とその分岐
- `scripts/get-mpv.ps1`、`native/libmpv-2.dll` の入手手順（SETUP.md・native/README.md）
- mpv 専用テスト（`MpvLibraryNameResolverTests` ほか）と、E2E の mpv 経路
- **`IMpvApi` / `IMpvRenderApi` の名前はこの時点では変えない**（削除の差分を読めるようにするため）

### コミット B: 名前の整理

`IMpvApi` → 中立名（例 `IPlayerApi`）、`IMpvRenderApi` → 例 `IPlayerRenderApi`、
`GstMpvApiAdapter` → 例 `GstPlayerApi`、`GstMpvRenderApiAdapter` → 例 `GstPlayerRenderApi`。
**機械的な改名のみ**とし、振る舞いの変更を混ぜない。名前は実装側が案を出し親が確認する。

## 5. 検証（V11 として追加）

デコード方式の切替は新機能なので、V1〜V10 と同じ厳しさで検証する。

| # | 項目 | 方法 | 合格 |
| --- | --- | --- | --- |
| V11-a | `hardware` 既定の非回帰 | V1 の 11 素材を既定で再実行 | V1 の結果と同一（デコーダ名も一致） |
| V11-b | `software` での再生能力 | 同じ 11 素材を `software` で実行し、**素材ごとに達成 fps と CPU 使用率を記録** | 1080p は実フレーム＝素材 fps。**4K は達成値を記録する（未達でも可。限界を文書化する）** |
| V11-c | 退避の警告 | ソフトウェアデコーダが無いコーデックで `software` を指定 | GPU に落ち、警告ログが出る |
| V11-d | 出力側の不変 | `software` で共通条件（表示・合成 p99・Spout・error・exit） | `hardware` と同等 |

### 事前計測（親、2026-09-13 09:10 JST）— **ソフトウェアデコードは 4K でも実用域**

`gst-launch-1.0 filesrc ! qtdemux ! <parse> ! <dec> ! videoconvert ! fakesink sync=false` の所要時間から
デコード単体の上限を測った（V1 の生成素材）:

| 素材 | ソフトウェア（`avdec_*`） | ハードウェア（`d3d11*dec`、参考） |
| --- | ---: | ---: |
| H.264 1080p60 | 2290 fps（**38.2×** 実時間） | 788 fps（13.1×） |
| HEVC 1080p60 | 890 fps（**14.8×**） | — |
| **HEVC 4K60** | 209 fps（**3.5×**） | 484 fps（8.1×） |
| ProRes 422 1080p60 | 586 fps（**9.8×**） | — |

**4K HEVC でも実時間の 3.5 倍**あり、退避経路として 4K を含めて成立する見込み。
当初の懸念（4K で追いつかない）は外れた。

**この数字の限定（重要）**:
- 素材は `testsrc2` の合成映像で、実写の 4K HEVC 10bit よりデコードが軽い。**実素材では 1〜1.5 倍程度まで落ちうる**。
- デコード単体の上限であり、合成・全画面表示・Spout が同時に走る本番条件ではない。
- ハードウェアが遅く見えるのは投入・同期のオーバーヘッドを含むスループット測定のためで、
  ハードウェアの価値は CPU コアを使わないこと。**この表で優劣を論じてはいけない。**

したがって **V11-b は「アプリ上で `software` を指定した実機 run」で改めて測る**。
現場素材を入手できたら同じ表を実素材で作り直す。

## 6. この変更で消える／変わる項目

- **V7（mpv×Gpu×Spout）は項目ごと消滅**する。条件未達だった「mpv 4K の合成 p99 2.94ms」も同時に解消する。
- V3 の合格条件「mpv 経路と同等以下」は、**上記 2 の順序で基準値を取った後は固定値との比較**になる。
- `docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の「v0.4 のゴール」節と
  `docs/release-0.4-plan.md` を、この文書に合わせて更新する（既定切替の記述、mpv 退避の記述）。

## 7. 未解決（親が判断を仰ぐ／調べる）

- C ABI への伝達方法（引数追加か新関数か）— 実装側の提案を待って親が承認。
- インストーラー（`scripts/installer.iss`）と `scripts/package-release.ps1` が
  GStreamer ランタイムと `tcs_gstreamer.dll` を同梱するか — **未調査**。既定にする以上これが無いと配布物が動かない。
- V8 の「落ち 0」の読み方（共通条件 59.9Hz 以上として読むか）— 利用者の確認待ち。
