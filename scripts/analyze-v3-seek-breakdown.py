#!/usr/bin/env python3
"""V3 時系列: events.jsonl + phases.jsonl から 1 操作 = 1 行の表を作る。

行の列はすべて同じ QPC 時計:
  a  seek.decide   (SyncDecisionEngine が Seek を決めた時刻。同値 decide の先頭。load 行は無し)
  b  seek.issue / seek.return  または load.issue / load.return
  c  gst.delivery / mpv.frame (新位置の最初のフレーム到着。gst は pts も)
  d  compose.publish
  e  present.scanout (このハーネスは全画面表示を開かないため 0 件)

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

KNOWN_PHASES = ("black-sweep", "freeze-sweep", "seek-a", "seek-b", "seek-c", "seek-back")
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
ROW_KEYS = (
    "player", "run", "phase", "phase_group", "kind", "target_s", "frame_pts_s",
    "decide_first_ms", "decide_last_ms", "issue_ms", "return_ms", "frame_ms",
    "publish_ms", "scanout_ms",
) + tuple("d_" + key + "_ms" for key in DELTA_KEYS)


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


def gst_pts_seconds(event):
    head = (event.get("detail") or "").split(":", 1)[0]
    try:
        return int(head) / 1e9
    except ValueError:
        return None


def match_decide(decides, issues, issue):
    """seek.issue に対応する decide エピソード（先頭と直前）を返す。値（target µs）一致で追う。"""
    target = issue.get("value")
    previous_issue_qpc = max((i["qpc"] for i in issues if i["qpc"] < issue["qpc"]), default=None)
    candidates = [d for d in decides
                  if d.get("value") == target and d["qpc"] <= issue["qpc"]
                  and (previous_issue_qpc is None or d["qpc"] > previous_issue_qpc)]
    if not candidates:
        return None, None
    return candidates[0], candidates[-1]


def first_after(events, stage, qpc, predicate=None, end_qpc=None):
    for event in events:
        if event.get("stage") != stage or event["qpc"] < qpc:
            continue
        if end_qpc is not None and event["qpc"] >= end_qpc:
            return None
        if predicate is None or predicate(event):
            return event
    return None


def first_gst_frame(deliveries, issue_qpc, end_qpc, target_s):
    """新位置の最初の配信を pts で同定する。preroll（pts=0）や旧位置のフレームを拾わない。"""
    for event in deliveries:
        if event["qpc"] <= issue_qpc:
            continue
        if end_qpc is not None and event["qpc"] >= end_qpc:
            return None
        pts = gst_pts_seconds(event)
        if pts is None:
            continue
        if target_s is not None and target_s > 0.05:
            if pts >= target_s - 0.2:
                return event
        elif pts >= -0.05:
            return event
    return None


def phase_lookup(phases):
    def find(qpc):
        for phase in phases:
            if (phase["start"] is not None and phase["end"] is not None
                    and phase["start"] <= qpc <= phase["end"]):
                return phase
        return None
    return find


def build_rows(player, run_name, phases, frequency, events):
    decides = sorted([e for e in events if e["stage"] == "seek.decide"], key=lambda e: e["qpc"])
    seeks = sorted([e for e in events if e["stage"] == "seek.issue"], key=lambda e: e["qpc"])
    loads = sorted([e for e in events if e["stage"] == "load.issue"], key=lambda e: e["qpc"])
    returns = sorted([e for e in events if e["stage"] in ("seek.return", "load.return")], key=lambda e: e["qpc"])
    deliveries = sorted([e for e in events if e["stage"] == "gst.delivery"], key=lambda e: e["qpc"])
    mpv_frames = sorted([e for e in events if e["stage"] == "mpv.frame"], key=lambda e: e["qpc"])
    publishes = sorted([e for e in events if e["stage"] == "compose.publish"], key=lambda e: e["qpc"])
    scanouts = sorted([e for e in events if e["stage"] == "present.scanout"], key=lambda e: e["qpc"])

    find_phase = phase_lookup(phases)
    operations = [("seek", e) for e in seeks] + [("load", e) for e in loads]
    operations.sort(key=lambda item: item[1]["qpc"])
    rows = []

    for index, (kind, issue) in enumerate(operations):
        # 次の操作（seek/load）までをこの操作の測定区間とする。それ以降のフレームは次の操作のもの。
        end_qpc = operations[index + 1][1]["qpc"] if index + 1 < len(operations) else None
        phase = find_phase(issue["qpc"])
        phase_name = phase["name"] if phase else "(outside)"
        qpc_ref = phase["start"] if phase else issue["qpc"]

        decide_first = decide_last = None
        if kind == "seek":
            decide_first, decide_last = match_decide(decides, seeks, issue)
        target_us = issue.get("value")
        target_s = target_us / 1e6 if target_us is not None and target_us >= 0 else None

        returning = next((r for r in returns if r["qpc"] >= issue["qpc"]), None)

        frame = None
        frame_pts_s = None
        if player == "gst":
            frame = first_gst_frame(deliveries, issue["qpc"], end_qpc, target_s)
            if frame is not None:
                frame_pts_s = gst_pts_seconds(frame)
        else:
            if target_us is not None and target_us >= 0:
                frame = first_after(mpv_frames, "mpv.frame", issue["qpc"],
                                    lambda e: e.get("value") is not None and e["value"] >= target_us - 150_000,
                                    end_qpc=end_qpc)

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
            "phase_group": "resync" if phase_name.startswith("resync-") else phase_name,
            "kind": kind,
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


def aggregate(rows):
    result = {"count": len(rows)}
    for key in DELTA_KEYS:
        values = [r["d_" + key + "_ms"] for r in rows if r["d_" + key + "_ms"] is not None]
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


def aggregate_cell(stats, key):
    value = stats[key]["p95"]
    median = stats[key]["median"]
    count = stats[key]["n"]
    return "-" if value is None else f"{format_ms(median)} / {format_ms(value)} (n={count})"


def ordered_phase_names(all_rows):
    names = {row["phase_group"] for row in all_rows}
    ordered = [name for name in KNOWN_PHASES if name in names]
    for row in all_rows:
        if row["phase_group"] not in ordered:
            ordered.append(row["phase_group"])
    return ordered


def seeking_evidence(events, frequency):
    samples = [e for e in events if e["stage"] == "player.seeking"]
    if not samples:
        return "player.seeking: 0 件（IsNativeSeeking が一度も呼ばれていない）\n"
    lines = ["| raw | result | count | first qpc | last qpc |", "| --- | --- | ---: | ---: | ---: |"]
    groups = {}
    for event in samples:
        raw = (event.get("detail") or "").replace("raw=", "", 1)
        result = bool(event.get("value"))
        key = (raw, result)
        group = groups.setdefault(key, {"count": 0, "first": event["qpc"], "last": event["qpc"]})
        group["count"] += 1
        group["last"] = event["qpc"]
    for (raw, result), group in sorted(groups.items(), key=lambda item: -item[1]["count"]):
        lines.append(f"| `{raw}` | {result} | {group['count']} | {group['first']} | {group['last']} |")
    lines.append("")
    lines.append("変化点（先頭 20 件）:")
    lines.append("")
    lines.append("| qpc | raw | result |")
    lines.append("| ---: | --- | --- |")
    previous = None
    shown = 0
    for event in samples:
        raw = (event.get("detail") or "").replace("raw=", "", 1)
        result = bool(event.get("value"))
        if (raw, result) == previous:
            continue
        previous = (raw, result)
        lines.append(f"| {event['qpc']} | `{raw}` | {result} |")
        shown += 1
        if shown >= 20:
            break
    lines.append("")
    return "\n".join(lines)


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
    all_seeking = []
    for run in args.run:
        run_dir = Path(run)
        phases, frequency = load_phases(run_dir / "phases.jsonl")
        events = load_events(run_dir / "events.jsonl")
        if not frequency:
            frequency = 10_000_000
        rows = build_rows(args.player, run_dir.name, phases, frequency, events)
        all_rows.extend(rows)
        all_seeking.extend(e for e in events if e["stage"] == "player.seeking")
        run_sections.append((run_dir.name, rows))

        csv_path = out_dir / f"seek-rows-{run_dir.name}.csv"
        with csv_path.open("w", encoding="utf-8", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(ROW_KEYS))
            writer.writeheader()
            writer.writerows(rows)

    lines = [f"# V3 時系列 ({args.player})", ""]
    lines.append("## 区間ごとの差（ms）")
    lines.append("")
    lines.append("| phase | kind | n | a→b | a→b(last) | b→b' | b'→c | b→c | c→d | d→e | a→e |")
    lines.append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for phase in ordered_phase_names(all_rows):
        for kind in ("seek", "load"):
            subset = [r for r in all_rows if r["phase_group"] == phase and r["kind"] == kind]
            if not subset:
                continue
            stats = aggregate(subset)
            lines.append(
                f"| {phase} | {kind} | {stats['count']} | {aggregate_cell(stats, 'decide_issue')} | "
                f"{aggregate_cell(stats, 'decideLast_issue')} | {aggregate_cell(stats, 'issue_return')} | "
                f"{aggregate_cell(stats, 'return_frame')} | {aggregate_cell(stats, 'issue_frame')} | "
                f"{aggregate_cell(stats, 'frame_publish')} | {aggregate_cell(stats, 'publish_scanout')} | "
                f"{aggregate_cell(stats, 'decide_scanout')} |")
    for label, predicate in (("ALL seeks", lambda r: r["kind"] == "seek"),
                             ("ALL loads", lambda r: r["kind"] == "load"),
                             ("seek phases only", lambda r: r["kind"] == "seek" and r["phase"] in SEEK_PHASES),
                             ("gap phases only", lambda r: r["kind"] == "seek" and r["phase"] in GAP_PHASES)):
        subset = [r for r in all_rows if predicate(r)]
        if not subset:
            continue
        stats = aggregate(subset)
        lines.append(
            f"| {label} | - | {stats['count']} | {aggregate_cell(stats, 'decide_issue')} | "
            f"{aggregate_cell(stats, 'decideLast_issue')} | {aggregate_cell(stats, 'issue_return')} | "
            f"{aggregate_cell(stats, 'return_frame')} | {aggregate_cell(stats, 'issue_frame')} | "
            f"{aggregate_cell(stats, 'frame_publish')} | {aggregate_cell(stats, 'publish_scanout')} | "
            f"{aggregate_cell(stats, 'decide_scanout')} |")
    lines.append("")
    lines.append("各セルは中央値 / p95。a→b は同値 decide の先頭、a→b(last) は issue 直前の decide。")
    lines.append("present.scanout（e）は全画面表示を開かない計測のため 0 件。")
    lines.append("")
    lines.append("## player.seeking 証跡（IsNativeSeeking の戻り値と生値）")
    lines.append("")
    lines.append(seeking_evidence(all_seeking, None))
    for name, rows in run_sections:
        lines.append(f"## run {name}")
        lines.append("")
        lines.append("| # | kind | phase | target s | frame pts s | a first | a last | b issue | b return | c frame | d publish | a→b | a→b(last) | b→b' | b'→c | b→c | c→d | d→e | a→e |")
        lines.append("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
        for index, row in enumerate(rows, start=1):
            cells = [
                str(index), row["kind"], row["phase"],
                "-" if row["target_s"] is None else f"{row['target_s']:.3f}",
                "-" if row["frame_pts_s"] is None else f"{row['frame_pts_s']:.3f}",
                format_ms(row["decide_first_ms"]), format_ms(row["decide_last_ms"]),
                format_ms(row["issue_ms"]), format_ms(row["return_ms"]), format_ms(row["frame_ms"]),
                format_ms(row["publish_ms"]), format_ms(row["d_decide_issue_ms"]),
                format_ms(row["d_decideLast_issue_ms"]), format_ms(row["d_issue_return_ms"]),
                format_ms(row["d_return_frame_ms"]), format_ms(row["d_issue_frame_ms"]),
                format_ms(row["d_frame_publish_ms"]), format_ms(row["d_publish_scanout_ms"]),
                format_ms(row["d_decide_scanout_ms"]),
            ]
            lines.append("| " + " | ".join(cells) + " |")
        lines.append("")
    lines.append("timestamp 列はフェーズ開始からの QPC ms。差分は QPC の差。")
    (out_dir / f"seek-breakdown-{args.player}.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"{args.player}: runs={len(run_sections)} operations={len(all_rows)} out={out_dir}")


if __name__ == "__main__":
    main()
