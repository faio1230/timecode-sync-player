#!/usr/bin/env python3
"""M6: ダーティー LTC の限界点をレベル窓ごとに集計する。

入力（run のレポートディレクトリ）:
  dirty-plan.json, fixture.json, phases.jsonl, trace.jsonl, events.jsonl（root か *\\events.jsonl）
出力（標準出力）:
  レベルごとの decode 成功率・値の跳び率・同期誤差分布・ギャップ・gate_rejected・seeks と、
  条件ごとの「成功率 100% の最大悪化レベル」「最初に落ちたレベル」「落ち方（急/緩）」。
判定はしない（数字のみ）。

使い方: python scripts/analyze-dirty-ltc.py <report-dir>
"""
import bisect
import json
import statistics
import sys
from pathlib import Path

FREQ_FALLBACK = 10_000_000
CLEAN_THRESHOLD = 0.999  # 「壊れていない」の数値基準（decode かつ値の跳びなし）
STEP_POINTS = 5.0        # 隣接レベルでこれ以上成功率が落ちたら「急」


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


def main():
    report = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".")
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
    ltc_ticks = [event["ticks"] for event in ltc_events]
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
    if not phases:
        print("dirty- phase-start がありません（M6 の run ではありません）")
        return

    print(f"trace: {report}")
    print(f"condition={plan.get('condition')} ltcFps={ltc_fps} levels={len(phases)}")
    print("")
    print("| レベル | 窓 s | 期待 | 受信 | decode% | 跳び | clean% | 誤差 mean | p5 | p95 | max | 未対応 | |err|>許容 | gap 回/max ms | gate_rejected Δ | seeks |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |")

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

        row = {
            "name": name,
            "decoded": decoded,
            "expected": expected,
            "decode_rate": decode_rate,
            "jumps": jumps,
            "clean_rate": clean_rate,
            "errors": errors,
            "unpaired": unpaired,
            "over_tolerance": over_tolerance,
            "gaps": gaps,
            "gate_delta": gate_delta,
            "seeks": seek_count,
        }
        rows.append(row)
        error_mean = statistics.fmean(errors) if errors else None
        fmt = lambda value: "-" if value is None else f"{value:+.2f}"
        gap_text = f"{len(gaps)} / {max(gaps):.1f}" if gaps else "0 / -"
        tolerance_pct = 100.0 * over_tolerance / len(errors) if errors else 0.0
        print(f"| {name} | {seconds:.0f} | {expected:.0f} | {decoded} | {100 * decode_rate:.1f} | {jumps} | "
              f"{100 * clean_rate:.1f} | {fmt(error_mean)} | {fmt(percentile(errors, 5))} | "
              f"{fmt(percentile(errors, 95))} | {fmt(max(errors) if errors else None)} | {unpaired} | "
              f"{tolerance_pct:.1f}% | {gap_text} | {gate_delta} | {seek_count} |")

    print("")
    print("## 限界点（clean% = decode かつ値の跳びなし。閾値 99.9%）")
    print("")
    print("| 条件 | 試した範囲 | clean 100% の最大悪化 | 最初に落ちた点 | 落ち方 |")
    print("| --- | --- | --- | --- | --- |")
    # plan の並びをレベルの悪化順としてそのまま使う（1 run = 1 条件）
    ok = [row for row in rows if row["clean_rate"] >= CLEAN_THRESHOLD]
    bad = [row for row in rows if row["clean_rate"] < CLEAN_THRESHOLD]
    tested = " / ".join(row["name"] for row in rows)
    if rows:
        worst_ok = ok[-1]["name"] if ok else "-"
        first_bad = bad[0]["name"] if bad else "なし"
        shape = "-"
        if bad:
            index = rows.index(bad[0])
            previous = rows[index - 1]["clean_rate"] if index > 0 else 1.0
            drop = (previous - bad[0]["clean_rate"]) * 100.0
            shape = f"急（{drop:.1f} ポイント低下）" if drop >= STEP_POINTS else f"緩（{drop:.1f} ポイント）"
        print(f"| {plan.get('condition')} | {tested} | {worst_ok} | {first_bad} | {shape} |")
    print("")
    print("注: decode% は LTC フレームの受信率、clean% は「受信かつ直前から 1 フレーム分進んだ値」の率。")
    print("跳びは値の化けの代理指標（trace には生の HH:MM:SS:FF が無い）。")


if __name__ == "__main__":
    main()
