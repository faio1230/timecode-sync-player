#!/usr/bin/env python3
"""M6: ダーティー LTC の限界点をレベル窓ごとに集計する（clean 基準の相対判定）。

入力（1 つ以上の run のレポートディレクトリ）:
  dirty-plan.json, fixture.json, phases.jsonl, trace.jsonl, events.jsonl（root か *\\events.jsonl）
出力（標準出力）:
  run ごとのレベル別 decode 成功率・値の跳び率・同期誤差分布・ギャップ・gate_rejected・seeks、
  clean レベルからの低下幅（ポイント）、および「低下なしの最大悪化」「最初に落ちた点」「落ち方」。
  複数 run を渡すと、同じレベル名を run 横断で並べる（限界近傍の反復用）。
判定はしない（数字のみ）。

使い方:
  python scripts/analyze-dirty-ltc.py <report-dir> [<report-dir> ...] [--degrade-points 0.5]

判定規則:
  baseline = 同じ run 先頭の clean レベルの clean% 実測値。
  低下 = (baseline - level.clean%) が degrade-points（既定 0.5 ポイント）を超えたら「落ちた」。
  境界で 1 フレーム前後が落ちることがあるため、絶対値ではなく相対で見る。
"""
import argparse
import bisect
import json
import statistics
from pathlib import Path

FREQ_FALLBACK = 10_000_000
DEFAULT_DEGRADE_POINTS = 0.5
STEP_POINTS = 5.0


def load_jsonl(path):
    return [json.loads(line) for line in Path(path).read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def percentile(values, p):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1)))))]


def find_events(report):
    direct = report / "events.jsonl"
    if direct.exists():
        return direct
    candidates = sorted(report.glob("*/events.jsonl"))
    return max(candidates, key=lambda path: path.stat().st_mtime) if candidates else None


def clip_fps(clip):
    return clip["fpsNumerator"] / clip["fpsDenominator"]


