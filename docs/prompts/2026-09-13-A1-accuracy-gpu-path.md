# A1: 同期精度ハーネスを GPU 出力経路に対応させる（V3 の前提）

worktree `C:\Users\<user>\Documents\timecode-sync-player-wt-output-engine-20260911-1247`（cwd 固定）。
main から新ブランチ `codex/a1-accuracy-gpu-20260913` を切る。不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`。
**実機・E2E を走らせる前に親へ一声かけること**（利用者が在席中）。

## 背景（親の計測、2026-09-13 09:08〜09:20、`TestResults/v3`）

V3（LTC 同期シーク精度）を VB-CABLE のループで実施できるようになったが、**判定できなかった**。
`SyncAccuracyE2ETests` の計測境界は「復号した LTC の受信 → WriteableBitmap への画素公開」で、
公開されたビットマップの画素マーカーを読んでどのフレームが出ているかを判定する作りのため、
**`outputBackend=Gpu` では画像が GPU 合成器へ直接渡り WriteableBitmap を経由せず、捕捉できない**。

| 構成 | ltcEvents | frameEvents |
| --- | ---: | ---: |
| mpv × Gpu | 2239 | **143** |
| GStreamer × Gpu | 2242 | **3** |
| mpv × Cpu | 2237 | 3300 |
| GStreamer × Cpu | 2241 | 3158 |

LTC 側は毎回正常（2237〜2242）。ループは機能している。

`outputBackend=0` に落として測る回避策は**使えない**。`backend=1` + `outputBackend=0` は
SETUP.md のとおり互換アダプター（CPU 読み戻し）経路で、出荷構成ではない。
mpv は本来の経路、GStreamer は読み戻し込みという不公平な比較になる。

## 依頼

**`outputBackend=Gpu` のときに、実際に表示されたフレームを同定して精度計測できるようにする。**

1. まず**設計案を親に提出して承認を得ること**（実装前）。論点は次のとおり:
   - 表示フレームの同定方法。現行は公開ビットマップの画素マーカー読み取り。GPU 経路では
     合成後テクスチャの読み戻しが要るが、**計測時のみ**行うこと（既定の再生に読み戻しを足さない）。
   - 公開時刻の基準。`events.jsonl` の `compose.publish` / `present.scanout` を使えるか。
     使えるなら、精度トレースと同じ時計（QPC）で突き合わせられる形にする。
   - 計測を有効にする条件。現行の `TIMECODE_ACCURACY_TRACE` の指定時のみ、など。
2. 承認後に実装する。**既定の再生経路に計測のコストを載せないこと**（不変条件 I4 の趣旨:
   使わない機能は合成 tick にコストを足さない）。
3. `scripts/analyze-sync-accuracy.py` 側も、新しいイベント種別を解釈できるようにする
   （現状 `unknown-trace-event-type` と `trace-event-count-mismatch` が出ている）。

## 合格条件

- `outputBackend=1` + `backend=1`（出荷構成）で `SyncAccuracyE2ETests` を実行し、
  解析が **COMPLETE** になること（`accuracyPassFail` が not-defined 以外、または complete=true）。
- 同じ手順で `backend=0`（mpv）+ `outputBackend=1` も測れること（**mpv 削除前の比較のため**）。
- 計測を有効にしない通常の run で、実機指標が現状と同等（実フレーム 60/秒、合成 p99 1ms 前後、
  生成→走査 4〜6ms、error 0）。**計測のコストが既定経路に載っていないことを数値で示すこと。**
- 非E2E 全件成功、全 E2E 58 成功・0 失敗・4 スキップ。
- 1 コミット（計測経路の追加＋解析スクリプトの対応）。

## 参考: 既に取れている mpv の基準値

mpv 本来の構成（`backend=0` + `outputBackend=0`）の steady: 平均絶対 59.7ms、p95 118.6ms、
p99 146.7ms、最大 160.0ms、測定 1741/1741。**GPU 経路で測り直した値と混同しないこと。**

報告は コミット／変更ファイル／設計案と親の承認の経緯／非E2E 件数／E2E／計測結果／設計差異／未検証。
