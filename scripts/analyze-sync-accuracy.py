#!/usr/bin/env python3
"""Independent decoded-LTC receipt -> published-bitmap accuracy analysis.

No application sync decisions, player positions or render-kind hints enter the
error calculation. Durations are observed receipt-to-receipt intervals, never
inferred over input discontinuities. This is not a physical display measurement.
"""
import argparse
from collections import Counter
import csv
import json
import math
from pathlib import Path


PHASES = {"black-sweep": (0, 35), "freeze-sweep": (0, 35),
          "seek-a": (3, 5), "seek-b": (15, 5), "seek-c": (27, 5), "seek-back": (3, 5)}
# The source restarts at each phase's startSeconds. The decoder can lose the first
# few frames while it re-locks (observed: 1 at 25 fps, 3 when it is slow), so the
# restart frame is accepted as the phase clock up to this many LTC periods after
# startSeconds. Kept small so stale input from the previous phase (e.g. 34.96 for a
# phase starting at 0) can never match.
PHASE_START_LTC_LAG_PERIODS = 5
THRESHOLDS = (20, 40, 80, 250)
EPS = 1e-7
TRACE_DATA_TYPES = ("ltc", "frame", "render-stage")
# A1: reduced-preview events share the writer/footer but are not full-resolution evidence.
TRACE_EXTENSION_TYPES = ("preview-frame",)
TRACE_TYPES = ("meta", "end") + TRACE_DATA_TYPES + TRACE_EXTENSION_TYPES


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def integer(value):
    return isinstance(value, int) and not isinstance(value, bool)


def supported_ltc_fps(fps):
    """V3 LTC fps matrix: 24, 25, 30, and non-drop 29.97 (30000/1001)."""
    return (abs(fps - 24.0) < 0.01 or abs(fps - 25.0) < 0.01 or
            abs(fps - 30.0) < 0.01 or abs(fps - (30000.0 / 1001.0)) < 0.01)


def valid_render_stage(event):
    """Validate the diagnostic envelope without using it as frame evidence."""
    return (all(integer(event.get(key)) for key in
                ("sessionId", "generation", "startTicks", "endTicks", "width", "height", "threadId")) and
            event["sessionId"] > 0 and event["threadId"] > 0 and
            event["width"] >= 0 and event["height"] >= 0 and
            0 <= event["startTicks"] <= event["endTicks"] == event["ticks"] and
            all(isinstance(event.get(key), str) and event[key] for key in ("stage", "outcome")) and
            all(event.get(key) is None or integer(event[key]) for key in ("attemptId", "sequence", "returnCode")))


def distribution(rows, key):
    measured = [r for r in rows if r[key] is not None]
    values = [r[key] for r in measured]
    absolute = sorted(abs(v) for v in values)
    duration = sum(r["weightMs"] for r in measured)
    return {
        "meanSignedMs": sum(values) / len(values) if values else None,
        "meanAbsMs": sum(absolute) / len(absolute) if values else None,
        "p95AbsMs": absolute[math.ceil(len(absolute) * .95) - 1] if absolute else None,
        "p99AbsMs": absolute[math.ceil(len(absolute) * .99) - 1] if absolute else None,
        "maxAbsMs": max(absolute) if absolute else None,
        "observedDurationMs": duration,
        "timeWeightedMeanSignedMs": sum(r[key] * r["weightMs"] for r in measured) / duration if duration else None,
        "timeWeightedMeanAbsMs": sum(abs(r[key]) * r["weightMs"] for r in measured) / duration if duration else None,
        "thresholds": {str(t): {"count": sum(abs(v) > t + EPS for v in values),
                               "durationMs": sum(r["weightMs"] for r in measured if abs(r[key]) > t + EPS)}
                       for t in THRESHOLDS}}


def aggregate(rows):
    return {"samples": len(rows), "measuredSamples": sum(r["status"] == "measured" for r in rows),
            "unmeasuredSamples": sum(r["status"] != "measured" for r in rows),
            "statusCounts": dict(Counter(r["status"] for r in rows)),
            "statusDurationMs": {status: sum(r["weightMs"] for r in rows if r["status"] == status)
                                 for status in sorted({r["status"] for r in rows})},
            "signed": distribution(rows, "signedErrorMs"),
            "interval": distribution(rows, "intervalErrorMs")}


