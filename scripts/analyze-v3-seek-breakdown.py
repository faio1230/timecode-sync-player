#!/usr/bin/env python3
"""V3 シーク時系列: events.jsonl + phases.jsonl から 1 シーク = 1 行の表を作る。

行の列はすべて同じ QPC 時計:
  a  seek.decide   (SyncDecisionEngine が Seek を決めた時刻。同値 decide の先頭)
  b  seek.issue / seek.return (プレイヤーへの seek 発行と呼び出し復帰)
  c  gst.delivery / mpv.frame (新位置の最初のフレーム到着。gst は pts も)
  d  compose.publish
  e  present.scanout

使い方:
  python scripts/analyze-v3-seek-breakdown.py --player gst --run DIR [--run DIR ...] --out DIR
DIR は TIMECODE_ACCURACY_REPORT_DIR 兼 TIMECODE_SYNC_PLAYER_OUTPUT_TRACE の run ディレクトリ。
"""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path

PHASE_ORDER = ("black-sweep", "freeze-sweep", "seek-a", "seek-b", "seek-c", "seek-back")
SEEK_PHASES = ("seek-a", "seek-b", "seek-c", "seek-back")
GAP_PHASES = ("black-sweep", "freeze-sweep")
DELTA_KEYS = (
    "decide_issue", "decideLast_issue", "issue_return", "return_frame",
    "issue_frame", "frame_publish", "publish_scanout", "decide_scanout",
)
PAIR_KEYS = (
    ("decide_first", "issue", "decide_issue"),
    ("decide_last", "issue", "decideLast_issue"),
    ("issue", "return", "issue_return"),
    ("return", "frame", "return_frame"),
    ("issue", "frame", "issue_frame"),
    ("frame", "publish", "frame_publish"),
    ("publish", "scanout", "publish_scanout"),
    ("decide_first", "scanout", "decide_scanout"),
)


def load_jsonl(path):
    rows = []
    with Path(path).open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def load_events(path):
    return [e for e in load_jsonl(path) if e.get("stage")]


def load_phases(path):
    rows = load_jsonl(path)
    frequency = next((r.get("frequency") for r in rows if r.get("frequency")), None)
    starts = [r for r in rows if r.get("type") == "phase-start"]
    ends = [r for r in rows if r.get("type") == "phase-end"]
    phases = []
    for index, start in enumerate(starts):
        end = ends[index] if index < len(ends) else None
        phases.append({
            "name": start.get("name"),
            "start": start.get("ticks"),
            "end": (end or {}).get("ticks"),
        })
    return phases, frequency


def detail_value(event, key):
    text = event.get("detail") or ""
    for part in text.split():
        if part.startswith(key + "="):
            try:
                return float(part.split("=", 1)[1])
            except ValueError:
                return None
    return None


def gst_pts_seconds(event):
    text = event.get("detail") or ""
    head = text.split(":", 1)[0]
    try:
        return int(head) / 1e9
    except ValueError:
        return None


def match_decide(decides, issues, issue):
    """issue に対応する decide エピソード（先頭と直前）を返す。値（target µs）一致で追う。"""
    target = issue.get("value")
    previous_issue_qpc = max((i["qpc"] for i in issues if i["qpc"] < issue["qpc"]), default=None)
    candidates = [d for d in decides
                  if d.get("value") == target and d["qpc"] <= issue["qpc"]
                  and (previous_issue_qpc is None or d["qpc"] > previous_issue_qpc)]
    if not candidates:
        return None, None
    return candidates[0], candidates[-1]


def first_after(events, stage, qpc, predicate=None):
    for event in events:
        if event.get("stage") != stage or event["qpc"] < qpc:
            continue
        if predicate is None or predicate(event):
            return event
    return None


