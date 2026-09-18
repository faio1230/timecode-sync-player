#!/usr/bin/env python3
"""M5: LTC fps と映像 fps の比が定常精度に効くかを既存 V3 run から集計する。

V3 の run（fixture.json / analysis/accuracy-samples.csv / analysis/accuracy-summary.json）だけを使う。
steady の定義は analyze-v3-spread-stages.py と同じ:
  status=measured、excludedReason なし、black-sweep 以外、signedErrorMs あり。
（LTC fps × 映像 fps）セルごとに n・許容 ms・平均・p50・p5・p95・p95−p5（bootstrap CI）・σ を出し、
予想される合流周期で残差を折り返した R² と並べ替え検定 p で「うなりの有無」を出す。
seek の収束（summary.json の ±80ms）とギャップの着地もセルに割り当てて表示する。

使い方:
  python scripts/analyze-ltc-fps-ratio.py --run DIR [--run DIR ...] [--output FILE]
"""
import argparse
import csv
import json
import math
from collections import defaultdict
from fractions import Fraction
from pathlib import Path

import numpy as np

BOOTSTRAP_ROUNDS = 2000
BOOTSTRAP_SEED = 20260918
PERMUTATION_ROUNDS = 1000
PERMUTATION_SEED = 20260918
SCAN_BINS = 12
MAX_PERIOD_SECONDS = 30.0

VIDEO_NAMES = {"24/1": "24", "25/1": "25", "30000/1001": "29.97", "30/1": "30", "60/1": "60"}
COMBO_ORDER = {"24": 0, "25": 1, "29.97": 2, "30": 3}
PHASE_CLIP = {"seek-a": "1", "seek-b": "2", "seek-c": "3", "seek-back": "1"}
CLIP_VIDEO = {"1": "24", "2": "29.97", "3": "60"}


def percentile(values, p):
    ordered = sorted(values)
    if not ordered:
        return None
    return ordered[max(0, min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1)))))]


def video_fps(value):
    numerator, denominator = value.split("/")
    return float(numerator) / float(denominator)


def ltc_name(fps):
    if abs(fps - 24.0) < 0.01:
        return "24"
    if abs(fps - 25.0) < 0.01:
        return "25"
    if abs(fps - 30.0) < 0.01:
        return "30"
    if abs(fps - 30000.0 / 1001.0) < 0.01:
        return "29.97"
    return f"{fps}"