def analyze_report(report):
    plan = json.loads((report / "dirty-plan.json").read_text(encoding="utf-8-sig"))
    fixture = json.loads((report / "fixture.json").read_text(encoding="utf-8-sig"))
    clips = {str(clip["id"]): clip for clip in fixture["clips"]}
    levels = {level["name"]: level for level in plan["levels"]}
    settling = float(plan.get("settlingSeconds", 2.0))
    ltc_fps = float(plan.get("ltcFps", 25.0))

    events = load_jsonl(report / "trace.jsonl")
    meta = next((event for event in events if event.get("type") == "meta"), {})
    frequency = meta.get("frequency", FREQ_FALLBACK)
    to_ms = 1000.0 / frequency
    ltc_events = sorted((event for event in events if event.get("type") == "ltc"), key=lambda event: event["ticks"])
    frame_events = sorted((event for event in events if event.get("type") == "frame"), key=lambda event: event["ticks"])
    frame_ticks = [event["ticks"] for event in frame_events]

    output_events = find_events(report)
    evaluations = []
    seeks = []
    if output_events is not None:
        for event in load_jsonl(output_events):
            if event.get("stage") == "sync.evaluate":
                evaluations.append(event)
            elif event.get("stage") == "load.issue":
                seeks.append(event)
    evaluation_ticks = [event["qpc"] for event in evaluations]
    seek_ticks = [event["qpc"] for event in seeks]

    phases = [event for event in load_jsonl(report / "phases.jsonl")
              if event.get("type") == "phase-start" and str(event.get("name", "")).startswith("dirty-")]
    rows = []
    for phase in phases:
        name = str(phase["name"])[len("dirty-"):]
        level = levels.get(name)
        if level is None:
            continue
        seconds = float(level.get("seconds", 20.0))
        start = phase["ticks"] + int(round(settling * frequency))
        end = start + int(round(seconds * frequency))
        window_ltc = [event for event in ltc_events if start <= event["ticks"] <= end]
        expected = seconds * ltc_fps
        decoded = len(window_ltc)
        decode_rate = decoded / expected if expected > 0 else 0.0

        jumps = 0
        gaps = []
        for previous, current in zip(window_ltc, window_ltc[1:]):
            gap_ms = (current["ticks"] - previous["ticks"]) * to_ms
            if gap_ms > 2.5 * 1000.0 / ltc_fps:
                gaps.append(gap_ms)
            delta = current.get("seconds", 0.0) - previous.get("seconds", 0.0)
            if delta <= 0 or abs(delta - 1.0 / ltc_fps) > 0.5 / ltc_fps:
                jumps += 1
        clean_rate = max(0.0, decoded - jumps) / expected if expected > 0 else 0.0

        errors = []
        unpaired = 0
        over_tolerance = 0
        for event in window_ltc:
            index = bisect.bisect_right(frame_ticks, event["ticks"]) - 1
            if index < 0:
                unpaired += 1
                continue
            frame = frame_events[index]
            clip = clips.get(str(frame.get("clipId")))
            if frame.get("markerValid") is not True or frame.get("isBlack") is True or clip is None:
                unpaired += 1
                continue
            pts = clip["timelineOffset"] + frame["frameIndex"] / clip_fps(clip)
            error = (pts - event["seconds"]) * 1000.0
            errors.append(error)
            tolerance = max(1.0 / clip_fps(clip), 1.0 / ltc_fps) * 6.0 * 1000.0
            if abs(error) > tolerance:
                over_tolerance += 1

        gate_delta = 0
        if evaluations:
            low = bisect.bisect_left(evaluation_ticks, start)
            high = bisect.bisect_right(evaluation_ticks, end)
            values = []
            for event in evaluations[low:high]:
                detail = str(event.get("detail", ""))
                marker = "gate_rejected="
                position = detail.find(marker)
                if position >= 0:
                    tail = detail[position + len(marker):]
                    number = "".join(ch for ch in tail.split()[0] if ch.isdigit())
                    if number:
                        values.append(int(number))
            if values:
                gate_delta = max(values) - min(values)
        seek_count = bisect.bisect_right(seek_ticks, end) - bisect.bisect_left(seek_ticks, start)

        rows.append({
            "name": name,
            "seconds": seconds,
            "expected": expected,
            "decoded": decoded,
            "decode_rate": decode_rate,
            "jumps": jumps,
            "clean_rate": clean_rate,
            "errors": errors,
            "unpaired": unpaired,
            "over_tolerance": over_tolerance,
            "gaps": gaps,
            "gate_delta": gate_delta,
            "seeks": seek_count,
        })
    return {"report": report, "condition": plan.get("condition"), "rows": rows}


def fmt(value, digits=2):
    return "-" if value is None else f"{value:+.{digits}f}"


