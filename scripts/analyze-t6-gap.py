#!/usr/bin/env python3
"""T6: 「アプリが報告する再生位置」と「画面に出ている絵」のギャップを B1〜B4 に分解する。

2026-09-16 追記版。shim の gst.position（flags bit 3）が入ったログで、B1 を
「同じ時点の position と最新 delivery PTS」から直接出す。

  B1 = gst.position.value(µs) − そのイベントの ptsNs（最新 delivery PTS）。
       gst.position は sync.evaluate の直前に呼ばれた GetTimePos と対応付ける。
  B2 = gst.delivery（同一ソース seq の到着）→ compose.acquire
  B3 = compose.acquire → compose.publish（同一 scheduledQpc の合成）
  B4 = compose.publish → accuracy sample の frameTicks（完全一致結合。新ログでは 0）

符号の定義:
  signedErrorMs = 表示中の絵の PTS − LTC が期待する素材位置（負 = 絵が遅れ）
  delta         = LTC − playback（正 = 再生位置が遅れ）
  絵の遅れ       = signedErrorMs + delta（負 = 絵が報告位置より遅れ）
  −(signedError + delta) − 合計 は、報告位置から絵までの遅れが B1〜B4 の内訳で
  閉じているかを見る列（0 に近いほど閉じている）。

標本は status=measured / excludedReason 空 / black-sweep 以外。中央値と p95 を
run ごと・フェーズ別・全 run 結合で出す。判定はしない。

使い方: python scripts/analyze-t6-gap.py --run DIR [--run DIR ...] [--output FILE]
"""
import argparse
import bisect
import csv
import json
import re
import statistics
from collections import Counter, defaultdict
from pathlib import Path

FREQ = 10_000_000
PLAYBACK_RE = re.compile(r"playback=(-?[0-9.]+)")
DELTA_RE = re.compile(r"delta=(-?[0-9.]+)")
PHASES = ("freeze-sweep", "seek-a", "seek-b", "seek-c", "seek-back")


def fnum(row, key):
    value = row.get(key)
    if value in (None, ""):
        return None
    return float(value)


def picture_delay(record):
    """絵の遅れ（signedErrorMs + delta）。負 = 絵が報告位置より遅れ。"""
    if record["signed_ms"] is None or record["delta_ms"] is None:
        return None
    return record["signed_ms"] + record["delta_ms"]


def closure_ref(record):
    """−(絵の遅れ) − 合計。B1〜B4 の内訳で閉じているかを見る列。"""
    delay = picture_delay(record)
    if delay is None or record["sum"] is None:
        return None
    return -delay - record["sum"]


def load_jsonl(path):
    with Path(path).open("r", encoding="utf-8-sig") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def load_events(path):
    stages = defaultdict(list)
    for event in load_jsonl(path):
        stages[event["stage"]].append(event)
    for events in stages.values():
        events.sort(key=lambda e: e["qpc"])
    return stages


def percentile(values, p):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1)))))]