def load_run(run, phase_filter):
    fixture = json.loads((run / "fixture.json").read_text(encoding="utf-8-sig"))
    ltc = float(fixture["ltcFps"])
    with (run / "analysis" / "accuracy-samples.csv").open(encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    records = []
    for row in rows:
        if row["status"] != "measured" or row["excludedReason"] not in ("", None):
            continue
        if row["phase"] == "black-sweep" or row["signedErrorMs"] in ("", None):
            continue
        if phase_filter and row["phase"] != phase_filter:
            continue
        if row["expectedClipId"] not in ("1", "2", "3"):
            continue
        video = video_fps(row["fps"])
        records.append({
            "run": run.name,
            "ltc": ltc,
            "ltcName": ltc_name(ltc),
            "video": video,
            "videoName": VIDEO_NAMES.get(row["fps"], row["fps"]),
            "clip": row["expectedClipId"],
            "phase": row["phase"],
            "seconds": float(row["receivedSeconds"]),
            "err": float(row["signedErrorMs"]),
            "intervalErr": float(row["intervalErrorMs"]) if row["intervalErrorMs"] not in ("", None) else None,
        })
    return ltc, records


def tolerance_ms(video, ltc):
    return max(1.0 / video, 1.0 / ltc) * 6.0 * 1000.0


def cell_stats(errors):
    values = np.asarray(errors, dtype=float)
    n = len(values)
    ordered = np.sort(values)
    def rank(p):
        return float(ordered[max(0, min(n - 1, int(round(p / 100 * (n - 1)))))])
    p5, p50, p95 = rank(5), rank(50), rank(95)
    rng = np.random.default_rng(BOOTSTRAP_SEED)
    spans = []
    if n >= 5:
        draws = values[rng.integers(0, n, size=(BOOTSTRAP_ROUNDS, n))]
        draws.sort(axis=1)
        i5 = max(0, min(n - 1, int(round(5 / 100 * (n - 1)))))
        i95 = max(0, min(n - 1, int(round(95 / 100 * (n - 1)))))
        spans = draws[:, i95] - draws[:, i5]
    return {
        "n": n,
        "mean": float(values.mean()),
        "p50": p50,
        "p5": p5,
        "p95": p95,
        "span": p95 - p5,
        "spanLow": float(np.percentile(spans, 2.5, method="nearest")) if len(spans) else None,
        "spanHigh": float(np.percentile(spans, 97.5, method="nearest")) if len(spans) else None,
        "sigma": float(values.std(ddof=0)),
    }


def expected_periods(ltc, video):
    ratio = Fraction(video / ltc).limit_denominator(2000)
    period = ratio.denominator / ltc
    periods = [period]
    if abs(ratio - round(ratio)) < 0.01 and round(ratio) % 2 == 0:
        periods.append(period / 2)
    return sorted({round(value, 6) for value in periods if value > 0})


def fold_r2(times, values, period, bins):
    phase = np.mod(times, period) / period
    index = np.minimum((phase * bins).astype(int), bins - 1)
    counts = np.bincount(index, minlength=bins).astype(float)
    sums = np.bincount(index, weights=values, minlength=bins)
    total = float(((values - values.mean()) ** 2).sum())
    if total <= 0:
        return 0.0, None
    between = 0.0
    means = []
    for count, value_sum in zip(counts, sums):
        if count > 0:
            mean = value_sum / count
            means.append(mean)
            between += count * (mean - values.mean()) ** 2
    return between / total, (max(means) - min(means) if means else None)


def fold_permutation_p(times, values, period, bins):
    rng = np.random.default_rng(PERMUTATION_SEED)
    observed, amplitude = fold_r2(times, values, period, bins)
    hits = 0
    for _ in range(PERMUTATION_ROUNDS):
        shuffled = rng.permutation(values)
        value, _ = fold_r2(times, shuffled, period, bins)
        if value >= observed:
            hits += 1
    return observed, amplitude, (hits + 1) / (PERMUTATION_ROUNDS + 1)


def scan_peaks(times, values, min_period):
    span = float(times.max() - times.min())
    upper = min(MAX_PERIOD_SECONDS, span / 1.5)
    if upper <= min_period:
        return []
    peaks = []
    for period in np.geomspace(min_period, upper, 120):
        value, _ = fold_r2(times, values, float(period), SCAN_BINS)
        peaks.append((float(period), value))
    peaks.sort(key=lambda item: -item[1])
    picked = []
    for period, value in peaks:
        if all(abs(math.log(period / other)) > 0.35 for other, _ in picked):
            picked.append((period, value))
        if len(picked) == 4:
            break
    return picked


def beat_section(records):
    lines = []
    groups = defaultdict(list)
    for record in records:
        groups[(record["run"], record["videoName"], record["video"], record["ltc"])].append(record)
    lines.append("### うなり検査（予想周期で折り返し）")
    lines.append("")
    lines.append("| run | LTC×映像 | 予想周期 s | カバー率 | bin | R² | 並べ替え p | 振幅 ms | 走査ピーク (周期 s, R²) |")
    lines.append("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |")
    for (run_name, video_name, video, ltc), rows in sorted(groups.items()):
        times = np.asarray([row["seconds"] for row in rows], dtype=float)
        values = np.asarray([row["err"] for row in rows], dtype=float)
        median_dt = float(np.median(np.diff(np.sort(times)))) if len(times) > 1 else 0.0
        for period in expected_periods(ltc, video):
            if period < 4 * median_dt:
                lines.append(f"| {run_name} | {ltc_name(ltc)}×{video_name} | {period:.4f} | - | - | - | - "
                             f"| - | 周期がサンプル間隔以下 |")
                continue
            bins = max(3, min(60, int(round(period / median_dt)) if median_dt > 0 else 12))
            r2, amplitude, p_value = fold_permutation_p(times, values, period, bins)
            coverage = (times.max() - times.min()) / period
            peaks = scan_peaks(times, values, max(4 * median_dt, 0.02))
            peak_text = "; ".join(f"{peak:.3f}, {value:.2f}" for peak, value in peaks)
            lines.append(f"| {run_name} | {ltc_name(ltc)}×{video_name} | {period:.4f} | {coverage:.1f} | {bins} | "
                         f"{r2:.3f} | {p_value:.3f} | {amplitude:+.2f} | {peak_text} |")
    lines.append("")
    return lines


def phase_drift_ms_per_second(ltc, video):
    ltc_ms = 1000.0 / ltc
    video_ms = 1000.0 / video
    delta = ltc_ms % video_ms
    if delta > video_ms / 2:
        delta -= video_ms
    return delta * ltc


def slope_section(records):
    groups = defaultdict(list)
    for record in records:
        if record["phase"] == "freeze-sweep":
            groups[(record["run"], record["ltcName"], record["videoName"],
                    record["video"], record["ltc"])].append(record)
    lines = ["### freeze-sweep のメディア時間傾き（残差 vs receivedSeconds、OLS）", "",
             "位相ドリフト予測は (L mod h) の最短表現 × LTC fps。誤差 = C − 位相 なので傾きの予測は符号反転。",
             "|Δ| が大きいセルは周期が短く、窓内の OLS は 0 近傍になる（比較は小さいセルだけ）。", "",
             "| run | LTC×映像 | n | メディア範囲 s | 傾き ms/s | 位相ドリフト予測 ms/s |",
             "| --- | --- | ---: | --- | ---: | ---: |"]
    for key in sorted(groups, key=lambda item: (item[0], COMBO_ORDER.get(item[1], 9), COMBO_ORDER.get(item[2], 9))):
        run_name, ltc_display, video_display, video, ltc = key
        times = np.asarray([row["seconds"] for row in groups[key]], dtype=float)
        values = np.asarray([row["err"] for row in groups[key]], dtype=float)
        centered = times - times.mean()
        slope = float((centered * (values - values.mean())).sum() / (centered ** 2).sum())
        lines.append(f"| {run_name} | {ltc_display}×{video_display} | {len(values)} | "
                     f"{times.min():.1f}–{times.max():.1f} | {slope:+.3f} | "
                     f"{phase_drift_ms_per_second(ltc, video):+.3f} |")
    lines.append("")
    return lines


def recovery_section(runs):
    lines = ["### 収束（summary.json: seek → |interval error| ≤ 80ms）とギャップ着地"]
    lines.append("")
    lines.append("| run | LTC | seek-a→24 | seek-b→29.97 | seek-c→60 | seek-back→24 | gap 12s→29.97 ms | gap 24s→60 ms |")
    lines.append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    for run in runs:
        summary = json.loads((run / "analysis" / "accuracy-summary.json").read_text(encoding="utf-8-sig"))
        ltc = ltc_name(float(json.loads((run / "fixture.json").read_text(encoding="utf-8-sig"))["ltcFps"]))
        recovery = {}
        for entry in summary.get("recovery", []):
            value = (entry.get("within80Ms") or {}).get("recoveryMs")
            recovery[PHASE_CLIP.get(entry["phase"], "?")] = value
        gaps = {}
        for gap in summary.get("gaps", []):
            gaps[round(gap["receivedSeconds"])] = gap.get("latencyMs")
        fmt = lambda value: "-" if value is None else f"{value:.1f}"
        lines.append(f"| {run.name} | {ltc} | {fmt(recovery.get('1'))} | {fmt(recovery.get('2'))} | "
                     f"{fmt(recovery.get('3'))} | {fmt(recovery.get('1'))} | {fmt(gaps.get(12))} | {fmt(gaps.get(24))} |")
    lines.append("")
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--phase", default="", help="指定するとその phase だけ使う（例 freeze-sweep）")
    args = parser.parse_args()

    all_records = []
    per_run = []
    for run in args.run:
        ltc, records = load_run(run, args.phase)
        per_run.append((run, ltc, records))
        all_records += records

    scope = f"phase={args.phase}" if args.phase else "phase=all steady"
    lines = [f"run 数={len(per_run)}  {scope}  steady サンプル合計={len(all_records)}", ""]
    cells = defaultdict(list)
    for record in all_records:
        cells[(record["ltcName"], record["videoName"], record["video"], record["ltc"])].append(record["err"])

    lines.append("## (LTC fps × 映像 fps) セル（全 run 合算。run の内訳は後段）")
    lines.append("")
    lines.append("| LTC | 映像 | n | 許容 ms | 平均 | p50 | p5 | p95 | p95−p5 | p95−p5 95%CI | σ |")
    lines.append("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |")
    for key in sorted(cells, key=lambda item: (COMBO_ORDER.get(item[0], 9), COMBO_ORDER.get(item[1], 9))):
        ltc_display, video_display, video, ltc = key
        stats = cell_stats(cells[key])
        ci = ("-" if stats["spanLow"] is None else
              f"[{stats['spanLow']:.2f}, {stats['spanHigh']:.2f}]")
        lines.append(f"| {ltc_display} | {video_display} | {stats['n']} | {tolerance_ms(video, ltc):.0f} | "
                     f"{stats['mean']:+.2f} | {stats['p50']:+.2f} | {stats['p5']:+.2f} | {stats['p95']:+.2f} | "
                     f"{stats['span']:.2f} | {ci} | {stats['sigma']:.2f} |")
    lines.append("")

    lines.append("## run 別セル")
    lines.append("")
    lines.append("| run | LTC | 映像 | n | 許容 ms | 平均 | p50 | p95 | p95−p5 | σ |")
    lines.append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for run, ltc, records in per_run:
        run_cells = defaultdict(list)
        for record in records:
            run_cells[record["videoName"]].append(record["err"])
        for video_display in sorted(run_cells, key=lambda item: COMBO_ORDER.get(item, 9)):
            stats = cell_stats(run_cells[video_display])
            video = video_fps({"24": "24/1", "25": "25/1", "29.97": "30000/1001", "30": "30/1", "60": "60/1"}[video_display])
            lines.append(f"| {run.name} | {ltc_name(ltc)} | {video_display} | {stats['n']} | "
                         f"{tolerance_ms(video, ltc):.0f} | {stats['mean']:+.2f} | {stats['p50']:+.2f} | "
                         f"{stats['p95']:+.2f} | {stats['span']:.2f} | {stats['sigma']:.2f} |")
    lines.append("")

    lines += beat_section(all_records)
    lines += slope_section(all_records)
    lines += recovery_section([run for run, _, _ in per_run])

    text = "\n".join(lines)
    print(text)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
        print("wrote", args.output)


if __name__ == "__main__":
    main()