def valid_marker(frame, clips):
    if not frame or frame.get("markerValid") is not True or frame.get("isBlack") is True:
        return False
    clip = clips.get(frame.get("clipId"))
    return (clip is not None and integer(frame.get("frameIndex")) and
            0 <= frame["frameIndex"] < clip["frameCount"])


def gap_correct(frame, mode, previous, clips):
    if not frame:
        return False
    if mode == "black":
        return frame.get("isBlack") is True
    if not previous or not valid_marker(frame, clips) or frame["clipId"] != previous["id"]:
        return False
    last = math.ceil(previous["mediaOut"] * previous["fps"] - EPS) - 1
    return last - 1 <= frame["frameIndex"] <= last


def recovery(rows, limit):
    start = None
    previous = None
    for row in rows:
        good = row["status"] == "measured" and abs(row["intervalErrorMs"]) <= limit + EPS
        if not good:
            start = None
        elif start is None or previous is None or not previous["continuousToNext"]:
            start = row
        if start and row["phaseElapsedMs"] - start["phaseElapsedMs"] >= 500 - EPS:
            return {"recoveryMs": start["phaseElapsedMs"], "confirmedAtMs": row["phaseElapsedMs"],
                    "status": "converged", "sustainMs": 500}
        previous = row
    return {"recoveryMs": None, "confirmedAtMs": None, "status": "not-observed", "sustainMs": 500}


