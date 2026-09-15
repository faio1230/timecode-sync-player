#!/usr/bin/env python3
"""T7: 補正ログの集計（クリップ別・フェーズ別の Smooth 行数、smooth-ineffective、補正シーク）。

アプリログ（Serilog）を run ごとの時間窓で切り出し、次を数える:
  - `Smooth correction rate=` の行数（クリップ別・フェーズ別）
  - smooth-ineffective（rate=1.00000 かつ |residualMs| > デッドバンド 20ms）
  - `Jump correction seek target=`（補正シーク）
  - `Gst loadfile ... elapsedMs=` の完了数と、切替ごとの完了欠落
フェーズ窓は harness.jsonl の phase-start / phase-end の timestampUtc から作る。

使い方: python scripts/analyze-t7-correction.py --app-log PATH --run DIR [--run DIR ...]
"""
import argparse
import json
import re
from datetime import datetime
from pathlib import Path

DEADBAND_MS = 20.0

LINE_RE = re.compile(
    r"^(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)\s+(?P<offset>[+-]\d{2}:\d{2})\s+(?P<body>.*)$")
SMOOTH_RE = re.compile(r"Smooth correction rate=([0-9.]+) residualMs=(-?[0-9.]+)")
JUMP_RE = re.compile(r"Jump correction seek target=(-?[0-9.]+)")
SWITCH_RE = re.compile(r"Continue mode: switching to track (\S+) at media position")
LOADFILE_RE = re.compile(r"Gst loadfile path=(?P<path>\S+) .* rc=(?P<rc>-?\d+) elapsedMs=(?P<ms>[0-9.]+)")


def parse_timestamp(value):
    """harness の 7 桁小数を含む ISO 時刻を fromisoformat で読める形にする。"""
    normalized = re.sub(r"\.(\d{6})\d+", r".\1", value)
    return datetime.fromisoformat(normalized)


def load_harness(run):
    with (run / "harness.jsonl").open(encoding="utf-8-sig") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def phase_windows(entries):
    windows = []
    current = None
    for entry in entries:
        timestamp = entry.get("timestampUtc")
        if not timestamp:
            continue
        when = parse_timestamp(timestamp)
        if entry.get("event") == "phase-start":
            current = [entry.get("details", {}).get("name", "?"), when, None]
        elif entry.get("event") == "phase-end" and current is not None:
            current[2] = when
            windows.append(tuple(current))
            current = None
    return windows


def read_log(path):
    lines = []
    with Path(path).open(encoding="utf-8-sig") as handle:
        for line in handle:
            match = LINE_RE.match(line.rstrip("\n"))
            if not match:
                continue
            try:
                when = datetime.fromisoformat(f"{match.group('ts')}{match.group('offset')}")
            except ValueError:
                continue
            lines.append((when, match.group("body")))
    return lines


def count_run(run, log_lines):
    entries = load_harness(run)
    stamps = [parse_timestamp(e["timestampUtc"]) for e in entries if e.get("timestampUtc")]
    if not stamps:
        return None
    start, end = min(stamps), max(stamps)
    windows = phase_windows(entries)

    in_run = [(when, body) for when, body in log_lines
              if start.timestamp() - 1 <= when.timestamp() <= end.timestamp() + 1]

    def phase_of(when):
        for name, phase_start, phase_end in windows:
            if phase_start is not None and phase_end is not None and \
                    phase_start.timestamp() - 0.5 <= when.timestamp() <= phase_end.timestamp() + 0.5:
                return name
        return "(pre-phase)"

    clip = "(initial)"
    initial_clip = None
    by_clip = {}
    by_phase = {}
    switches = []
    loadfiles = []

    def bump(table, key, field):
        row = table.setdefault(key, {"smooth": 0, "ineffective": 0, "jump": 0})
        row[field] += 1

    for when, body in in_run:
        switch = SWITCH_RE.search(body)
        if switch:
            clip = switch.group(1)
            switches.append(when)
            continue
        loadfile = LOADFILE_RE.search(body)
        if loadfile:
            if initial_clip is None:
                initial_clip = Path(loadfile.group("path")).stem
                if clip == "(initial)":
                    clip = initial_clip
            loadfiles.append((when, int(loadfile.group("rc"))))
            continue
        smooth = SMOOTH_RE.search(body)
        if smooth:
            rate = float(smooth.group(1))
            residual = float(smooth.group(2))
            bump(by_clip, clip, "smooth")
            bump(by_phase, phase_of(when), "smooth")
            if abs(rate - 1.0) < 1e-9 and abs(residual) > DEADBAND_MS:
                bump(by_clip, clip, "ineffective")
                bump(by_phase, phase_of(when), "ineffective")
            continue
        if JUMP_RE.search(body):
            bump(by_clip, clip, "jump")
            bump(by_phase, phase_of(when), "jump")

    missing = []
    for index, switch in enumerate(switches):
        following = [when for when, _ in loadfiles
                     if when >= switch and (index + 1 >= len(switches) or when < switches[index + 1])]
        if not following:
            missing.append(switch)

    return {
        "name": run.name,
        "window": (start, end),
        "by_clip": by_clip,
        "by_phase": by_phase,
        "switches": len(switches),
        "loadfiles": len(loadfiles),
        "loadfile_failures": sum(1 for _, rc in loadfiles if rc != 0),
        "missing_load_after_switch": missing,
    }


def report(result):
    lines = [f"## {result['name']}", "",
             f"- run 窓: {result['window'][0]} 〜 {result['window'][1]}",
             f"- 切替 {result['switches']} 件 / load 完了 {result['loadfiles']} 件"
             f"（rc!=0 {result['loadfile_failures']} 件）",
             f"- 切替後に load 完了が出ていない件数: {len(result['missing_load_after_switch'])}",
             "",
             "| clip | Smooth correction 行 | smooth-ineffective（推定） | Jump 補正シーク |",
             "| --- | ---: | ---: | ---: |"]
    for clip, row in result["by_clip"].items():
        lines.append(f"| {clip} | {row['smooth']} | {row['ineffective']} | {row['jump']} |")
    lines.append("")
    lines.append("| phase | Smooth correction 行 | smooth-ineffective（推定） | Jump 補正シーク |")
    lines.append("| --- | ---: | ---: | ---: |")
    for phase, row in result["by_phase"].items():
        lines.append(f"| {phase} | {row['smooth']} | {row['ineffective']} | {row['jump']} |")
    lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-log", required=True, type=Path)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    log_lines = read_log(args.app_log)
    sections = [report(count_run(run, log_lines)) for run in args.run]
    text = "\n".join(sections)
    if args.output:
        args.output.write_text(text, encoding="utf-8")
        print("wrote", args.output)
    else:
        print(text)


if __name__ == "__main__":
    main()
