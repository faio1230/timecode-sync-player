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
import math
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


def theil_sen_ppm(segment, frequency, baseline_seconds):
    """段差（アンカー更新）に強い傾き。baseline 秒以上離れた 2 点の傾きの中央値。"""
    points = [(event["sampleTicks"] / frequency, event["seconds"]) for event in segment]
    slopes = []
    for index, (x_i, y_i) in enumerate(points):
        for x_j, y_j in points[index + 1:]:
            if x_j - x_i < baseline_seconds:
                continue
            slopes.append((y_j - y_i) / (x_j - x_i))
    if not slopes:
        return None
    return (statistics.median(slopes) - 1.0) * 1e6


def speed_analysis(clocked, frequency):
    """LTC の速さ（ΔLTC 秒 ÷ Δサンプル時刻）と、連続区間ごとの回帰傾きを出す。

    sampleTicks は音声サンプル位置から出したフレーム終端 QPC。LTC 値はタイムコードなので、
    隣接フレームの ΔLTC はちょうど 1/fps になる。この比がサンプル時計に対する LTC の速さ
    （1.0 が一致）。ペアの比は 1 フレーム分の量子化を含むため、長い連続区間の回帰傾きと
    その標準誤差 sePpm も出す。
    """
    ordered = sorted(clocked, key=lambda event: event["sampleTicks"])
    pair_ppm = []
    segments = []
    current = []
    for earlier, later in zip(ordered, ordered[1:]):
        fps = later.get("fps") or 0.0
        if not fps:
            if len(current) >= 2:
                segments.append(current)
            current = []
            continue
        period = 1.0 / fps
        delta_seconds = later["seconds"] - earlier["seconds"]
        delta_ticks = later["sampleTicks"] - earlier["sampleTicks"]
        if delta_ticks <= 0:
            if len(current) >= 2:
                segments.append(current)
            current = []
            continue
        delta_clock = delta_ticks / frequency
        adjacent = abs(delta_seconds - period) <= period * 1e-6 + 1e-12
        in_range = 0.5 * period <= delta_clock <= 1.5 * period
        if adjacent and in_range:
            if not current:
                current = [earlier, later]
            else:
                current.append(later)
            pair_ppm.append((delta_seconds / delta_clock - 1.0) * 1e6)
        else:
            if len(current) >= 2:
                segments.append(current)
            current = []
    if len(current) >= 2:
        segments.append(current)

    results = []
    for segment in segments:
        xs = [event["sampleTicks"] / frequency for event in segment]
        ys = [event["seconds"] for event in segment]
        count = len(xs)
        mean_x = sum(xs) / count
        mean_y = sum(ys) / count
        sxx = sum((x - mean_x) ** 2 for x in xs)
        sxy = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
        if sxx <= 0:
            continue
        slope = sxy / sxx
        residual = sum((y - mean_y - slope * (x - mean_x)) ** 2 for x, y in zip(xs, ys))
        standard_error = (math.sqrt(residual / (count - 2) / sxx)
                          if count > 2 and residual > 0 else float("nan"))
        segment_pair_ppm = []
        for earlier, later in zip(segment, segment[1:]):
            delta_seconds = later["seconds"] - earlier["seconds"]
            delta_clock = (later["sampleTicks"] - earlier["sampleTicks"]) / frequency
            if delta_clock > 0:
                segment_pair_ppm.append((delta_seconds / delta_clock - 1.0) * 1e6)
        duration = xs[-1] - xs[0]
        results.append({
            "count": count,
            "durationSeconds": duration,
            "ppm": (slope - 1.0) * 1e6,
            "standardErrorPpm": standard_error * 1e6,
            "pairMedianPpm": statistics.median(segment_pair_ppm) if segment_pair_ppm else None,
            "robustPpm": theil_sen_ppm(segment, frequency, min(10.0, duration / 3.0)),
        })
    return pair_ppm, results


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

    pair_ppm, speed_segments = speed_analysis(clocked, frequency)

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
        "speedPairPpm": stats(pair_ppm) if pair_ppm else None,
        "speedPairStdPpm": statistics.pstdev(pair_ppm) if len(pair_ppm) > 1 else None,
        "speedPairCount": len(pair_ppm),
        "speedPairOutliers100": sum(1 for value in pair_ppm if abs(value) > 100),
        "speedPairPpmFiltered": stats([value for value in pair_ppm if abs(value) <= 100]),
        "speedPairFilteredStdPpm": (
            statistics.pstdev([value for value in pair_ppm if abs(value) <= 100])
            if len([value for value in pair_ppm if abs(value) <= 100]) > 1 else None),
        "speedSegments": speed_segments,
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
    if result["speedPairPpm"]:
        speed = result["speedPairPpm"]
        deviation = result["speedPairStdPpm"]
        deviation_text = f"{deviation:.1f}" if deviation is not None else "-"
        filtered = result["speedPairPpmFiltered"]
        filtered_std = result["speedPairFilteredStdPpm"]
        filtered_std_text = f"{filtered_std:.1f}" if filtered_std is not None else "-"
        filtered_text = "-" if not filtered else (f"median={filtered['median']:+.1f} p5={filtered['p5']:+.1f} "
                                                  f"p95={filtered['p95']:+.1f} std={filtered_std_text}")
        print(f"[5] LTC 速度（1 フレーム差の比 − 1、ppm）: n={speed['n']} median={speed['median']:+.1f} "
              f"p5={speed['p5']:+.1f} p95={speed['p95']:+.1f} min={speed['min']:+.1f} "
              f"max={speed['max']:+.1f} std={deviation_text}")
        print(f"    |ppm|>100（アンカー更新の段差を含むペア）: {result['speedPairOutliers100']} / "
              f"{result['speedPairCount']} 件、除外後のペア比: {filtered_text}")
    else:
        print("[5] LTC 速度（1 フレーム差の比 − 1、ppm）: no adjacent-frame pairs")
    speed_segments = [segment for segment in result["speedSegments"] if segment["durationSeconds"] >= 5.0]
    if speed_segments:
        print(f"[6] 連続区間の回帰（LTC 秒 vs サンプル時刻、ppm）: {len(speed_segments)} 区間（>= 5s）")
        for index, segment in enumerate(speed_segments, 1):
            pair_median = (f"{segment['pairMedianPpm']:+.1f}"
                           if segment["pairMedianPpm"] is not None else "-")
            robust = f"{segment['robustPpm']:+.2f}" if segment["robustPpm"] is not None else "-"
            print(f"    #{index} n={segment['count']} duration={segment['durationSeconds']:.1f}s "
                  f"slopePpm={segment['ppm']:+.2f} sePpm={segment['standardErrorPpm']:.2f} "
                  f"robustPpm={robust} pairMedianPpm={pair_median}")
    else:
        print("[6] 連続区間の回帰（ppm）: no segment >= 5s")
    print("    注: ペア比は 1 フレーム（1/fps）分の量子化を含む。長区間の傾きは sePpm と robustPpm を併記。")
    if result["clockedEvents"] == 0:
        print("  note: sampleTicks が無い trace です（T2 段 1 より前に記録された run）")


if __name__ == "__main__":
    main()