def analyze(events, fixture, journal):
    reasons = []
    warnings = []
    meta = [e for e in events if e.get("type") == "meta"]
    if len(meta) != 1 or meta[0].get("schema") != 1 or not number(meta[0].get("frequency")) or meta[0]["frequency"] <= 0:
        raise ValueError("trace requires exactly one schema=1 meta with positive frequency")
    meta = meta[0]
    if meta.get("boundary") != "bitmap-publication" or meta.get("reference") != "decoded-ltc-receipt":
        raise ValueError("unsupported measurement boundary/reference")
    frequency = meta["frequency"]
    ltc_fps = fixture.get("ltcFps")
    if fixture.get("schema") != 1 or not number(ltc_fps) or not supported_ltc_fps(ltc_fps):
        raise ValueError("fixture requires schema=1 and ltcFps in {24, 25, 29.97, 30}")
    period = 1 / ltc_fps
    max_interval = 3 * period
    clips = {}
    for source in fixture.get("clips", []):
        c = dict(source)
        if (not integer(c.get("id")) or c["id"] not in (1, 2, 3) or c["id"] in clips or
                not integer(c.get("fpsNumerator")) or c["fpsNumerator"] <= 0 or
                not integer(c.get("fpsDenominator")) or c["fpsDenominator"] <= 0 or
                not integer(c.get("frameCount")) or c["frameCount"] <= 0 or
                any(not number(c.get(k)) for k in ("timelineOffset", "mediaIn", "mediaOut")) or
                c["mediaIn"] < 0 or c["mediaOut"] <= c["mediaIn"]):
            raise ValueError("invalid fixture clip")
        c["fps"] = c["fpsNumerator"] / c["fpsDenominator"]
        c["end"] = c["timelineOffset"] + c["mediaOut"] - c["mediaIn"]
        if c["mediaOut"] > c["frameCount"] / c["fps"] + EPS:
            raise ValueError("fixture mediaOut exceeds encoded frames")
        clips[c["id"]] = c
    ordered_clips = sorted(clips.values(), key=lambda c: c["timelineOffset"])
    if len(clips) != 3 or any(a["end"] > b["timelineOffset"] for a, b in zip(ordered_clips, ordered_clips[1:])):
        raise ValueError("fixture requires three non-overlapping clips")

    clean = []
    for event in events:
        if not isinstance(event, dict) or not integer(event.get("ticks")):
            reasons.append("invalid-trace-event")
            continue
        if event.get("type") not in TRACE_TYPES:
            reasons.append("unknown-trace-event-type")
            continue
        # Render diagnostics share the writer/footer but are not pixel or LTC
        # evidence. Accept only the declared extension version; never treat
        # arbitrary new event types as a way to satisfy the footer count.
        if event.get("type") == "render-stage" and (
                not integer(meta.get("renderStageSchema")) or meta["renderStageSchema"] != 1):
            reasons.append("unsupported-render-stage-schema")
            continue
        if event.get("type") == "render-stage" and not valid_render_stage(event):
            reasons.append("invalid-render-stage-event")
            continue
        if event.get("type") == "preview-frame" and (
                not integer(meta.get("previewFrameSchema")) or meta["previewFrameSchema"] != 1):
            reasons.append("unsupported-preview-frame-schema")
            continue
        if event.get("type") == "ltc" and (not number(event.get("seconds")) or
                not number(event.get("fps")) or abs(event["fps"] - ltc_fps) > 0.01):
            reasons.append("invalid-ltc-event")
            continue
        if event.get("type") == "frame" and (not integer(event.get("width")) or event["width"] <= 0 or
                not integer(event.get("height")) or event["height"] <= 0 or
                not isinstance(event.get("markerValid"), bool) or not isinstance(event.get("isBlack"), bool) or
                not number(event.get("probeTicks")) or event["probeTicks"] < 0):
            reasons.append("invalid-frame-event")
            continue
        clean.append(event)
    events = sorted(clean, key=lambda e: e["ticks"])
    ends = [e for e in events if e["type"] == "end"]
    if len(ends) != 1:
        reasons.append("trace-end-missing" if not ends else "trace-end-duplicate")
    else:
        end = ends[0]
        if any(not integer(end.get(k)) or end[k] < 0 for k in ("dropped", "errors", "events")):
            reasons.append("invalid-trace-end")
        else:
            if end["dropped"]:
                reasons.append("trace-dropped-events")
            if end["errors"]:
                reasons.append("trace-write-errors")
            if end["events"] != sum(e["type"] in TRACE_DATA_TYPES + TRACE_EXTENSION_TYPES for e in events):
                reasons.append("trace-event-count-mismatch")
        if any(e["type"] in TRACE_DATA_TYPES + TRACE_EXTENSION_TYPES and e["ticks"] > end["ticks"] for e in events):
            reasons.append("trace-events-after-end")

    phases = []
    for entry in journal:
        if not isinstance(entry, dict) or not number(entry.get("ticks")):
            reasons.append("invalid-phase-event")
            continue
        if entry.get("type") != "phase-start":
            continue
        p = dict(entry)
        if (p.get("name") not in PHASES or p.get("mode") not in ("black", "freeze") or
                p.get("frequency") != frequency or not number(p.get("startSeconds")) or
                not number(p.get("durationSeconds")) or p["durationSeconds"] <= 0):
            reasons.append("invalid-phase-start")
            continue
        matching = [e for e in journal if e.get("type") == "phase-end" and e.get("name") == p["name"]
                    and number(e.get("ticks")) and e["ticks"] > p["ticks"]]
        if len(matching) != 1:
            reasons.append("phase-end-missing-or-duplicate:" + p["name"])
            continue
        p["endTicks"] = matching[0]["ticks"]
        p["firstTicks"] = None
        p["rows"] = []
        if (p["startSeconds"], p["durationSeconds"]) != PHASES[p["name"]]:
            reasons.append("phase-contract-mismatch:" + p["name"])
        if p["name"] in ("black-sweep", "freeze-sweep") and p["mode"] != p["name"].split("-")[0]:
            reasons.append("phase-mode-mismatch:" + p["name"])
        phases.append(p)
    phases.sort(key=lambda p: p["ticks"])
    for name in PHASES:
        count = sum(p["name"] == name for p in phases)
        if count != 1:
            reasons.append("phase-missing-or-duplicate:" + name)
    if any(a["endTicks"] > b["ticks"] for a, b in zip(phases, phases[1:])):
        reasons.append("phase-overlap")
    completed = [e for e in journal if e.get("type") == "completed" and number(e.get("ticks"))]
    if len(completed) != 1 or (phases and completed[0]["ticks"] < phases[-1]["endTicks"]):
        reasons.append("phases-not-completed")

    rows, frames = [], []
    latest = None
    identity = None
    hold_tick = None
    previous_ltc_seconds = None
    for event in events:
        if event["type"] == "frame":
            latest = event
            frames.append(event)
            new_identity = (("marker", event["clipId"], event["frameIndex"]) if valid_marker(event, clips)
                            else ("black",) if event["isBlack"] else ("unknown", len(frames)))
            if new_identity != identity:
                hold_tick = event["ticks"]
                identity = new_identity
            continue
        if event["type"] != "ltc":
            continue
        tick, seconds = event["ticks"], event["seconds"]
        # A phase's LTC clock starts at the first frame that is not a one-period
        # step from the previous frame: the source restart after the phase
        # boundary shows up as a rollback (0 s sweeps) or a forward jump (seek
        # targets). Stale queued frames stay continuous with the previous phase
        # and are not restart candidates.
        restart = previous_ltc_seconds is None or abs(seconds - previous_ltc_seconds - period) > EPS
        previous_ltc_seconds = seconds
        p = next((p for p in phases if p["ticks"] <= tick < p["endTicks"]), None)
        row = {"ticks": tick, "receivedSeconds": seconds, "phase": p["name"] if p else None,
               "mode": p["mode"] if p else None, "phaseElapsedMs": None, "expectedClipId": None,
               "expectedMediaSeconds": None, "fps": None, "clipId": latest.get("clipId") if latest else None,
               "frameIndex": latest.get("frameIndex") if latest else None,
               "frameTicks": latest["ticks"] if latest else None,
               "frameEventIndex": len(frames) - 1 if latest else None,
               "frameAgeMs": (tick - latest["ticks"]) * 1000 / frequency if latest else None,
               "imageHoldMs": (tick - hold_tick) * 1000 / frequency if hold_tick is not None else None,
               "kind": latest.get("kind") if latest else None,
               "status": "outside-phase", "signedErrorMs": None, "intervalErrorMs": None,
               "steady": False, "excludedReason": "outside-phase", "weightMs": 0,
               "receiptIntervalMs": None, "ltcStepMs": None, "continuousToNext": False}
        rows.append(row)
        if not p:
            continue
        p["rows"].append(row)
        # The journal's command time cannot identify audio still queued from the
        # previous phase. Take the restart frame as the phase clock, but only when
        # it is close enough to startSeconds: the decoder can lose a few frames
        # while it re-locks, while stale input from the previous phase stays far
        # from startSeconds and is rejected by the window.
        if p["firstTicks"] is None and restart and (
                p["startSeconds"] - EPS <= seconds
                <= p["startSeconds"] + PHASE_START_LTC_LAG_PERIODS * period + EPS):
            p["firstTicks"] = tick
        if p["firstTicks"] is None or not p["startSeconds"] - EPS <= seconds < p["startSeconds"] + p["durationSeconds"]:
            row.update(status="phase-unmatched", excludedReason="phase-unmatched")
            continue
        row["phaseElapsedMs"] = (tick - p["firstTicks"]) * 1000 / frequency
        clip = next((c for c in ordered_clips if c["timelineOffset"] <= seconds < c["end"]), None)
        if clip:
            row.update(expectedClipId=clip["id"], expectedMediaSeconds=clip["mediaIn"] + seconds - clip["timelineOffset"],
                       fps=f'{clip["fpsNumerator"]}/{clip["fpsDenominator"]}')
        expected_id = clip["id"] if clip else "gap"
        if p.get("previousExpected") != expected_id:
            p["transitionTicks"] = tick
            p["previousExpected"] = expected_id
        settling = tick - max(p["firstTicks"], p["transitionTicks"]) < frequency
        row.update(steady=not settling and clip is not None,
                   excludedReason="gap" if not clip else "settling" if settling else None)
        if not clip:
            previous = next((c for c in reversed(ordered_clips) if c["end"] <= seconds), None)
            row["status"] = "gap-correct" if gap_correct(latest, p["mode"], previous, clips) else (
                "unpublished" if latest is None else "gap-wrong" if valid_marker(latest, clips) or latest["isBlack"] else "gap-unknown")
        elif latest is None:
            row["status"] = "unpublished"
        elif latest["isBlack"]:
            row["status"] = "unexpected-black"
        elif not valid_marker(latest, clips):
            row["status"] = "unknown"
        elif latest["clipId"] != clip["id"]:
            row["status"] = "wrong-clip"
        else:
            pts = latest["frameIndex"] / clip["fps"]
            error = pts - row["expectedMediaSeconds"]
            interval_error = error if error > 0 else min(0, error + 1 / clip["fps"])
            row.update(status="measured", signedErrorMs=error * 1000, intervalErrorMs=interval_error * 1000)

    # Case C: a phase whose start clock never resolved has no usable sample even
    # though the LTC stream is present; keep the phase-level reason explicit.
    for p in phases:
        if p["rows"] and p["firstTicks"] is None:
            reasons.append("phase-start-not-found:" + p["name"])
            for row in p["rows"]:
                row["excludedReason"] = "phase-start-not-found"

    input_stats = {"longGapCount": 0, "discontinuityCount": 0, "abnormalReceiptIntervalCount": 0,
                   "maxReceiptIntervalMs": None, "unweightedGapMs": 0}
    gaps, recoveries, phase_summaries = [], [], []
    for p in phases:
        matching = [r for r in p["rows"] if r["phaseElapsedMs"] is not None]
        for a, b in zip(matching, matching[1:]):
            dt = (b["ticks"] - a["ticks"]) / frequency
            step = b["receivedSeconds"] - a["receivedSeconds"]
            a.update(receiptIntervalMs=dt * 1000, ltcStepMs=step * 1000)
            continuous = 0 < dt <= max_interval + EPS and abs(step - period) <= EPS
            a["continuousToNext"] = continuous
            a["weightMs"] = dt * 1000 if continuous else 0
            input_stats["maxReceiptIntervalMs"] = max(input_stats["maxReceiptIntervalMs"] or 0, dt * 1000)
            if dt > max_interval + EPS:
                input_stats["longGapCount"] += 1
                reasons.append("input-long-gap:" + p["name"])
            if abs(step - period) > EPS:
                input_stats["discontinuityCount"] += 1
                reasons.append("input-discontinuity:" + p["name"])
            if not .5 * period <= dt <= 1.5 * period:
                input_stats["abnormalReceiptIntervalCount"] += 1
            if not continuous:
                input_stats["unweightedGapMs"] += max(0, dt * 1000)
        if (len(matching) < p["durationSeconds"] / period - 3 or not matching or
                matching[-1]["receivedSeconds"] < p["startSeconds"] + p["durationSeconds"] - 3 * period - EPS):
            reasons.append("input-coverage:" + p["name"])
        phase_frames = [f for f in frames if p["ticks"] <= f["ticks"] < p["endTicks"]]
        if not phase_frames:
            reasons.append("frame-coverage:" + p["name"])
        if p["name"].startswith("seek-"):
            recoveries.append({"phase": p["name"], "firstCorrespondingLtcTicks": p["firstTicks"],
                               "within80Ms": recovery(matching, 80), "within250Ms": recovery(matching, 250)})
        in_gap = False
        for i, row in enumerate(matching):
            now_gap = row["expectedClipId"] is None
            if now_gap and not in_gap:
                previous = next((c for c in reversed(ordered_clips) if c["end"] <= row["receivedSeconds"]), None)
                upper = next((r["ticks"] for r in matching[i + 1:] if r["expectedClipId"] is not None), p["endTicks"])
                pos = row["frameEventIndex"] + 1 if row["frameEventIndex"] is not None else 0
                at_start = frames[pos - 1] if pos else None
                censored = gap_correct(at_start, p["mode"], previous, clips)
                correct = at_start if censored else next((f for f in frames[pos:] if f["ticks"] < upper and
                                                         gap_correct(f, p["mode"], previous, clips)), None)
                gaps.append({"phase": p["name"], "mode": p["mode"], "receivedSeconds": row["receivedSeconds"],
                             "firstGapLtcTicks": row["ticks"], "previousClipId": previous["id"] if previous else None,
                             "latencyMs": 0 if censored else (correct["ticks"] - row["ticks"]) * 1000 / frequency if correct else None,
                             "leftCensored": censored, "status": "observed" if correct else "not-observed"})
            in_gap = now_gap
        by_clip = {str(c["id"]): {
            "all": aggregate([r for r in p["rows"] if r["expectedClipId"] == c["id"]]),
            "steady": aggregate([r for r in p["rows"] if r["expectedClipId"] == c["id"] and r["steady"]])}
                   for c in ordered_clips}
        if p["name"] in ("black-sweep", "freeze-sweep"):
            for clip_id, groups in by_clip.items():
                if not groups["steady"]["measuredSamples"]:
                    reasons.append(f'measurable-steady-coverage:{p["name"]}:clip{clip_id}')
        phase_summaries.append({"name": p["name"], "inputSamples": len(p["rows"]),
                                "matchedSamples": len(matching), "publishedFrames": len(phase_frames),
                                "firstCorrespondingLtcTicks": p["firstTicks"],
                                "byClip": by_clip,
                                "all": aggregate(p["rows"]), "steady": aggregate([r for r in p["rows"] if r["steady"]])})
    if not frames:
        reasons.append("frame-coverage:trace")
    if not rows:
        reasons.append("input-coverage:trace")
    if any(r["status"] in ("unknown", "gap-unknown") for r in rows):
        warnings.append("Unknown images have no numeric error; inspect status coverage alongside measured-only statistics.")
    held = [r for r in rows if r["imageHoldMs"] is not None and r["expectedClipId"] is not None]
    all_rows = [r for r in rows if r["phase"] is not None]
    probes = sorted(f["probeTicks"] * 1000 / frequency for f in frames)
    summary = {"schema": 1, "complete": not reasons, "incompleteReasons": sorted(set(reasons)), "warnings": warnings,
               "accuracyPassFail": "not-defined", "boundary": meta["boundary"], "reference": meta["reference"],
                "policy": {"ltcPeriodMs": period * 1000, "maximumContinuousReceiptIntervalMs": max_interval * 1000,
                          "phaseStartMatchToleranceMs": PHASE_START_LTC_LAG_PERIODS * period * 1000,
                          "phaseStartMatchRule": "first frame that is not one LTC period after the previous frame",
                          "settlingMs": 1000, "recoverySustainMs": 500,
                          "percentile": "nearest-rank; sample-weighted", "duration": "forward receipt intervals; zero across discontinuities and after final sample",
                          "signedError": "actual frame PTS minus expected media position", "intervalError": "signed distance from expected media position to frame interval",
                          "resolution": "25 fps LTC: 40 ms steps; frame interval depends on source fps; ms units do not prove 1 ms accuracy"},
               "trace": {"ltcEvents": len(rows), "frameEvents": len(frames), "end": ends[0] if len(ends) == 1 else None,
                         "renderStageEvents": sum(e["type"] == "render-stage" for e in events),
                         "maxProbeMs": max((f["probeTicks"] * 1000 / frequency for f in frames), default=None),
                         "probeOverhead": {"samples": len(probes), "meanMs": sum(probes) / len(probes) if probes else None,
                                           "p95Ms": probes[math.ceil(len(probes) * .95) - 1] if probes else None,
                                           "p99Ms": probes[math.ceil(len(probes) * .99) - 1] if probes else None,
                                           "maxMs": max(probes) if probes else None,
                                           "scope": "pixel marker/black probe only; excludes queue, serialization and other tracing costs"},
                         "maxFrameAgeMs": max((r["frameAgeMs"] for r in rows if r["frameAgeMs"] is not None), default=None),
                         "maxActiveImageHoldMs": max((r["imageHoldMs"] for r in held), default=None),
                         "activeImageHeldOver250MsSamples": sum(r["imageHoldMs"] > 250 for r in held)},
               "input": input_stats, "all": aggregate(all_rows), "steady": aggregate([r for r in all_rows if r["steady"]]),
               "excludedFromSteady": dict(Counter(r["excludedReason"] for r in rows if not r["steady"])),
               "outsidePhaseSamples": sum(r["phase"] is None for r in rows),
               "byFps": {f'{c["fpsNumerator"]}/{c["fpsDenominator"]}': {
                   "all": aggregate([r for r in all_rows if r["expectedClipId"] == c["id"]]),
                   "steady": aggregate([r for r in all_rows if r["expectedClipId"] == c["id"] and r["steady"]])}
                         for c in ordered_clips}, "phases": phase_summaries, "recovery": recoveries, "gaps": gaps}
    return summary, rows


