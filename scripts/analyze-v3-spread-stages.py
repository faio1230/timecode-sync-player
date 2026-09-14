#!/usr/bin/env python3
"""V3: 定常誤差の大小サンプルで、出力トレースの各段階の所要時間がどう違うかを出す。

同じ run の accuracy-samples.csv（LTC 受信ごとの signedErrorMs）と events.jsonl
（OutputTrace の段階イベント）を QPC で突き合わせる。サンプルが使った公開フレーム
（CSV の frameTicks）を compose.publish に結び、その tick の段階イベントから
  デコード→配信 : gst.delivery / mpv.frame の到着間隔と pts 刻み
  配信→取得     : 到着イベント → compose.acquire
  取得→合成     : compose.acquire → compose.start → compose.complete
  合成→公開     : compose.complete → compose.publish
  公開→走査     : compose.publish → present.scanout（この計測は全画面を開かないため 0 件）
を測る。集計は |誤差| < p10 と > p90 の 2 群。さらに、誤差の区間間/区間内分散と、
フェーズ先頭（と freeze-sweep のギャップ出口）で「LTC 受信 → load 発行 → 新位置の
最初のフレーム公開」までの遅れ、およびその後の定常平均を出す。判定はしない。

使い方: python scripts/analyze-v3-spread-stages.py --run DIR [--run DIR ...]
"""
import argparse
import bisect
import csv
import json
import statistics
from collections import Counter, defaultdict
from pathlib import Path

FREQ = 10_000_000


def fnum(row, key):
    value = row.get(key)
    if value in (None, ""):
        return None
    return float(value)


def fraction(value):
    if value in (None, ""):
        return None
    if "/" not in value:
        return float(value)
    numerator, denominator = value.split("/", 1)
    return float(numerator) / float(denominator)


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int(round(fraction / 100 * (len(ordered) - 1)))))]


def ms(delta):
    return delta * 1000.0 / FREQ


def load_events(path):
    stages = defaultdict(list)
    with Path(path).open("r", encoding="utf-8-sig") as handle:
        for line in handle:
            event = json.loads(line)
            stages[event["stage"]].append(event)
    for events in stages.values():
        events.sort(key=lambda e: e["qpc"])
    return stages


def nearest_before(items, key, target, tolerance):
    index = bisect.bisect_right([i[key] for i in items], target) - 1
    if index < 0:
        return None
    if target - items[index][key] > tolerance:
        return None
    return items[index]