def print_report(run, degrade_points):
    rows = run["rows"]
    baseline_row = next((row for row in rows if row["name"] == "clean"), None)
    baseline = baseline_row["clean_rate"] if baseline_row else 1.0
    print(f"## {run['report']}")
    print(f"condition={run['condition']} levels={len(rows)} clean 実測={100 * baseline:.1f}%")
    print("")
    print("| レベル | 窓 s | 期待 | 受信 | decode% | 跳び | clean% | Δclean | 誤差 mean | p5 | p95 | max | 未対応 | |err|>許容 | gap 回/max ms | gate_rejected Δ | seeks |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |")
    for row in rows:
        delta = (row["clean_rate"] - baseline) * 100.0
        gap_text = f"{len(row['gaps'])} / {max(row['gaps']):.1f}" if row["gaps"] else "0 / -"
        tolerance_pct = 100.0 * row["over_tolerance"] / len(row["errors"]) if row["errors"] else 0.0
        print(f"| {row['name']} | {row['seconds']:.0f} | {row['expected']:.0f} | {row['decoded']} | "
              f"{100 * row['decode_rate']:.1f} | {row['jumps']} | {100 * row['clean_rate']:.1f} | "
              f"{delta:+.1f}pt | {fmt(statistics.fmean(row['errors']) if row['errors'] else None)} | "
              f"{fmt(percentile(row['errors'], 5))} | {fmt(percentile(row['errors'], 95))} | "
              f"{fmt(max(row['errors']) if row['errors'] else None)} | {row['unpaired']} | "
              f"{tolerance_pct:.1f}% | {gap_text} | {row['gate_delta']} | {row['seeks']} |")
    print("")

    degraded = [row for row in rows
                if row["name"] != "clean" and (baseline - row["clean_rate"]) * 100.0 > degrade_points]
    worst_ok = "-"
    for row in rows:
        if row["name"] == "clean":
            continue
        if row in degraded:
            break
        worst_ok = row["name"]
    first_bad = degraded[0]["name"] if degraded else "なし"
    shape = "-"
    if degraded:
        index = rows.index(degraded[0])
        previous = rows[index - 1]["clean_rate"] if index > 0 else baseline
        drop = (previous - degraded[0]["clean_rate"]) * 100.0
        shape = f"急（{drop:.1f}pt 低下）" if drop >= STEP_POINTS else f"緩（{drop:.1f}pt 低下）"
    tested = " / ".join(row["name"] for row in rows)
    print("### 限界点（clean 基準、閾値 " + f"{degrade_points:.1f}pt)" )
    print("")
    print("| run | clean 実測 | 試した範囲 | 低下なしの最大悪化 | 最初に落ちた点 | 落ち方 |")
    print("| --- | ---: | --- | --- | --- | --- |")
    print(f"| {run['report'].name} | {100 * baseline:.1f}% | {tested} | {worst_ok} | {first_bad} | {shape} |")
    print("")
    return {"condition": run["condition"], "report": run["report"], "baseline": baseline,
            "worst_ok": worst_ok, "first_bad": first_bad, "shape": shape, "rows": rows}


def print_combined(runs, summaries, degrade_points):
    print("## 反復 run の横断（同じレベル名）")
    print("")
    names = [row["name"] for row in runs[0]["rows"]]
    header = "| レベル | " + " | ".join(f"{run['report'].name} clean% (Δ) / decode%" for run in runs) + " | 判定 |"
    print(header)
    print("| --- | " + " | ".join("---" for _ in runs) + " | --- |")
    for name in names:
        cells = []
        dropped = 0
        for run, summary in zip(runs, summaries):
            row = next((item for item in run["rows"] if item["name"] == name), None)
            if row is None:
                cells.append("-")
                continue
            delta = (row["clean_rate"] - summary["baseline"]) * 100.0
            dropped_here = name != "clean" and (summary["baseline"] - row["clean_rate"]) * 100.0 > degrade_points
            if dropped_here:
                dropped += 1
            cells.append(f"{row['clean_rate'] * 100:.1f} ({delta:+.1f}pt) / {row['decode_rate'] * 100:.1f}")
        verdict = "" if name == "clean" else ("両 run" if dropped == len(runs) else ("片 run" if dropped else ""))
        print(f"| {name} | " + " | ".join(cells) + f" | {verdict} |")
    print("")
    print("## run ごとの限界点")
    print("")
    print("| run | clean 実測 | 低下なしの最大悪化 | 最初に落ちた点 | 落ち方 |")
    print("| --- | ---: | --- | --- | --- |")
    for summary in summaries:
        print(f"| {summary['report'].name} | {100 * summary['baseline']:.1f}% | {summary['worst_ok']} | "
              f"{summary['first_bad']} | {summary['shape']} |")
    print("")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", nargs="+", type=Path)
    parser.add_argument("--degrade-points", type=float, default=DEFAULT_DEGRADE_POINTS,
                        help="clean 基準からの低下がこのポイント数を超えたら「落ちた」（既定 0.5）")
    args = parser.parse_args()

    runs = [analyze_report(report) for report in args.report]
    summaries = [print_report(run, args.degrade_points) for run in runs]
    if len(runs) > 1:
        print_combined(runs, summaries, args.degrade_points)
    print("注: decode% は LTC フレームの受信率、clean% は「受信かつ直前から 1 フレーム分進んだ値」の率。")
    print("Δclean は同じ run の clean 実測からの差（ポイント）。境界の 1 フレームは clean 実測に含まれる。")
    print("跳びは値の化けの代理指標（trace には生の HH:MM:SS:FF が無い）。")


if __name__ == "__main__":
    main()