def build_rows(player, run_name, phases, frequency, events):
    decides = sorted([e for e in events if e["stage"] == "seek.decide"], key=lambda e: e["qpc"])
    issues = sorted([e for e in events if e["stage"] == "seek.issue"], key=lambda e: e["qpc"])
    returns = sorted([e for e in events if e["stage"] == "seek.return"], key=lambda e: e["qpc"])
    deliveries = sorted([e for e in events if e["stage"] == "gst.delivery"], key=lambda e: e["qpc"])
    mpv_frames = sorted([e for e in events if e["stage"] == "mpv.frame"], key=lambda e: e["qpc"])
    publishes = sorted([e for e in events if e["stage"] == "compose.publish"], key=lambda e: e["qpc"])
    scanouts = sorted([e for e in events if e["stage"] == "present.scanout"], key=lambda e: e["qpc"])

    rows = []
    for issue in issues:
        phase = next((p for p in phases
                      if p["start"] is not None and p["end"] is not None
                      and p["start"] <= issue["qpc"] <= p["end"]), None)
        phase_name = phase["name"] if phase else "(outside)"
        qpc_ref = phase["start"] if phase else issue["qpc"]

        decide_first, decide_last = match_decide(decides, issues, issue)
        target_us = issue.get("value")
        target_s = target_us / 1e6 if target_us is not None else None

        returning = next((r for r in returns if r["qpc"] >= issue["qpc"]), None)

        frame = None
        frame_pts_s = None
        if player == "gst":
            frame = first_after(deliveries, "gst.delivery", issue["qpc"])
            if frame is not None:
                frame_pts_s = gst_pts_seconds(frame)
        else:
            if target_us is not None:
                frame = first_after(mpv_frames, "mpv.frame", issue["qpc"],
                                    lambda e: e.get("value") is not None and e["value"] >= target_us - 50_000)
            if frame is None:
                frame = first_after(mpv_frames, "mpv.frame", issue["qpc"])

        publish = first_after(publishes, "compose.publish", frame["qpc"]) if frame else None
        scanout = first_after(scanouts, "present.scanout", publish["qpc"]) if publish else None

        times = {
            "decide_first": decide_first["qpc"] if decide_first else None,
            "decide_last": decide_last["qpc"] if decide_last else None,
            "issue": issue["qpc"],
            "return": returning["qpc"] if returning else None,
            "frame": frame["qpc"] if frame else None,
            "publish": publish["qpc"] if publish else None,
            "scanout": scanout["qpc"] if scanout else None,
        }
        row = {
            "player": player,
            "run": run_name,
            "phase": phase_name,
            "target_s": round(target_s, 6) if target_s is not None else None,
            "frame_pts_s": round(frame_pts_s, 6) if frame_pts_s is not None else None,
        }
        for key, value in times.items():
            row[key + "_ms"] = round((value - qpc_ref) * 1000.0 / frequency, 3) if value is not None else None
        for start_key, end_key, delta_key in PAIR_KEYS:
            start, end = times[start_key], times[end_key]
            row["d_" + delta_key + "_ms"] = round((end - start) * 1000.0 / frequency, 3) \
                if start is not None and end is not None else None
        rows.append(row)

    return sorted(rows, key=lambda r: (r["issue_ms"] if r["issue_ms"] is not None else math.inf))


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def aggregate(rows, predicate):
    selected = [r for r in rows if predicate(r)]
    result = {"count": len(selected)}
    for key in DELTA_KEYS:
        values = [r["d_" + key + "_ms"] for r in selected if r["d_" + key + "_ms"] is not None]
        result[key] = {
            "n": len(values),
            "mean": statistics.fmean(values) if values else None,
            "median": statistics.median(values) if values else None,
            "p95": percentile(values, 0.95),
            "max": max(values) if values else None,
        }
    return result


def format_ms(value):
    return "-" if value is None else f"{value:.1f}"