def sample_rows(path):
    with Path(path).open("r", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    return [r for r in rows
            if r["status"] == "measured" and r["excludedReason"] in ("", None)
            and r["phase"] != "black-sweep" and fnum(r, "signedErrorMs") is not None]


def build_records(run):
    stages = load_events(run / "events.jsonl")
    positions = stages["gst.position"]
    evaluates = stages["sync.evaluate"]
    deliveries = stages["gst.delivery"]
    publishes = stages["compose.publish"]
    acquire_events = stages["compose.acquire"]

    publish_by_qpc = {}
    for event in publishes:
        publish_by_qpc.setdefault(event["qpc"], event)

    groups = defaultdict(list)
    for stage in ("compose.acquire", "compose.start", "compose.complete"):
        for event in stages[stage]:
            if event.get("scheduledQpc"):
                groups[event["scheduledQpc"]].append(event)

    new_source_qpc = set()
    previous_ready = None
    for event in acquire_events:
        if event.get("detail") != "Ready":
            continue
        if previous_ready is None or event.get("imageId") != previous_ready.get("imageId"):
            new_source_qpc.add(event["qpc"])
        previous_ready = event

    delivery_qpc = [e["qpc"] for e in deliveries]

    def match_delivery(image_id, before_qpc):
        limit = bisect.bisect_right(delivery_qpc, before_qpc)
        for index in range(limit - 1, max(-1, limit - 40), -1):
            if deliveries[index].get("imageId") == image_id:
                return deliveries[index]
        return None

    evaluate_qpc = [e["qpc"] for e in evaluates]

    def pair_evaluate(ticks):
        """LTC 受信直後にディスパッチャで走った最初の評価を対応付ける。

        sync.evaluate の value は Continue モードではトラック内メディア位置なので、
        標本の receivedSeconds（タイムライン秒）とは比較しない。受信 tick の直後
        （-1ms〜+200ms）で最初のイベントを使う。"""
        start = bisect.bisect_left(evaluate_qpc, ticks - FREQ // 1000)
        for index in range(start, len(evaluates)):
            event = evaluates[index]
            if event["qpc"] > ticks + FREQ // 5:
                break
            return event
        return None

    position_qpc = [e["qpc"] for e in positions]

    def pair_position(evaluate):
        playback_match = PLAYBACK_RE.search(evaluate.get("detail") or "")
        playback = float(playback_match.group(1)) if playback_match else None
        index = bisect.bisect_right(position_qpc, evaluate["qpc"]) - 1
        if index < 0:
            return None
        for i in range(index, max(-1, index - 50), -1):
            position = positions[i]
            if evaluate["qpc"] - position["qpc"] > FREQ // 5:
                return None
            if playback is None or abs(position.get("value", 0) / 1e6 - playback) <= 0.002:
                return position
        return None

    records = []
    misses = Counter()
    for row in sample_rows(run / "analysis" / "accuracy-samples.csv"):
        frame_ticks = fnum(row, "frameTicks")
        if frame_ticks is None:
            misses["frameTicks"] += 1
            continue
        publish = publish_by_qpc.get(int(frame_ticks))
        if publish is None:
            misses["publish"] += 1
            continue
        acquire = None
        for group_event in groups.get(publish.get("scheduledQpc"), []):
            if group_event["stage"] == "compose.acquire":
                acquire = group_event
                break
        b2 = b3 = None
        if acquire is None:
            misses["acquire"] += 1
        else:
            acquire_qpc = acquire["qpc"]
            b3 = (publish["qpc"] - acquire_qpc) * 1000 / FREQ
            if acquire.get("detail") == "Ready" and acquire_qpc in new_source_qpc:
                delivery = match_delivery(acquire.get("imageId"), acquire_qpc)
                if delivery is not None:
                    b2 = (acquire_qpc - delivery["qpc"]) * 1000 / FREQ
                else:
                    misses["delivery"] += 1
            else:
                misses["acquire_not_new"] += 1

        b1 = None
        delta_ms = None
        evaluate = pair_evaluate(int(row["ticks"]))
        if evaluate is None:
            misses["evaluate"] += 1
        else:
            delta_match = DELTA_RE.search(evaluate.get("detail") or "")
            if delta_match:
                delta_ms = float(delta_match.group(1)) * 1000.0
            position = pair_position(evaluate)
            if position is not None and position.get("ptsNs", 0) > 0:
                b1 = (position["value"] * 1000 - position["ptsNs"]) / 1e6
            else:
                misses["position"] += 1

        b4 = (frame_ticks - publish["qpc"]) * 1000 / FREQ
        stages_ms = [b1, b2, b3, b4]
        records.append({
            "phase": row["phase"],
            "b1": b1,
            "b2": b2,
            "b3": b3,
            "b4": b4,
            "sum": sum(stages_ms) if all(v is not None for v in stages_ms) else None,
            "delta_ms": delta_ms,
            "signed_ms": fnum(row, "signedErrorMs"),
        })
    stage_counts = {name: len(stages[name]) for name in
                    ("gst.position", "gst.delivery", "compose.acquire", "compose.publish", "sync.evaluate")}
    return records, misses, stage_counts


def fmt(values):
    clean = [v for v in values if v is not None]
    if not clean:
        return "-"
    median = statistics.median(clean)
    p95 = percentile(clean, 95)
    return f"{median:.1f} / {p95:.1f} (n={len(clean)})"


def describe(values):
    clean = [v for v in values if v is not None]
    if not clean:
        return "-"
    return (f"中央値 {statistics.median(clean):.1f} / p5 {percentile(clean, 5):.1f} / "
            f"p95 {percentile(clean, 95):.1f} / 平均 {statistics.fmean(clean):.1f} (n={len(clean)})")


def median_p5_p95(values):
    clean = [v for v in values if v is not None]
    if not clean:
        return "-"
    return (f"{statistics.median(clean):.1f} / {percentile(clean, 5):.1f} / "
            f"{percentile(clean, 95):.1f} (n={len(clean)})")


def pooled_spread(values):
    clean = [v for v in values if v is not None]
    if not clean:
        return "-"
    return f"{percentile(clean, 95) - percentile(clean, 5):.1f}"


def median_str(values):
    clean = [v for v in values if v is not None]
    return "-" if not clean else f"{statistics.median(clean):.1f}"


def mean_of(values):
    clean = [v for v in values if v is not None]
    return statistics.fmean(clean) if clean else None


def report_run(run, records, misses, stage_counts):
    lines = [f"## {run.name}", "",
             "- events: " + ", ".join(f"{k}={v}" for k, v in stage_counts.items()),
             f"- 標本 n={len(records)} / 突き合わせ欠落: " +
             (", ".join(f"{k}={v}" for k, v in sorted(misses.items())) or "なし"),
             "",
             "| 段 | 中央値 / p95 ms |", "| --- | ---: |",
             f"| B1 position−最新delivery PTS | {fmt([r['b1'] for r in records])} |",
             f"| B2 delivery→acquire | {fmt([r['b2'] for r in records])} |",
             f"| B3 acquire→publish | {fmt([r['b3'] for r in records])} |",
             f"| B4 publish→frameTicks | {fmt([r['b4'] for r in records])} |",
             f"| **合計 B1+B2+B3+B4** | {fmt([r['sum'] for r in records])} |",
             f"| 参考 delta（ltc−playback） | {fmt([r['delta_ms'] for r in records])} |",
             f"| 参考 signedErrorMs | {fmt([r['signed_ms'] for r in records])} |",
             f"| 参考 絵の遅れ（signedError+delta） | {fmt([picture_delay(r) for r in records])} |",
             f"| 参考 −(絵の遅れ)−合計 | {fmt([closure_ref(r) for r in records])} |",
             "",
             f"- 絵の遅れ（signedError+delta）: {describe([picture_delay(r) for r in records])}",
             f"- −(絵の遅れ)−合計: {describe([closure_ref(r) for r in records])}",
             ""]
    return "\n".join(lines)


def report_combined(runs):
    lines = ["## 全 run 結合（run 名は --run の順）", "",
             "| phase | n | B1 | B2 | B3 | B4 | 合計 | delta | signedError | 絵の遅れ（signedError+delta） | −(絵の遅れ)−合計 |",
             "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for phase in PHASES:
        rows = [r for run in runs for r in run[1] if r["phase"] == phase]
        if not rows:
            continue
        lines.append(
            f"| {phase} | {len(rows)} | {fmt([r['b1'] for r in rows])} | {fmt([r['b2'] for r in rows])} | "
            f"{fmt([r['b3'] for r in rows])} | {fmt([r['b4'] for r in rows])} | "
            f"{fmt([r['sum'] for r in rows])} | {fmt([r['delta_ms'] for r in rows])} | "
            f"{fmt([r['signed_ms'] for r in rows])} | {fmt([picture_delay(r) for r in rows])} | "
            f"{fmt([closure_ref(r) for r in rows])} |")
    all_rows = [r for run in runs for r in run[1]]
    lines.append(
        f"| **全体** | {len(all_rows)} | {fmt([r['b1'] for r in all_rows])} | {fmt([r['b2'] for r in all_rows])} | "
        f"{fmt([r['b3'] for r in all_rows])} | {fmt([r['b4'] for r in all_rows])} | "
        f"{fmt([r['sum'] for r in all_rows])} | {fmt([r['delta_ms'] for r in all_rows])} | "
        f"{fmt([r['signed_ms'] for r in all_rows])} | {fmt([picture_delay(r) for r in all_rows])} | "
        f"{fmt([closure_ref(r) for r in all_rows])} |")
    lines.append("")
    mean_delta = mean_of([r["delta_ms"] for r in all_rows])
    mean_signed = mean_of([r["signed_ms"] for r in all_rows])
    mean_delay = mean_of([picture_delay(r) for r in all_rows])
    mean_closure = mean_of([closure_ref(r) for r in all_rows])
    if mean_delta is not None and mean_signed is not None:
        lines.append(f"- 平均: delta={mean_delta:+.1f}ms signedErrorMs={mean_signed:+.1f}ms "
                     f"絵の遅れ={mean_delay:+.1f}ms（n={len(all_rows)}）")
    if mean_closure is not None:
        lines.append(f"- 平均: −(絵の遅れ)−合計={mean_closure:+.1f}ms")
    lines.append(f"- 絵の遅れ（signedError+delta）: {describe([picture_delay(r) for r in all_rows])}")
    lines.append(f"- −(絵の遅れ)−合計: {describe([closure_ref(r) for r in all_rows])}")
    lines.append("")
    return "\n".join(lines)


def report_t7(runs):
    """T7 集計: delta / signedErrorMs の中央値・p5・p95、プール p95-p5、絵の遅れの中央値。"""
    lines = ["## T7 集計（フェーズ別・全 run 結合）", "",
             "| phase | n | delta 中央値 / p5 / p95 | signedError 中央値 / p5 / p95 | "
             "絵の遅れ 中央値 | signedError プール p95-p5 | delta プール p95-p5 |",
             "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    all_rows = [r for run in runs for r in run[1]]
    for phase in PHASES:
        rows = [r for r in all_rows if r["phase"] == phase]
        if not rows:
            continue
        lines.append(
            f"| {phase} | {len(rows)} | {median_p5_p95([r['delta_ms'] for r in rows])} | "
            f"{median_p5_p95([r['signed_ms'] for r in rows])} | "
            f"{median_str([picture_delay(r) for r in rows])} | "
            f"{pooled_spread([r['signed_ms'] for r in rows])} | {pooled_spread([r['delta_ms'] for r in rows])} |")
    lines.append(
        f"| **全体** | {len(all_rows)} | {median_p5_p95([r['delta_ms'] for r in all_rows])} | "
        f"{median_p5_p95([r['signed_ms'] for r in all_rows])} | "
        f"{median_str([picture_delay(r) for r in all_rows])} | "
        f"{pooled_spread([r['signed_ms'] for r in all_rows])} | {pooled_spread([r['delta_ms'] for r in all_rows])} |")
    lines.append("")
    lines.append("| run | n | delta 中央値 / p5 / p95 | signedError 中央値 / p5 / p95 | 絵の遅れ 中央値 |")
    lines.append("| --- | ---: | ---: | ---: | ---: |")
    for run_name, records in runs:
        lines.append(
            f"| {run_name.name} | {len(records)} | {median_p5_p95([r['delta_ms'] for r in records])} | "
            f"{median_p5_p95([r['signed_ms'] for r in records])} | "
            f"{median_str([picture_delay(r) for r in records])} |")
    lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    runs = []
    sections = []
    for run in args.run:
        records, misses, stage_counts = build_records(run)
        runs.append((run, records))
        sections.append(report_run(run, records, misses, stage_counts))
    sections.append(report_combined(runs))
    sections.append(report_t7(runs))
    text = "\n".join(sections)
    if args.output:
        args.output.write_text(text, encoding="utf-8")
        print("wrote", args.output)
    else:
        print(text)


if __name__ == "__main__":
    main()
