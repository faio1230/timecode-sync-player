#!/usr/bin/env python3
"""T2 段 1: LTC フレームの時刻を音声サンプル位置から出した結果を表にする。

sync-accuracy の trace（trace.jsonl）の `ltc` イベントを読み、次を出す（単位 ms）:
  [1] ticks - sampleTicks     受信ハンドラがフレーム終端からどれだけ遅れて動いたか
  [2] sampleTicks の連続間隔  サンプル位置から出した時刻（1/fps になるはず）
  [3] ticks の連続間隔        従来の受信時刻（音声コールバックの 0/50ms の量子化）
  [4] anchor の窓内ばらつき   最小値フィルタがどれだけ効いたか（コールバックごと）

使い方:
  python scripts/analyze-t2-ltc-clock.py <run ディレクトリ | trace.jsonl | events.jsonl>
"""
import argparse
import json
import statistics
from pathlib import Path


def pct(values, p):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int(round(p / 100 * (len(ordered) - 1)))))]


def stats(values):
    if not values:
        return None
    return {
        "n": len(values),
        "median": statistics.median(values),
        "p5": pct(values, 5),
        "p95": pct(values, 95),
        "min": min(values),
        "max": max(values),
    }


def ticks_to_ms(values, frequency):
    return [value * 1000.0 / frequency for value in values]


def histogram_ms(values, frequency, bucket_ms):
    counts = {}
    for value in values:
        bucket = round(value * 1000.0 / frequency / bucket_ms) * bucket_ms
        counts[bucket] = counts.get(bucket, 0) + 1
    return sorted(counts.items(), key=lambda item: (-item[1], item[0]))


def load_events(path):
    events = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                events.append(json.loads(line))
    return events


def resolve_trace(path):
    target = Path(path)
    if target.is_dir():
        for name in ("trace.jsonl", "events.jsonl"):
            candidate = target / name
            if candidate.exists():
                return candidate
        raise FileNotFoundError(f"{target} に trace.jsonl も events.jsonl もありません")
    return target


def analyze(events):
    frequency = next((event.get("frequency") for event in events
                      if event.get("type") == "meta" and event.get("frequency")), None)
    if not frequency:
        frequency = 10_000_000  # Windows の QPC 既定

    ltcs = [event for event in events if event.get("type") == "ltc"]
    clocked = [event for event in ltcs if event.get("sampleTicks")]

    receipt_delays = [event["ticks"] - event["sampleTicks"] for event in clocked]
    sample_ticks = sorted(event["sampleTicks"] for event in clocked)
    sample_intervals = [later - earlier for earlier, later in zip(sample_ticks, sample_ticks[1:])]
    ticks = sorted(event["ticks"] for event in ltcs)
    receipt_intervals = [later - earlier for earlier, later in zip(ticks, ticks[1:])]

    anchor_spreads = []
    seen_callbacks = set()
    for event in clocked:
        callback = event.get("callbackTicks")
        if callback in seen_callbacks:
            continue
        seen_callbacks.add(callback)
        if "anchorSpreadMs" in event:
            anchor_spreads.append(event["anchorSpreadMs"])

    return {
        "frequency": frequency,
        "ltcEvents": len(ltcs),
        "clockedEvents": len(clocked),
        "receiptDelay": stats(ticks_to_ms(receipt_delays, frequency)),
        "receiptDelayNegative": sum(1 for value in receipt_delays if value < 0),
        "sampleIntervals": stats(ticks_to_ms(sample_intervals, frequency)),
        "sampleIntervalHistogram": histogram_ms(sample_intervals, frequency, bucket_ms=1.0),
        "receiptIntervals": stats(ticks_to_ms(receipt_intervals, frequency)),
        "receiptIntervalHistogram": histogram_ms(receipt_intervals, frequency, bucket_ms=10.0),
        "anchorSpreadMs": stats(anchor_spreads) if anchor_spreads else None,
    }


def format_stats(values):
    if not values:
        return "no samples"
    return (f"n={values['n']} median={values['median']:.1f} p5={values['p5']:.1f} "
            f"p95={values['p95']:.1f} min={values['min']:.1f} max={values['max']:.1f}")


def format_histogram(histogram, top=5):
    if not histogram:
        return "no samples"
    return ", ".join(f"{bucket:.0f}ms:{count}" for bucket, count in histogram[:top])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace", help="run ディレクトリ、または trace.jsonl / events.jsonl のパス")
    args = parser.parse_args()

    path = resolve_trace(args.trace)
    result = analyze(load_events(path))
    print(f"T2 LTC sample clock: {path}")
    print(f"  frequency={result['frequency']} ltcEvents={result['ltcEvents']} "
          f"withSampleClock={result['clockedEvents']}")
    print(f"[1] ticks - sampleTicks (ms): {format_stats(result['receiptDelay'])} "
          f"negative={result['receiptDelayNegative']}")
    print(f"[2] sampleTicks interval (ms): {format_stats(result['sampleIntervals'])}")
    print(f"    histogram (1ms): {format_histogram(result['sampleIntervalHistogram'])}")
    print(f"[3] ticks interval (ms): {format_stats(result['receiptIntervals'])}")
    print(f"    histogram (10ms): {format_histogram(result['receiptIntervalHistogram'])}")
    print(f"[4] anchor window spread (ms per callback): {format_stats(result['anchorSpreadMs'])}")
    if result["clockedEvents"] == 0:
        print("  note: sampleTicks が無い trace です（T2 段 1 より前に記録された run）")


if __name__ == "__main__":
    main()
