# P1: 配布物の GStreamer 対応（v0.4 の出荷ブロッカー）

作業する git の作業ツリー（worktree）:
`C:\Users\codea\Documents\timecode-sync-player-wt-pkg-20260915`
ブランチ `codex/v04-packaging-20260915`（`main 220296e` から作成済み）。
**共有ツリー（`...\timecode-sync-player`）では作業しないこと。**

---

## 1. 問題

v0.4 は `PlayerBackend=Gstreamer` を既定にする。しかし**配布物に GStreamer が一切入っていない**。

| ファイル | 現状 |
| --- | --- |
| `scripts/installer.iss` | 全面的に mpv 前提。`get-mpv.ps1` を同梱し、インストール後に実行させる。**GStreamer への言及ゼロ** |
| `scripts/package-release.ps1` | `$requiredRuntimeFiles = @("TimecodeSyncPlayer.exe", "TimecodeSyncPlayer.dll", "SpoutDX.dll")`。**`tcs_gstreamer.dll` が無い** |

このままだと、既定を切り替えた v0.4 は**インストールしても再生できない**。

## 2. 依頼（調査が主。実装はそのあと）

### 2-1. 何が要るのかを確定する

shim（`native/gst-shim/src/tcs_gstreamer.cpp`）は `gst_parse_launch` を使わず、
次の element を明示的に作っている:

```
appsink audioconvert audiotestsrc autoaudiosink capsfilter
d3d11upload decodebin fakesink filesrc queue videoconvert volume
```

**`decodebin` が問題の中心**である。実行時に利用可能なプラグインから demuxer / decoder を選ぶため、
必要なプラグイン集合は**扱う素材で決まる**。対象は V1 のコーデック行列（11 本、
`docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md` の「V1 の結果」）と
`artifacts/media/v1` の素材。

出すもの:

- `tcs_gstreamer.dll` 自身の DLL 依存（`dumpbin /dependents` 等）
- V1 の 11 本を再生するのに実際に読み込まれる GStreamer プラグイン
  （`GST_DEBUG` のプラグイン読み込みログか `gst-inspect-1.0` で確定させる。**推測で並べないこと**）
- 各プラグイン DLL の**ファイル名・サイズ・ライセンス**
- 合計サイズ

### 2-2. 配布方式を 3 案で比較する

1. **(a) 同梱** — 必要な DLL を配布物に入れる
2. **(b) 後から取得** — `get-gstreamer.ps1` を作り、mpv と同じくインストール後に実行させる
3. **(c) 利用者が別途インストール** — 公式ランタイムを前提にする

各案について**サイズ・更新の追随・ライセンス・失敗時の挙動**を表にすること。

### 2-3. 実装する（方式の最終決定は利用者が行う。**先に 2-1・2-2 を報告して指示を待つこと**）

## 3. ライセンスについて（重要）

**法的な判断はしないこと。事実の収集だけを行う。**

親の調査では、公式ランタイムの ffmpeg は LGPL-2.1-or-later（`gstlibav.dll` = `avdec_*` の実体）で、
GPL なのは x264・x265・a52dec（エンコーダか AC-3 で、再生経路では不使用）。
**これを正しいものとして採用せず、2-1 で実際に読み込まれたプラグインについて自分で確認すること。**
確認できない項目は「不明」と書く。最終判断は利用者と法務が行う。

## 4. 守ること

1. **実機（全画面・GPU 性能試験）は使わない。** この作業に実機は要らない。
   もう一方のペインが実機を使うので、**性能試験を走らせないこと**
2. **合否判定は書かないこと**（判定は親が出す）
3. `main` へ書き込まない。統合は親が行う
4. 2-1 と 2-2 を報告してから 2-3 に進むこと。**方式を自分で決めて実装しない**
5. 非E2E が全て通ること

---

## 5. 前提の明確化（利用者の指示、2026-09-15）

**配布物が mpv 前提なのは、v0.3 がそうだったからである。
v0.4 は「そこを GStreamer に変える」大型アップデートである。**

したがってこの作業は「mpv の隣に GStreamer を足す」ではない。
**v0.3 で mpv が占めていた位置（必須ランタイム）を GStreamer が引き継ぐ**と考えること。

v0.3 で mpv が受けていた扱いを、そのまま GStreamer 側の要件として読み替える:

| v0.3 の mpv | v0.4 で GStreamer に必要な対応 |
| --- | --- |
| `get-mpv.ps1` を同梱し、インストール後に実行 | 同等の取得手段（方式は 2-2 で比較） |
| インストーラーに「今すぐ mpv をダウンロード」タスク | 同等のタスク |
| `installer.iss` の `Excludes` で DLL を除外 | GStreamer 側の同等の扱い |
| README に「libmpv を別途入れる」手順 | GStreamer の手順に差し替え |
| `$requiredRuntimeFiles` に含まれない | `tcs_gstreamer.dll` は**必須ファイル**として検証する |

**mpv は v0.4 で完全に除去する**（2026-09-15 利用者の決定。当初この節に「退避経路として残し任意へ降りる」と
書いていたが誤りで、作業中に口頭で訂正した。文書への反映が遅れたので 2026-09-16 に直した）。
**GStreamer が唯一のランタイム**であり、配布物に mpv は一切含めない。

## 6. 「実機を使わない」の意味（2026-09-15 の質問への回答）

**禁止しているのは全画面表示と GPU 性能試験**（`Invoke-AppGpuTrial.ps1` による測定）である。
もう一方のペインが同じ画面と GPU を使うため。

**プラグインの特定に必要な範囲の実行は可**:

- `gst-inspect-1.0` によるプラグイン一覧・依存の確認
- `GST_DEBUG` を上げて `decodebin` が選んだ element を記録する目的で、
  **`fakesink` 等に流す短時間のヘッドレス実行**
- `dumpbin /dependents` などの静的な依存調査

全画面を開かず、性能を測らないなら問題ない。**時間のかかる実行を始める前に一報を入れること。**
