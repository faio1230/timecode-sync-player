#!/usr/bin/env python3
"""M3-b: 「制御ループが見る報告位置」と「発表されたフレーム PTS」の差を素材条件ごとに集計する。

V3 の run（analysis/accuracy-samples.csv + events.jsonl + fixture.json）から steady サンプルだけを使い、
scripts/analyze-v3-spread-stages.py の two_bases と同じ定義で 1 サンプル 1 行を作る:
  - steady: status=measured、excludedReason なし、black-sweep 以外
  - compose.publish と frameTicks を 2ms 以内で突き合わせ
  - 同じ ticks に最も近い gst.position を 25ms 以内で採用し、取得 lag 分だけ期待値を外挿
  - displayed = signedErrorMs（フレーム PTS − 期待メディア秒）
  - reported  = position − 外挿期待
  - difference = displayed − reported（固定遅延の推定量）
素材 fps（と run / LTC fps）ごとに n・中央値・p5・p95・平均・σ・p95|差|・中央値の 95% CI
（bootstrap、決定的 seed）を出す。判定はしない。

使い方:
  python scripts/analyze-sync-offset.py --run DIR [--run DIR ...] [--output FILE]
"""
import argparse
import bisect
import csv
import json
import random
import statistics
from collections import defaultdict
from pathlib import Path

FREQ = 10_000_000
PUBLISH_TOLERANCE_TICKS = 20_000
POSITION_TOLERANCE_TICKS = 250_000
BOOTSTRAP_ROUNDS = 4000
BOOTSTRAP_SEED = 20260919

FPS_NAMES = {"24/1": "24", "25/1": "25", "30000/1001": "29.97", "30/1": "30", "60/1": "60"}
ORDER = {"24": 0, "25": 1, "29.97": 2, "30": 3}


def ltc_label(fps):
    for name in ("24", "25", "30"):
        if abs(fps - float(name)) < 0.01:
            return name
    return "29.97" if abs(fps - 30000.0 / 1001.0) < 0.01 else str(fps)


def combo_key(record):
    return f"{ltc_label(record['ltcFps'])}×{record['fpsName']}"


def combo_sort(text):
    ltc, video = text.split("×", 1)
    return (ORDER.get(ltc, 9), ORDER.get(video, 9))


def percentile(values, p):
    ordered = sorted(values)
    if not ordered:
        return None
    return ordered[max(0, min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1)))))]


def fnum(row, key):
    value = row.get(key)
    if value in (None, ""):
        return None
    return float(value)


def events_path(run):
    direct = run / "events.jsonl"
    if direct.exists():
        return direct
    candidates = sorted(run.glob("*/events.jsonl"))
    if not candidates:
        raise FileNotFoundError(f"events.jsonl not found under {run}")
    return max(candidates, key=lambda path: path.stat().st_mtime)


def load_events(path):
    stages = defaultdict(list)
    with Path(path).open(encoding="utf-8-sig") as handle:
        for line in handle:
            if line.strip():
                event = json.loads(line)
                stages[event["stage"]].append(event)
    for events in stages.values():
        events.sort(key=lambda e: e["qpc"])
    return stages