def markdown_table(rows, aggregates):
    lines = []
    lines.append("| # | phase | target s | frame pts s | a first | a last | b issue | b return | c frame | d publish | e scanout | a→b | a→b(last) | b→b' | b'→c | b→c | c→d | d→e | a→e |")
    lines.append("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for index, row in enumerate(rows, start=1):
        cells = [
            str(index), row["phase"],
            "-" if row["target_s"] is None else f"{row['target_s']:.3f}",
            "-" if row["frame_pts_s"] is None else f"{row['frame_pts_s']:.3f}",
            format_ms(row["decide_first_ms"]), format_ms(row["decide_last_ms"]),
            format_ms(row["issue_ms"]), format_ms(row["return_ms"]), format_ms(row["frame_ms"]),
            format_ms(row["publish_ms"]), format_ms(row["scanout_ms"]),
            format_ms(row["d_decide_issue_ms"]), format_ms(row["d_decideLast_issue_ms"]),
            format_ms(row["d_issue_return_ms"]), format_ms(row["d_return_frame_ms"]),
            format_ms(row["d_issue_frame_ms"]), format_ms(row["d_frame_publish_ms"]),
            format_ms(row["d_publish_scanout_ms"]), format_ms(row["d_decide_scanout_ms"]),
        ]
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("")
    lines.append("timestamp 列はフェーズ開始からの QPC ms。差分は QPC の差。")
    lines.append("")
    return lines, aggregates


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--player", required=True, choices=("gst", "mpv"))
    parser.add_argument("--run", action="append", required=True, help="run ディレクトリ（複数可）")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    all_rows = []
    run_sections = []
    for run in args.run:
        run_dir = Path(run)
        phases, frequency = load_phases(run_dir / "phases.jsonl")
        events = load_events(run_dir / "events.jsonl")
        if not frequency:
            frequency = 10_000_000
        rows = build_rows(args.player, run_dir.name, phases, frequency, events)
        all_rows.extend(rows)
        run_sections.append((run_dir.name, rows, phases, frequency))

        csv_path = out_dir / f"seek-rows-{run_dir.name}.csv"
        with csv_path.open("w", encoding="utf-8", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()) if rows else
                                    ["player", "run", "phase", "target_s"])
            writer.writeheader()
            writer.writerows(rows)

    aggregate_rows = []
    for phase in PHASE_ORDER:
        aggregate_rows.append((phase, aggregate(all_rows, lambda r, p=phase: r["phase"] == p)))
    aggregate_rows.append(("ALL seeks", aggregate(all_rows, lambda r: r["phase"] in SEEK_PHASES)))
    aggregate_rows.append(("ALL gaps", aggregate(all_rows, lambda r: r["phase"] in GAP_PHASES)))

    lines = [f"# V3 シーク時系列 ({args.player})", ""]
    lines.append("## 区間ごとの差（ms）")
    lines.append("")
    lines.append("| phase | n | a→b | a→b(last) | b→b' | b'→c | b→c | c→d | d→e | a→e |")
    lines.append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for name, stats in aggregate_rows:
        def cell(key):
            value = stats[key]["p95"]
            median = stats[key]["median"]
            return "-" if value is None else f"{format_ms(median)} / {format_ms(value)}"
        lines.append(
            f"| {name} | {stats['count']} | {cell('decide_issue')} | {cell('decideLast_issue')} | "
            f"{cell('issue_return')} | {cell('return_frame')} | {cell('issue_frame')} | "
            f"{cell('frame_publish')} | {cell('publish_scanout')} | {cell('decide_scanout')} |")
    lines.append("")
    lines.append("各セルは中央値 / p95。a→b は同値 decide の先頭、a→b(last) は issue 直前の decide。")
    lines.append("")
    for name, rows, phases, frequency in run_sections:
        lines.append(f"## run {name}")
        lines.append("")
        table, _ = markdown_table(rows, None)
        lines.extend(table)
    (out_dir / f"seek-breakdown-{args.player}.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"{args.player}: runs={len(run_sections)} seeks={len(all_rows)} out={out_dir}")


if __name__ == "__main__":
    main()
