"""Parent-side aggregation for the mutex-hold / copy-retry comparison (read-only over run directories).

Usage: python evaluate_runs.py <outputDir(new)> <label=runDir> [<label=runDir> ...]

Each runDir is a Run-GpuOutputProbe.ps1 run (app/, runner-result.json, inputs.json, analysis-*/analysis.json).
Writes <outputDir>/summary.json and <outputDir>/summary.md. Never modifies run directories.
"""
import hashlib
import importlib.util
import json
import statistics
import sys
from pathlib import Path

HARNESS = Path(__file__).resolve().parents[2] / "scripts" / "GpuOutputProbeHarness"


def load_holds_module():
    for name in ("analyze_mutex_holds.py", "Analyze-MutexHolds.py"):
        path = HARNESS / name
        if path.exists():
            spec = importlib.util.spec_from_file_location("holds", path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            return module, name
    raise FileNotFoundError("hold analyzer not found in " + str(HARNESS))


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()


def pct(values, p):
    if not values:
        return None
    ordered = sorted(values)
    import math
    return ordered[max(0, math.ceil(len(ordered) * p) - 1)]


def stats(values):
    return {"count": len(values), "mean": statistics.mean(values) if values else None,
            "p99": pct(values, .99), "max": max(values) if values else None}


def evaluate(label, run_dir, holds):
    run_dir = Path(run_dir)
    app = run_dir / "app"
    manifest = read_json(app / "manifest.json")
    summary = read_json(app / "summary.json")
    runner = read_json(run_dir / "runner-result.json") if (run_dir / "runner-result.json").exists() else None
    inputs = read_json(run_dir / "inputs.json") if (run_dir / "inputs.json").exists() else []
    analyses = sorted(app.parent.glob("analysis-*/analysis.json"))
    analysis = read_json(analyses[-1]) if analyses else None
    options = manifest["options"]
    freq = manifest["qpcFrequency"]
    origin = manifest["originQpc"]
    warm, secs = options["warmup"], options["seconds"]
    w0, w1 = origin + warm * freq, origin + (secs - warm) * freq
    events = [json.loads(line) for line in (app / "events.jsonl").read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    window = [e for e in events if w0 <= e["qpc"] < w1]
    # compose start lateness (scheduled -> compose.start) inside window
    lateness = [(e["qpc"] - e["scheduledQpc"]) * 1000 / freq for e in window if e["stage"] == "compose.start"]
    # display path: scheduled -> display.mutex.acquire (where the hold really starts)
    acquire_offset = [(e["qpc"] - e["scheduledQpc"]) * 1000 / freq for e in window if e["stage"] == "display.mutex.acquire"]
    # spout attempt offset from its own scheduled tick
    spout_select_offset = [(e["qpc"] - e["scheduledQpc"]) * 1000 / freq for e in window
                           if e["stage"] == "send.select.start" and e.get("worker") == "Spout" and not e.get("value")]
    skips = {}
    for e in window:
        if e["stage"] == "skip":
            key = f"{e.get('worker')}:{e.get('detail')}"
            skips[key] = skips.get(key, 0) + int(e.get("value") or 1)
    full_skips = {}
    for e in events:
        if e["stage"] == "skip":
            key = f"{e.get('worker')}:{e.get('detail')}"
            full_skips[key] = full_skips.get(key, 0) + int(e.get("value") or 1)
    sends = [e for e in window if e["stage"] == "send.publish"]
    same_id_resend = sum(1 for a, b in zip(sends, sends[1:]) if a["imageId"] == b["imageId"])
    worst = max(sends, key=lambda e: e["qpc"] - e["generatedQpc"], default=None)
    hold = holds.analyze_run(run_dir)
    errors = [e for e in events if e["stage"] == "error"]
    return {
        "label": label, "run": str(run_dir), "analysis": str(analyses[-1]) if analyses else None,
        "options": {k: options.get(k) for k in ("mode", "output", "width", "height", "fps", "seconds", "warmup", "mutexWaitMs", "sendPhaseMs", "presentWaitMs", "displayPacing", "copyRetry")},
        "display": manifest.get("actualDisplay"), "displays": manifest.get("displays"),
        "outcome": summary.get("outcome"), "validAppResult": summary.get("validPerformanceResult"),
        "validAnalysis": analysis.get("validPerformanceResult") if analysis else None,
        "invalidReasons": analysis.get("invalidReasons") if analysis else None,
        "runnerCompletedNormally": runner.get("completedNormally") if runner else None,
        "runnerError": runner.get("error") if runner else None,
        "metrics": {stage: {"rate": m.get("rateHz", m.get("rate")), "count": m.get("count"), "distinctImageIds": m.get("distinctImageIds", m.get("uniqueImageCount")),
                            "intervalMaxMs": m["intervalMs"]["max"], "intervalP99Ms": m["intervalMs"]["p99"],
                            "ageMeanMs": m["imageAgeMs"]["mean"], "ageP99Ms": m["imageAgeMs"]["p99"], "ageMaxMs": m["imageAgeMs"]["max"]}
                    for stage, m in (analysis["metrics"] if analysis else summary["metrics"]).items()},
        "sendSameIdResendsInWindow": same_id_resend, "sendPublishInWindow": len(sends),
        "worstSendAge": None if worst is None else {"imageId": worst["imageId"], "ageMs": (worst["qpc"] - worst["generatedQpc"]) * 1000 / freq, "atSeconds": (worst["qpc"] - origin) / freq},
        "composeStartLatenessMs": stats(lateness), "displayMutexAcquireOffsetMs": stats(acquire_offset),
        "spoutFirstSelectOffsetMs": stats(spout_select_offset),
        "holds": {k: v for k, v in hold.items() if k not in ("runDirectory", "send_publish")},
        "windowSkips": skips, "fullSkips": full_skips, "errorEvents": len(errors), "droppedEvents": summary.get("droppedEvents"),
        "pool": summary.get("pool"), "appCpuSeconds": summary.get("appCpuSeconds"),
        "appCpuElapsedSeconds": None if not summary.get("appCpuEndQpc") else (summary["appCpuEndQpc"] - summary["appCpuStartQpc"]) / freq,
        "selection": None if not analysis else {k: analysis["selection"].get(k) for k in ("retryAttemptsInWindow", "retrySelectedNewerImage", "metadataErrors") if k in analysis["selection"]},
        "inputSha256": {str(Path(i["Path"]).parent.name + "/" + Path(i["Path"]).name): i["Hash"] for i in inputs},
        "appDllSha256Now": sha256(HARNESS.parent / "GpuOutputProbe" / "bin" / "Debug" / "net8.0-windows" / "GpuOutputProbe.dll"),
    }


def fmt(v, d=3):
    if v is None:
        return "-"
    return f"{v:.{d}f}" if isinstance(v, float) else str(v)


def markdown(rows):
    lines = ["| run | phase | retry | compose Hz | display Hz | Spout Hz | Spout distinct | same-ID resend | Spout age mean/p99/max ms | Spout max gap ms | display hold mean/p99/max ms | copy hold mean/max ms | copy busy first/retry | retry ok/failed/newerID | unexplained | display notReady/busy | CPU s | valid |",
             "| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"]
    for r in rows:
        m, h, s = r["metrics"], r["holds"], r["windowSkips"]
        dh, ch = h.get("display_hold_ms") or {}, h.get("copy_hold_ms") or {}
        sp = m.get("send.publish", {})
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {}/{}/{} | {} | {}/{}/{} | {}/{} | {}/{} | {}/{}/{} | {} | {}/{} | {} | {} |".format(
            r["label"], r["options"]["sendPhaseMs"], r["options"]["copyRetry"],
            fmt(m.get("compose.publish", {}).get("rate")), fmt(m.get("present.return", {}).get("rate")), fmt(sp.get("rate")),
            sp.get("distinctImageIds"), r["sendSameIdResendsInWindow"],
            fmt(sp.get("ageMeanMs")), fmt(sp.get("ageP99Ms")), fmt(sp.get("ageMaxMs")), fmt(sp.get("intervalMaxMs")),
            fmt(dh.get("mean")), fmt(dh.get("p99")), fmt(dh.get("max")), fmt(ch.get("mean")), fmt(ch.get("max")),
            h.get("copy_busy_first"), h.get("copy_busy_retry"),
            h.get("copy_retry_acquire_success"), h.get("copy_retry_failed"), h.get("retry_acquire_newer_id"), h.get("unexplained_busy"),
            s.get("GPU:present.notReady", 0), s.get("GPU:present.keyedMutexBusy", 0), fmt(r["appCpuSeconds"]),
            "yes" if r["validAnalysis"] and r["runnerCompletedNormally"] else "NO"))
    return "\n".join(lines) + "\n"


def main(argv):
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    out = Path(argv[0])
    out.mkdir(parents=True, exist_ok=False)
    holds, holds_name = load_holds_module()
    rows = []
    for item in argv[1:]:
        label, run = item.split("=", 1)
        rows.append(evaluate(label, run, holds))
    (out / "summary.json").write_text(json.dumps({"holdAnalyzer": holds_name, "runs": rows,
        "scope": "standalone generated GPU pattern; prototype only; publication timing, not receiver or scanout verification"}, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "summary.md").write_text(markdown(rows), encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8"); print(markdown(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
