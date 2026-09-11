#!/usr/bin/env python3
"""Read-only keyed-mutex hold summary for GPU output probe runs.

Usage: python analyze_mutex_holds.py <runDir> [<runDir2> ...]

Each run directory is one created by Run-GpuOutputProbe.ps1 (it contains
app/manifest.json and app/events.jsonl). The analysis window is the probe
default [warmup, seconds - warmup). One JSON line per run goes to stdout.
Missing new events (older logs) yield null statistics, never a crash.
"""
import json
import sys
from math import ceil
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def percentile(values, p):
    """Nearest rank: sorted[ceil(n*p)-1], matching the probe's summary."""
    if not values:
        return None
    return sorted(values)[min(len(values) - 1, ceil(len(values) * p) - 1)]


def hold_stats(holds_ms, p99=True):
    if not holds_ms:
        return None
    stats = {"mean": sum(holds_ms) / len(holds_ms), "max": max(holds_ms)}
    if p99:
        stats["p99"] = percentile(holds_ms, 0.99)
    return stats


def ms(delta_qpc, frequency):
    return delta_qpc * 1000.0 / frequency


def analyze_run(run_directory):
    app = Path(run_directory) / "app"
    manifest = read_json(app / "manifest.json")
    options = manifest["options"]
    frequency = int(manifest["qpcFrequency"])
    origin = int(manifest["originQpc"])
    warmup = float(options["warmup"])
    end_seconds = float(options["seconds"]) - warmup
    window_start = origin + warmup * frequency
    window_end = origin + end_seconds * frequency

    def in_window(event):
        return window_start <= int(event["qpc"]) < window_end

    display_acquire = {}   # scheduledQpc -> acquire event
    display_holds_ms = []
    display_intervals_by_image = {}  # imageId -> [(acquireQpc, releaseQpc)]
    copy_acquire = {}      # (scheduledQpc, detail) -> acquire event
    copy_retry_acquire = {}  # scheduledQpc -> retry-kind acquire event
    copy_holds_ms = []
    copy_busy_first = []
    copy_busy_retry_count = 0
    copy_retry_events = []
    display_wait_start = {}  # scheduledQpc -> native start event
    display_wait_ms = []
    with (app / "events.jsonl").open(encoding="utf-8-sig") as stream:
        for line in stream:
            if not line.strip():
                continue
            event = json.loads(line)
            stage = event.get("stage")
            scheduled = int(event.get("scheduledQpc") or 0)
            if stage == "display.mutex.acquire":
                display_acquire.setdefault(scheduled, event)
            elif stage == "display.mutex.release":
                acquire = display_acquire.pop(scheduled, None)
                if acquire is not None:
                    interval = (int(acquire["qpc"]), int(event["qpc"]))
                    image_id = int(acquire.get("imageId") or 0)
                    display_intervals_by_image.setdefault(image_id, []).append(interval)
                    if in_window(acquire):
                        display_holds_ms.append(ms(interval[1] - interval[0], frequency))
            elif stage == "copy.mutex.acquire":
                key = (scheduled, event.get("detail"))
                copy_acquire.setdefault(key, event)
                if in_window(event) and str(event.get("detail", "")).split(":", 1)[0] == "retry":
                    copy_retry_acquire.setdefault(scheduled, event)
            elif stage == "copy.mutex.release":
                acquire = copy_acquire.pop((scheduled, event.get("detail")), None)
                if acquire is not None and in_window(acquire):
                    copy_holds_ms.append(ms(int(event["qpc"]) - int(acquire["qpc"]), frequency))
            elif stage == "copy.retry":
                if in_window(event):
                    copy_retry_events.append(event)
            elif stage == "display.wait.start":
                if in_window(event) and event.get("detail") == "native":
                    display_wait_start[scheduled] = event
            elif stage == "display.wait.end":
                start = display_wait_start.pop(scheduled, None)
                if start is not None and in_window(start):
                    display_wait_ms.append(ms(int(event["qpc"]) - int(start["qpc"]), frequency))
            elif stage == "skip" and event.get("worker") == "Spout" and in_window(event):
                detail = event.get("detail")
                if detail == "copy.keyedMutexBusy":
                    copy_busy_first.append(event)
                elif detail == "copy.keyedMutexBusy.retry":
                    copy_busy_retry_count += 1

    retry_successes = []
    for retry in copy_retry_events:
        acquire = copy_retry_acquire.get(int(retry.get("scheduledQpc") or 0))
        if acquire is not None and int(acquire["qpc"]) >= int(retry["qpc"]):
            retry_successes.append((retry, acquire))
    unexplained_busy = sum(
        1 for busy in copy_busy_first
        if not any(a <= int(busy["qpc"]) <= r for a, r in display_intervals_by_image.get(int(busy.get("imageId") or 0), []))
    )

    send_publish = {}
    try:
        summary = read_json(app / "summary.json")
        metrics = (summary.get("metrics") or {}).get("send.publish") or {}
        interval = metrics.get("intervalMs") or {}
        age = metrics.get("imageAgeMs") or {}
        send_publish = {
            "intervalMsMax": interval.get("max"),
            "imageAgeMs": {"mean": age.get("mean"), "p99": age.get("p99"), "max": age.get("max")},
            "uniqueImageCount": metrics.get("uniqueImageCount"),
        }
    except (OSError, ValueError, json.JSONDecodeError):
        send_publish = {"intervalMsMax": None, "imageAgeMs": None, "uniqueImageCount": None}

    return {
        "runDirectory": str(run_directory),
        "sourceSync": options.get("sourceSync"),
        "windowSeconds": {"start": warmup, "end": end_seconds},
        "display_hold_ms": hold_stats(display_holds_ms),
        "copy_hold_ms": hold_stats(copy_holds_ms),
        "copy_busy_first": len(copy_busy_first),
        "copy_busy_retry": copy_busy_retry_count,
        "copy_retry_events": len(copy_retry_events),
        "copy_retry_acquire_success": len(retry_successes),
        "copy_retry_failed": len(copy_retry_events) - len(retry_successes),
        "retry_acquire_newer_id": sum(1 for retry, acquire in retry_successes
                                      if int(acquire.get("imageId") or 0) > int(retry.get("imageId") or 0)),
        "unexplained_busy": unexplained_busy,
        "send_publish": send_publish,
        "display_wait_native_ms": hold_stats(display_wait_ms, p99=False) if display_wait_ms else None,
    }


def main(argv):
    if not argv:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    for run_directory in argv:
        print(json.dumps(analyze_run(run_directory), ensure_ascii=False, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