def sample_frame(accuracy_path):
    """Return steady measured samples: |err| defined, no excludedReason, not black-sweep."""
    with Path(accuracy_path).open("r", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    return [r for r in rows if r["status"] == "measured" and r["excludedReason"] in ("", None)
            and r["phase"] != "black-sweep" and fnum(r, "signedErrorMs") is not None]


def build_records(run):
    stages = load_events(run / "events.jsonl")
    settings = json.loads((run / "settings.json").read_text(encoding="utf-8-sig"))
    backend = "gst" if settings.get("backend") == 1 else "mpv"

    publishes = stages["compose.publish"]
    publish_qpc = [e["qpc"] for e in publishes]
    groups = defaultdict(dict)
    for stage, events in stages.items():
        for event in events:
            scheduled = event.get("scheduledQpc")
            if scheduled:
                groups[scheduled].setdefault(stage, []).append(event)

    delivery_stage = "gst.delivery" if backend == "gst" else "mpv.frame"
    deliveries = stages[delivery_stage]
    delivery_qpc = [e["qpc"] for e in deliveries]

    ready = [e for e in stages["compose.acquire"] if e.get("detail") == "Ready"]
    previous_ready = None
    new_after = {}
    last_ready_qpc = {}
    for event in stages["compose.acquire"]:
        if event.get("detail") != "Ready":
            new_after[event["qpc"]] = False
            continue
        if backend == "gst":
            new = previous_ready is None or event.get("imageId") != previous_ready.get("imageId")
        else:
            new = previous_ready is None or event.get("generatedQpc") != previous_ready.get("generatedQpc")
        new_after[event["qpc"]] = new
        previous_ready = event
        last_ready_qpc[event["qpc"]] = event

    records = []
    join_miss = 0
    for row in sample_frame(run / "analysis" / "accuracy-samples.csv"):
        frame_ticks = fnum(row, "frameTicks")
        index = bisect.bisect_left(publish_qpc, frame_ticks)
        best = None
        for candidate in (index - 1, index, index + 1):
            if 0 <= candidate < len(publish_qpc):
                distance = abs(publish_qpc[candidate] - frame_ticks)
                if best is None or distance < best[0]:
                    best = (distance, publishes[candidate])
        if best is None or best[0] > 20_000:  # 2 ms: 同じ tick の公開イベントにだけ結ぶ
            join_miss += 1
            continue
        publish = best[1]
        group = groups.get(publish["scheduledQpc"], {})
        acquire = (group.get("compose.acquire") or [{}])[0]
        compose_start = (group.get("compose.start") or [{}])[0]
        compose_complete = (group.get("compose.complete") or [{}])[0]
        record = {
            "phase": row["phase"],
            "clip": row["expectedClipId"],
            "seconds": fnum(row, "receivedSeconds"),
            "fps": fraction(row["fps"]),
            "kind": row["kind"],
            "err": fnum(row, "signedErrorMs"),
            "frameAgeMs": fnum(row, "frameAgeMs"),
            "scheduledQpc": publish["scheduledQpc"],
            "publish_qpc": publish["qpc"],
            "acquire_qpc": acquire.get("qpc"),
            "acquire_status": acquire.get("detail"),
            "acquire_image": acquire.get("imageId"),
            "acquire_generated": acquire.get("generatedQpc"),
            "compose_start_qpc": compose_start.get("qpc"),
            "compose_complete_qpc": compose_complete.get("qpc"),
        }
        record["new_source"] = bool(record["acquire_qpc"]) and new_after.get(record["acquire_qpc"], False)
        if record["new_source"]:
            if backend == "gst":
                match = None
                limit = bisect.bisect_right(delivery_qpc, record["acquire_qpc"])
                for candidate in range(limit - 1, max(-1, limit - 40), -1):
                    if deliveries[candidate].get("imageId") == record["acquire_image"]:
                        match = deliveries[candidate]
                        break
            else:
                match = nearest_before(deliveries, "qpc", record["acquire_generated"], 500_000)
            if match is not None:
                position = deliveries.index(match)
                record["delivery_qpc"] = match["qpc"]
                record["delivery_value"] = match.get("value")
                if position > 0:
                    record["delivery_interval_ms"] = ms(match["qpc"] - deliveries[position - 1]["qpc"])
                    if backend == "gst":
                        head = int(match["detail"].split(":", 1)[0])
                        previous_head = int(deliveries[position - 1]["detail"].split(":", 1)[0])
                        record["pts_step_ms"] = (head - previous_head) / 1e6
                    else:
                        record["pts_step_ms"] = (match.get("value", 0)
                                                 - deliveries[position - 1].get("value", 0)) / 1000.0
        records.append(record)
    return backend, records, join_miss, len(publishes)


def stage_rows():
    def acquire_to_start(r):
        return ms(r["compose_start_qpc"] - r["acquire_qpc"]) if r.get("acquire_qpc") and r.get("compose_start_qpc") else None

    def start_to_complete(r):
        return ms(r["compose_complete_qpc"] - r["compose_start_qpc"]) if r.get("compose_complete_qpc") and r.get("compose_start_qpc") else None

    def complete_to_publish(r):
        return ms(r["publish_qpc"] - r["compose_complete_qpc"]) if r.get("compose_complete_qpc") else None

    def delivery_to_acquire(r):
        return ms(r["acquire_qpc"] - r["delivery_qpc"]) if r.get("delivery_qpc") and r.get("acquire_qpc") else None

    def delivery_to_publish(r):
        return ms(r["publish_qpc"] - r["delivery_qpc"]) if r.get("delivery_qpc") else None

    def lag_frames(r):
        return r["err"] * r["fps"] / 1000.0 if r.get("fps") else None

    return {
        "定常誤差 ms": lambda r: r["err"],
        "誤差（フレーム数）": lag_frames,
        "公開フレーム年齢 ms（LTC 受信 − 公開）": lambda r: r["frameAgeMs"],
        "デコード→配信 到着間隔 ms": lambda r: r.get("delivery_interval_ms"),
        "デコード→配信 pts 刻み ms": lambda r: r.get("pts_step_ms"),
        "配信→取得 ms": delivery_to_acquire,
        "取得→合成 start ms": acquire_to_start,
        "取得→合成 complete ms": start_to_complete,
        "合成→公開 ms": complete_to_publish,
        "配信→公開 合計 ms": delivery_to_publish,
    }


def cell(values):
    if not values:
        return "-"
    return f"{percentile(values, 50):.2f} / {percentile(values, 90):.2f} (n={len(values)})"


def report(run, backend, records, join_miss, publish_count):
    errors = [abs(r["err"]) for r in records]
    p10, p90 = percentile(errors, 10), percentile(errors, 90)
    low = [r for r in records if abs(r["err"]) < p10]
    high = [r for r in records if abs(r["err"]) > p90]
    lines = [f"## {run.name} ({backend})", "",
             f"- 定常サンプル n={len(records)} / |誤差| p10={p10:.1f}ms p90={p90:.1f}ms / "
             f"公開イベント突き合わせ失敗 {join_miss} 件（compose.publish {publish_count} 件）",
             f"- 低誤差群 n={len(low)}: " + ", ".join(f"{k}={v}" for k, v in sorted(Counter(r["phase"] for r in low).items())),
             f"- 高誤差群 n={len(high)}: " + ", ".join(f"{k}={v}" for k, v in sorted(Counter(r["phase"] for r in high).items())),
             f"- 高誤差群の新規ソース率: {sum(r['new_source'] for r in high)}/{len(high)}、低誤差群: {sum(r['new_source'] for r in low)}/{len(low)}",
             f"- 素材 fps 構成（低誤差群）: {dict(sorted(Counter(round(r['fps']) for r in low).items()))}、"
             f"（高誤差群）: {dict(sorted(Counter(round(r['fps']) for r in high).items()))}",
             "",
             "| 段階（中央値 / p90 (n)） | 低誤差群 | 高誤差群 |",
             "| --- | ---: | ---: |"]
    for name, getter in stage_rows().items():
        low_values = [getter(r) for r in low if getter(r) is not None]
        high_values = [getter(r) for r in high if getter(r) is not None]
        lines.append(f"| {name} | {cell(low_values)} | {cell(high_values)} |")
    # 素材 fps が群間で違う行は、60fps 素材のサンプルだけに絞った値も出す。
    for name in ("デコード→配信 到着間隔 ms", "デコード→配信 pts 刻み ms", "配信→取得 ms", "配信→公開 合計 ms"):
        getter = stage_rows()[name]
        low60 = [getter(r) for r in low if round(r["fps"]) == 60 if getter(r) is not None]
        high60 = [getter(r) for r in high if round(r["fps"]) == 60 if getter(r) is not None]
        if low60 or high60:
            lines.append(f"| {name}（60fps のみ） | {cell(low60)} | {cell(high60)} |")
    lines.append("")
    lines.append("present.scanout（公開→走査）はこのハーネスが全画面表示を開かないため 0 件。")
    # 区間（フェーズ。freeze-sweep はクリップ単位）間と区間内の分散分解。
    segments = defaultdict(list)
    for r in records:
        key = r["phase"] if r["phase"] != "freeze-sweep" else f'freeze-sweep/clip{r["clip"]}'
        segments[key].append(r["err"])
    total = [r["err"] for r in records]
    total_var = statistics.pvariance(total)
    between = sum((len(v) / len(total)) * (statistics.fmean(v) - statistics.fmean(total)) ** 2
                  for v in segments.values())
    within = sum((len(v) / len(total)) * statistics.pvariance(v) for v in segments.values() if len(v) > 1)
    lines.append(f"- 区間間分散 {between:.0f} / 区間内分散 {within:.0f} "
                 f"（区間間 {100 * between / total_var:.0f}%、区間内 {100 * within / total_var:.0f}%）"
                 f"、区間内 σ は {min(statistics.pstdev(v) for v in segments.values() if len(v) > 1):.1f}"
                 f"〜{max(statistics.pstdev(v) for v in segments.values() if len(v) > 1):.1f} ms")
    lines.append("")
    return "\n".join(lines)


def load_jsonl(path):
    return [json.loads(line) for line in Path(path).read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def expected_media(fixture, seconds):
    for clip in fixture.get("clips", []):
        if clip["timelineOffset"] <= seconds < clip["timelineOffset"] + (clip["mediaOut"] - clip["mediaIn"]):
            return clip, clip["mediaIn"] + seconds - clip["timelineOffset"]
    return None, None


PHASE_TARGET_SECONDS = {"freeze-sweep": 0, "seek-a": 3, "seek-b": 15, "seek-c": 27, "seek-back": 3}


def resync_rows(run, records):
    fixture = json.loads((run / "fixture.json").read_text(encoding="utf-8-sig"))
    phases = load_jsonl(run / "phases.jsonl")
    starts = {p["name"]: p["ticks"] for p in phases if p.get("type") == "phase-start"}
    frames, ltcs = [], []
    for event in load_jsonl(run / "trace.jsonl"):
        if event.get("type") == "frame":
            frames.append(event)
        elif event.get("type") == "ltc":
            ltcs.append(event)
    frames.sort(key=lambda e: e["ticks"])
    ltcs.sort(key=lambda e: e["ticks"])
    loads = sorted((e for e in load_events(run / "events.jsonl")["load.issue"]), key=lambda e: e["qpc"])
    rows = []

    def add(label, first_ltc, clip, target_media, load, steady_errors, start_to_ltc=None):
        fps = clip["fpsNumerator"] / clip["fpsDenominator"]
        landing = next((e for e in frames if e["ticks"] >= first_ltc["ticks"] and e.get("markerValid") is True
                        and e.get("clipId") == clip["id"] and e["frameIndex"] / fps >= target_media - 0.2), None)
        delay = ms(landing["ticks"] - first_ltc["ticks"]) if landing else None
        landing_pts = landing["frameIndex"] / fps if landing else None
        rows.append({
            "label": label,
            "start_to_ltc": start_to_ltc,
            "ltc_to_load": ms(load["qpc"] - first_ltc["ticks"]) if load else None,
            "load_to_landing": ms(landing["ticks"] - load["qpc"]) if landing and load else None,
            "ltc_to_landing": delay,
            "landing_pts": landing_pts,
            # 着地した瞬間の誤差 = フレーム PTS −（目標メディア秒 + LTC 受信からの経過時間）
            "landing_error": landing_pts - target_media - delay / 1000.0
                             if landing_pts is not None and delay is not None else None,
            "steady_mean": statistics.fmean(steady_errors) if steady_errors else None,
            "n": len(steady_errors),
        })

    for phase in ("freeze-sweep", "seek-a", "seek-b", "seek-c", "seek-back"):
        target = PHASE_TARGET_SECONDS[phase]
        t0 = starts[phase]
        first_ltc = next((e for e in ltcs if e["ticks"] >= t0 and abs(e["seconds"] - target) <= 0.08), None)
        clip, target_media = expected_media(fixture, target + 0.01)
        load = next((e for e in loads if t0 <= e["qpc"] <= t0 + FREQ), None)
        steady = [r["err"] for r in records if r["phase"] == phase]
        add(phase, first_ltc, clip, target_media, load, steady, start_to_ltc=ms(first_ltc["ticks"] - t0))

    freeze_start = starts["freeze-sweep"]
    for boundary, clip_id in ((12, 2), (24, 3)):
        first_ltc = next((e for e in ltcs if e["ticks"] >= freeze_start and abs(e["seconds"] - boundary) <= 0.08), None)
        clip = next(c for c in fixture["clips"] if c["id"] == clip_id)
        load = next((e for e in loads if first_ltc["ticks"] - 2 * FREQ <= e["qpc"] <= first_ltc["ticks"] + FREQ), None)
        steady = [r["err"] for r in records
                  if r["phase"] == "freeze-sweep" and r["clip"] == str(clip_id)]
        add(f"gap {boundary}s -> clip{clip_id}", first_ltc, clip, 0.0, load, steady)
    return rows


def resync_report(run, records):
    rows = resync_rows(run, records)
    lines = ["", "### フェーズ再同期の分解（QPC、ms）", "",
             "start→LTC は計測開始から最初の対応 LTC 受信まで（入力側の遅れ）、"
             "LTC→load はその LTC 受信から load.issue まで、load→landing は load.issue から"
             "新位置の最初のフレーム公開まで。landing PTS はそのフレームのメディア秒、"
             "着地誤差は landing PTS − 目標メディア秒 − LTC→landing。", "",
             "| phase | start→LTC | LTC→load | load→landing | LTC→landing | landing PTS s | 着地誤差 ms | 定常平均 ms | n |",
             "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for row in rows:
        fmt = lambda value: "-" if value is None else f"{value:.1f}"
        landing = "-" if row["landing_pts"] is None else f"{row['landing_pts']:.3f}"
        landing_error = fmt(None if row["landing_error"] is None else row["landing_error"] * 1000)
        steady = ("+" if (row["steady_mean"] or 0) >= 0 else "") + fmt(row["steady_mean"])
        lines.append(f'| {row["label"]} | {fmt(row["start_to_ltc"])} | {fmt(row["ltc_to_load"])} | '
                     f'{fmt(row["load_to_landing"])} | {fmt(row["ltc_to_landing"])} | '
                     f'{landing} | {landing_error} | {steady} | {row["n"]} |')
    lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    sections = []
    for run in args.run:
        backend, records, join_miss, publish_count = build_records(run)
        sections.append(report(run, backend, records, join_miss, publish_count))
        sections.append(resync_report(run, records))
    text = "\n".join(sections)
    if args.output:
        args.output.write_text(text, encoding="utf-8")
        print("wrote", args.output)
    else:
        print(text)


if __name__ == "__main__":
    main()