def markdown(summary):
    if "all" not in summary:
        return "# Sync accuracy measurement\n\nINCOMPLETE\n\n" + "\n".join(summary["incompleteReasons"]) + "\n"
    fmt = lambda value: "unmeasured" if value is None else f"{value:.3f}"
    lines = ["# Sync accuracy measurement", "", "COMPLETE" if summary["complete"] else "INCOMPLETE", "",
             "Boundary: decoded LTC receipt to actual WriteableBitmap pixel publication. Physical display and Spout receiver latency are outside this measurement.", "",
             "25 fps LTC has 40 ms steps. Source images represent frame intervals. Millisecond units do not establish 1 ms accuracy; printed decimals are arithmetic only.", "",
             "Accuracy acceptance limits are not defined. A complete measurement can show poor accuracy.", "",
             "Every LTC sample uses the most recently published bitmap, including held images. Unknown and wrong-clip images have no numeric error; inspect coverage before interpreting measured-only statistics.", "",
             "| Population | Samples | Measured | Mean signed ms | Mean absolute ms | p95 absolute ms | p99 absolute ms | Max absolute ms |",
             "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for key in ("all", "steady"):
        group = summary[key]
        for metric in ("signed", "interval"):
            d = group[metric]
            lines.append(f'| {key} / {metric} | {group["samples"]} | {group["measuredSamples"]} | ' +
                         " | ".join(fmt(d[k]) for k in ("meanSignedMs", "meanAbsMs", "p95AbsMs", "p99AbsMs", "maxAbsMs")) + " |")
    lines += ["", "Steady statistics exclude the first 1 second after the first corresponding LTC of each phase and after each expected clip transition. All samples remain in CSV.", "",
              "Threshold times use forward observed receipt intervals. Discontinuous input intervals and the final sample have zero duration weight; these are observed-duration estimates, not continuous display dwell times.", "",
              "| Population / metric | Threshold ms | Exceeding samples | Observed duration ms |", "|---|---:|---:|---:|"]
    for key in ("all", "steady"):
        for metric in ("signed", "interval"):
            for threshold, data in summary[key][metric]["thresholds"].items():
                lines.append(f'| {key} / {metric} | {threshold} | {data["count"]} | {fmt(data["durationMs"])} |')
    lines += ["", "Coverage (all phase samples): " + json.dumps(summary["all"]["statusCounts"]),
              "", "Excluded from steady: " + json.dumps(summary["excludedFromSteady"]),
              "", "Input diagnostics: " + json.dumps(summary["input"]),
              "", f'Maximum unchanged-image age at active LTC samples: {fmt(summary["trace"]["maxActiveImageHoldMs"])} ms. This age can include a preceding intentional gap; it is not an active-only freeze duration.',
              "", "| Source fps | All expected clip samples | All measured | Steady expected clip samples | Steady measured |",
              "|---|---:|---:|---:|---:|"]
    for fps, data in summary["byFps"].items():
        lines.append(f'| {fps} | {data["all"]["samples"]} | {data["all"]["measuredSamples"]} | {data["steady"]["samples"]} | {data["steady"]["measuredSamples"]} |')
    lines += ["", "Recovery is the start of a >=0.5 second consecutive observed input sequence within the interval-error limit, measured from the first corresponding LTC receipt. It cannot bridge missing/discontinuous input.", "",
              "| Seek phase | <=80 ms recovery ms | <=250 ms recovery ms |", "|---|---:|---:|"]
    for data in summary["recovery"]:
        recovered = lambda value: "not observed within phase" if value is None else fmt(value)
        lines.append(f'| {data["phase"]} | {recovered(data["within80Ms"]["recoveryMs"])} | {recovered(data["within250Ms"]["recoveryMs"])} |')
    lines += ["", "Gap latency ends at a correct actual pixel publication. A 0 ms left-censored result means the correct image was already present at the first gap LTC; it is not an exact zero-delay measurement.", "",
              "| Phase | Gap LTC seconds | Latency ms | Left censored |", "|---|---:|---:|---|"]
    for data in summary["gaps"]:
        latency = "not observed during gap" if data["latencyMs"] is None else fmt(data["latencyMs"])
        lines.append(f'| {data["phase"]} | {data["receivedSeconds"]:.3f} | {latency} | {data["leftCensored"]} |')
    if summary["incompleteReasons"]:
        lines += ["", "Incomplete measurement reasons:", ""] + ["- " + r for r in summary["incompleteReasons"]]
    lines += ["", "See accuracy-summary.json for phase distributions, weighted means, image age and probe overhead; accuracy-samples.csv includes excluded and unmeasurable samples.", ""]
    return "\n".join(lines)


def read_jsonl(path):
    values = []
    for i, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        if line.strip():
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError(f"{path.name}:{i}: JSON object required")
            values.append(value)
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("trace", "fixture", "phases", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    try:
        summary, rows = analyze(read_jsonl(args.trace), json.loads(args.fixture.read_text(encoding="utf-8-sig")), read_jsonl(args.phases))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, OverflowError) as error:
        summary, rows = {"schema": 1, "complete": False, "accuracyPassFail": "not-defined",
                         "incompleteReasons": ["input-error: " + str(error)]}, []
    (args.output / "accuracy-summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    with (args.output / "accuracy-samples.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]) if rows else ["ticks", "status"])
        writer.writeheader()
        writer.writerows(rows)
    (args.output / "accuracy-report.md").write_text(markdown(summary), encoding="utf-8")
    print(("COMPLETE" if summary["complete"] else "INCOMPLETE") + ": " + str(args.output / "accuracy-report.md"))
    return 0 if summary["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