def steady_rows(path):
    with Path(path).open(encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    return [r for r in rows if r["status"] == "measured" and r["excludedReason"] in ("", None)
            and r["phase"] != "black-sweep" and fnum(r, "signedErrorMs") is not None]


def nearest(events, qpc, target, tolerance):
    index = bisect.bisect_left(qpc, target)
    best = None
    for candidate in (index - 1, index, index + 1):
        if 0 <= candidate < len(events):
            distance = abs(qpc[candidate] - target)
            if best is None or distance < best[0]:
                best = (distance, events[candidate])
    if best is None or best[0] > tolerance:
        return None
    return best[1]


def collect(run):
    fixture = json.loads((run / "fixture.json").read_text(encoding="utf-8-sig"))
    stages = load_events(events_path(run))
    positions = stages.get("gst.position", [])
    publishes = stages.get("compose.publish", [])
    position_qpc = [e["qpc"] for e in positions]
    publish_qpc = [e["qpc"] for e in publishes]

    records = []
    join_miss = 0
    unmatched = 0
    for row in steady_rows(run / "analysis" / "accuracy-samples.csv"):
        frame_ticks = fnum(row, "frameTicks")
        expected = fnum(row, "expectedMediaSeconds")
        tick = fnum(row, "ticks")
        if frame_ticks is None or expected is None or tick is None:
            continue
        if nearest(publishes, publish_qpc, frame_ticks, PUBLISH_TOLERANCE_TICKS) is None:
            join_miss += 1
            continue
        position = nearest(positions, position_qpc, tick, POSITION_TOLERANCE_TICKS)
        if position is None:
            unmatched += 1
            continue
        lag_ms = (position["qpc"] - tick) * 1000.0 / FREQ
        reported_ms = position["value"] / 1000.0 - expected * 1000.0 - lag_ms
        displayed_ms = fnum(row, "signedErrorMs")
        records.append({
            "run": run.name,
            "ltcFps": fixture.get("ltcFps"),
            "phase": row["phase"],
            "clip": row["expectedClipId"],
            "fps": row["fps"],
            "fpsName": FPS_NAMES.get(row["fps"], row["fps"]),
            "displayedMs": displayed_ms,
            "reportedMs": reported_ms,
            "differenceMs": displayed_ms - reported_ms,
            "lagMs": lag_ms,
        })
    return records, join_miss, unmatched


def bootstrap_ci(values):
    if len(values) < 3:
        return None, None
    rng = random.Random(BOOTSTRAP_SEED)
    n = len(values)
    medians = []
    for _ in range(BOOTSTRAP_ROUNDS):
        sample = rng.choices(values, k=n)
        medians.append(statistics.median(sample))
    return percentile(medians, 2.5), percentile(medians, 97.5)


def stats(values):
    if not values:
        return None
    absolute = sorted(abs(v) for v in values)
    low, high = bootstrap_ci(values)
    return {
        "n": len(values),
        "median": statistics.median(values),
        "p5": percentile(values, 5),
        "p95": percentile(values, 95),
        "mean": statistics.fmean(values),
        "sigma": statistics.pstdev(values) if len(values) > 1 else 0.0,
        "p95Abs": percentile(absolute, 95),
        "maxAbs": max(absolute),
        "ciLow": low,
        "ciHigh": high,
    }


def fmt(value, digits=2):
    return "-" if value is None else f"{value:+.{digits}f}"


def condition_table(title, groups):
    lines = [f"## {title}", "",
             "| LTC×映像 | n | 差(表示−報告) 中央値 | p5 | p95 | 平均 | σ | p95|差| | 中央値95%CI |",
             "| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"]
    for name in sorted(groups, key=combo_sort):
        values = [r["differenceMs"] for r in groups[name]]
        s = stats(values)
        lines.append(f'| {name} | {s["n"]} | {fmt(s["median"])} | {fmt(s["p5"])} | {fmt(s["p95"])} | '
                     f'{fmt(s["mean"])} | {s["sigma"]:.2f} | {s["p95Abs"]:.2f} | '
                     f'[{fmt(s["ciLow"])}, {fmt(s["ciHigh"])}] |')
    lines.append("")
    return lines


def base_table(title, groups):
    lines = [f"### {title}", "",
             "| LTC×映像 | n | 表示 PTS 平均 | 表示 σ | 報告位置 平均 | 報告 σ |",
             "| ---: | ---: | ---: | ---: | ---: | ---: |"]
    for name in sorted(groups, key=combo_sort):
        rows = groups[name]
        displayed = stats([r["displayedMs"] for r in rows])
        reported = stats([r["reportedMs"] for r in rows])
        lines.append(f'| {name} | {displayed["n"]} | {fmt(displayed["mean"])} | {displayed["sigma"]:.2f} | '
                     f'{fmt(reported["mean"])} | {reported["sigma"]:.2f} |')
    lines.append("")
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    lines = []
    merged = defaultdict(list)
    per_run = {}
    for run in args.run:
        records, join_miss, unmatched = collect(run)
        per_run[run] = records
        for record in records:
            merged[combo_key(record)].append(record)
        ltc = records[0]["ltcFps"] if records else "-"
        lines.append(f"run: {run.name} ltcFps={ltc} steady(結合後) n={len(records)} "
                     f"公開突き合わせ失敗={join_miss} 位置未対応={unmatched}")
    lines.append("")

    lines += condition_table("素材条件（LTC fps × 映像 fps、全 run 合算）: 差 = 表示 PTS − 報告位置", merged)
    lines += base_table("基準別（LTC fps × 映像 fps、全 run 合算）", merged)

    for run, records in per_run.items():
        groups = defaultdict(list)
        for record in records:
            groups[combo_key(record)].append(record)
        lines += condition_table(f"run 別: {run.name}", groups)

    text = "\n".join(lines)
    print(text)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
        print("wrote", args.output)


if __name__ == "__main__":
    main()
