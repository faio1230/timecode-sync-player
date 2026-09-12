"""Read-only analysis of GPU probe source logs; writes only a new output directory."""
from __future__ import annotations

import argparse
import bisect
import csv
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import sys
import uuid


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def percentile(values, fraction):
    """Nearest rank: sorted[ceil(n*p)-1], matching the probe's summary."""
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered)*fraction)-1)]


def distribution(values):
    return {"count": len(values), "mean": sum(values) / len(values) if values else None,
            "p95": percentile(values, .95), "p99": percentile(values, .99),
            "max": max(values) if values else None}


def align_offsets(events, frequency):
    """Rebuild the shared schedule offset from compose.align corrections (detail = whole microseconds, applied as
    c_us*frequency/1e6 truncated, exactly as the probe quantizes them). Returns (qpcs, cumulative ticks after each)."""
    qpcs, cumulative, total = [], [], 0
    for event in events:
        if event["stage"] != "compose.align":
            continue
        detail = str(event.get("detail", ""))
        correction = int(detail) if detail.lstrip("-").isdigit() else 0
        total += int(correction*frequency/1_000_000)
        qpcs.append(event["qpc"])
        cumulative.append(total)
    return qpcs, cumulative


def offset_before(offsets, qpc):
    """Offset in effect for a tick taken before an event at qpc: corrections recorded strictly earlier."""
    qpcs, cumulative = offsets
    index = bisect.bisect_left(qpcs, qpc)-1
    return cumulative[index] if index >= 0 else 0


def gpu_compose_slots(events):
    """scheduledQpc of every taken GPU compose tick (compose.start or a compose skip), in log order."""
    slots = {}
    for event in events:
        if event.get("worker") == "GPU" and (event["stage"] == "compose.start" or (event["stage"] == "skip" and str(event.get("detail", "")).startswith("compose."))):
            slots.setdefault(event.get("scheduledQpc"), event)
    return slots


def pair_durations(events, start, end, frequency):
    pairs = [("compose.start", "compose.complete"), ("copy.start", "copy.complete"),
             ("display.draw.start", "display.draw.complete"),
             ("send.acquire.start", "send.acquire.end"),
             ("present.ready.start", "present.ready.end"),
             ("display.wait.start", "display.wait.end"), ("display.select.start", "display.select.end"),
             ("compose.publish", "compose.visible"), ("send.select.start", "send.select.end"),
             ("present.start", "present.return"), ("send.start", "send.return"),
             ("send.return", "send.gpuComplete"), ("send.start", "send.publish")]
    result = {}
    for first_stage, last_stage in pairs:
        # A same-slot copy retry may re-select the same image; only the selection
        # pair carries an attempt index (value 0/1) to keep that from being a duplicate.
        key_fields = ["worker", "scheduledQpc", "imageId"] + (["attempt"] if first_stage == "send.select.start" else [])
        starts, ends = {}, {}
        for event in events:
            if event["stage"] not in (first_stage, last_stage):
                continue
            key = (event.get("worker"), event.get("scheduledQpc", 0), event["imageId"])
            if len(key_fields) == 4:
                key += (event.get("value") or 0,)
            selected = starts if event["stage"] == first_stage else ends
            selected.setdefault(key, []).append(event)
        durations = []
        missing_start = missing_end = duplicate = omitted_boundary = omitted_outside = reversed_pairs = 0
        for key in starts.keys() | ends.keys():
            left, right = starts.get(key, []), ends.get(key, [])
            if not left:
                missing_start += 1
            if not right:
                missing_end += 1
            if len(left) > 1 or len(right) > 1:
                duplicate += 1
            if len(left) != 1 or len(right) != 1:
                continue
            if right[0]["qpc"] < left[0]["qpc"]:
                reversed_pairs += 1
                continue
            left_inside = start <= left[0]["relativeSeconds"] < end
            right_inside = start <= right[0]["relativeSeconds"] < end
            if left_inside and right_inside:
                durations.append((right[0]["qpc"]-left[0]["qpc"])*1000/frequency)
            elif left_inside or right_inside or left[0]["relativeSeconds"] < start < end <= right[0]["relativeSeconds"]:
                omitted_boundary += 1
            else:
                omitted_outside += 1
        result[first_stage + "->" + last_stage] = {
            "durationMs": distribution(durations), "missingStartKeys": missing_start,
            "missingEndKeys": missing_end, "duplicateKeys": duplicate,
            "reversedPairs": reversed_pairs, "omittedBoundaryPairs": omitted_boundary,
            "omittedOutsidePairs": omitted_outside,
            "pairKey": key_fields}
    return result


def acquisition_report(events, start, end, frequency, options, image_sources):
    """Keep old logs valid; separately expose zero-poll overhead and positive waits."""
    first_stage, last_stage = "send.acquire.start", "send.acquire.end"
    starts, ends = {}, {}
    starts_in_window, ends_in_window = [], []
    sends = {}
    for event in events:
        key = (event.get("worker"), event.get("scheduledQpc", 0), event["imageId"])
        if event["stage"] == "send.start":
            sends.setdefault(key, []).append(event)
        if event["stage"] in (first_stage, last_stage):
            collection = starts if event["stage"] == first_stage else ends
            collection.setdefault(key, []).append(event)
            if start <= event["relativeSeconds"] < end:
                (starts_in_window if event["stage"] == first_stage else ends_in_window).append(event)
    configured = options.get("mutexWaitMs")
    errors = []
    if configured is not None and (isinstance(configured, bool) or not isinstance(configured, int) or not 0 <= configured <= 8):
        errors.append("Invalid configured mutexWaitMs")
        configured = None
    effective_values, durations, zero_durations, positive_durations, overruns = [], [], [], [], []
    outcomes = {"acquired": 0, "busy": 0, "abandoned": 0}
    deadline_count = late_acquired = late_without_send = late_with_send = 0
    overrun_count = 0
    unknown_deadline = 0
    for key in starts.keys() | ends.keys():
        left, right = starts.get(key, []), ends.get(key, [])
        if len(left) != 1 or len(right) != 1:
            errors.append("Acquisition start/end must pair exactly once")
            continue
        first, last = left[0], right[0]
        timeout = first.get("value")
        if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or timeout != int(timeout) or not 0 <= timeout <= 8 or (configured is not None and timeout > configured):
            errors.append("Invalid effective acquisition timeout")
            continue
        if start <= first["relativeSeconds"] < end:
            effective_values.append(timeout)
        if first["generatedQpc"] != last["generatedQpc"]:
            errors.append("Acquisition pair changed generatedQpc")
        matches = image_sources.get(first["imageId"], [])
        if first["imageId"] > 0 and (len(matches) != 1 or matches[0]["generatedQpc"] != first["generatedQpc"] or matches[0]["qpc"] > first["qpc"]):
            errors.append("Acquisition image has no unique earlier compose.publish with matching generatedQpc")
        outcome = last.get("detail")
        if outcome not in outcomes:
            errors.append("Unknown acquisition outcome")
            continue
        # Design rule: "abandoned" (receiver died while holding the mutex) counts as acquired; sending continues.
        if outcome not in ("acquired", "abandoned") and key in sends:
            errors.append("SendTexture started without successful mutex acquisition")
        first_deadline, last_deadline = first.get("deadlineQpc") or 0, last.get("deadlineQpc") or 0
        if isinstance(first_deadline, bool) or isinstance(last_deadline, bool) or not isinstance(first_deadline, int) or not isinstance(last_deadline, int) or first_deadline <= 0 or last_deadline <= 0:
            errors.append("Acquisition requires positive deadlineQpc on both endpoints")
            if start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end:
                unknown_deadline += 1
            continue
        if first_deadline != last_deadline:
            errors.append("Acquisition pair changed deadlineQpc")
        deadline = first_deadline
        # Match the engine's double-precision remaining-budget calculation.
        remaining_whole_ms = math.floor(max(0.0, float(deadline)-float(first["qpc"]))*1000.0/frequency)
        if timeout > remaining_whole_ms:
            errors.append("Effective acquisition timeout exceeds remaining whole milliseconds")
        if last["qpc"] < first["qpc"]:
            errors.append("Acquisition timestamps reversed")
            continue
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        duration = (last["qpc"]-first["qpc"])*1000/frequency
        durations.append(duration)
        outcomes[outcome] += 1
        if timeout == 0:
            zero_durations.append(duration)
        else:
            positive_durations.append(duration)
            overrun = max(0, duration-timeout)
            overruns.append(overrun)
            if overrun > 0:
                overrun_count += 1
        if not deadline:
            unknown_deadline += 1
        elif last["qpc"] >= deadline:
            deadline_count += 1
            if outcome == "acquired":
                late_acquired += 1
                if key in sends:
                    late_with_send += 1
                else:
                    late_without_send += 1
    # Protocol validation spans the whole log, including warmup/cooldown. A
    # well-formed busy or late-acquired attempt with no send remains intentional.
    if "mutexWaitMs" in options or starts or ends:
        for key, send_events in sends.items():
            left, right = starts.get(key, []), ends.get(key, [])
            if len(send_events) != 1 or len(left) != 1 or len(right) != 1:
                errors.append("SendTexture start requires exactly one matching acquisition pair")
                continue
            completed, sent = right[0], send_events[0]
            # Design rule: an abandoned mutex (receiver died while holding it) counts as acquired and sending continues.
            if completed.get("detail") not in ("acquired", "abandoned"):
                errors.append("SendTexture started without successful mutex acquisition")
            if completed["qpc"] > sent["qpc"]:
                errors.append("SendTexture started before mutex acquisition returned")
            deadline = completed.get("deadlineQpc") or 0
            if not isinstance(deadline, int) or deadline <= 0 or completed["qpc"] >= deadline:
                errors.append("Acquisition completed at/after deadline but SendTexture still started")
            if completed["generatedQpc"] != sent["generatedQpc"]:
                errors.append("SendTexture changed generatedQpc after mutex acquisition")
    return {"available": bool(starts or ends), "configuredWaitMs": configured,
            "startEventsInWindow": len(starts_in_window), "endEventsInWindow": len(ends_in_window),
            "pairedOutcomesInWindow": outcomes, "actualDurationMs": distribution(durations),
            "effectiveRequestedTimeoutMs": distribution(effective_values),
            "zeroTimeoutCallDurationMs": distribution(zero_durations),
            "positiveTimeoutCallDurationMs": distribution(positive_durations),
            "positiveTimeoutOverrunMs": distribution(overruns), "positiveTimeoutOverrunCount": overrun_count,
            "deadlineReachedOrExceededCount": deadline_count,
            "acquiredAtOrAfterDeadlineCount": late_acquired,
            "lateAcquiredWithoutSendStartCount": late_without_send,
            "lateAcquiredWithSendStartCount": late_with_send,
            "pairsWithoutDeadline": unknown_deadline,
            "convention": "Durations/outcomes/deadlines require both endpoints inside [start,end). Effective timeout uses starts inside window. Deadline is reached at endQpc>=deadlineQpc. Positive-timeout overrun is max(0,actual-requested); zero-timeout call overhead is excluded.",
            "metadataErrors": sorted(set(errors))}


def present_wait_plan_report(manifest, command, events, start, end):
    """Validate declared segments and observed application without resetting state."""
    options = manifest["options"]
    origin, frequency = int(manifest["originQpc"]), int(manifest["qpcFrequency"])
    duration, warmup = float(options["seconds"]), float(options["warmup"])
    new_plan = "presentWaitPlan" in options or "presentWaitSegments" in manifest
    plan = options.get("presentWaitPlan", "fixed")
    errors = []
    if plan not in ("fixed", "abba", "baab"):
        errors.append("Invalid presentWaitPlan")
    configured = options.get("presentWaitMs")
    if new_plan and (isinstance(configured, bool) or not isinstance(configured, int) or configured not in (0, 1)):
        errors.append("Present wait plan requires an explicit valid presentWaitMs")
    if plan in ("abba", "baab") and (configured != 0 or options["output"] not in ("fullscreen", "both") or duration/4 <= 2*warmup):
        errors.append("Nonfixed present wait plan requires display, base wait zero and a positive guarded segment window")
    if command is not None and (new_plan or "presentWaitPlan" in command):
        if "presentWaitPlan" not in options or "presentWaitPlan" not in command or command["presentWaitPlan"] != plan:
            errors.append("command presentWaitPlan must explicitly match manifest options")
    waits = [0, 1, 1, 0] if plan == "abba" else [1, 0, 0, 1] if plan == "baab" else [configured]
    segment_seconds = duration/len(waits)
    segments = []
    for index, wait in enumerate(waits):
        first, last = index*segment_seconds, (index+1)*segment_seconds
        segments.append({"index": index, "waitMs": wait, "startSeconds": first, "endSeconds": last,
                         "analysisStartSeconds": first+warmup, "analysisEndSeconds": last-warmup,
                         "startQpc": origin+int(first*frequency), "endQpc": origin+int(last*frequency)})
    if new_plan:
        actual = manifest.get("presentWaitSegments")
        if "presentWaitPlan" not in options or not isinstance(actual, list) or len(actual) != len(segments):
            errors.append("New present wait plan requires a complete manifest segment array")
        else:
            for expected, observed in zip(segments, actual):
                if not isinstance(observed, dict) or any(isinstance(observed.get(field), bool) or observed.get(field) != value for field, value in expected.items()):
                    errors.append("Manifest presentWaitSegments disagree with the declared plan/timebase")

    def member(scheduled):
        return next((segment for segment in segments if isinstance(scheduled, int) and not isinstance(scheduled, bool) and segment["startQpc"] <= scheduled < segment["endQpc"]), None)

    markers = [event for event in events if event["stage"] == "present.wait.segment"]
    if markers and not new_plan:
        errors.append("Segment application events require manifest plan definitions")
    applied = []
    for event in markers:
        segment = member(event.get("scheduledQpc"))
        if segment is None or event.get("worker") != "GPU" or event["imageId"] != 0 or event["generatedQpc"] != 0:
            errors.append("Invalid segment application event identity or membership")
            continue
        if event.get("detail") != str(segment["index"]) or event.get("value") != segment["waitMs"] or event.get("deadlineQpc") != segment["endQpc"]:
            errors.append("Segment application event does not match its scheduled segment")
        scheduled = event["scheduledQpc"]
        tick = round((scheduled-origin)*float(options["fps"])/frequency)
        if scheduled != origin+round(tick*frequency/float(options["fps"])) or event["qpc"] < scheduled:
            errors.append("Segment application event has an invalid GPU scheduled/observed time")
        if applied and (segment["index"] <= applied[-1][0]["index"] or scheduled <= applied[-1][1]["scheduledQpc"]):
            errors.append("Segment application must advance once per selected index")
        applied.append((segment, event))
    applied.sort(key=lambda item: item[1]["scheduledQpc"])
    applied_ticks = [item[1]["scheduledQpc"] for item in applied]
    if new_plan:
        for event in events:
            if event.get("worker") != "GPU" or event["stage"] not in ("compose.start", "compose.publish", "present.ready.start", "display.draw.start", "present.start"):
                continue
            segment = member(event.get("scheduledQpc"))
            if segment is None:
                errors.append("GPU work has no scheduled present wait segment")
                continue
            at = bisect.bisect_right(applied_ticks, event["scheduledQpc"])-1
            if at < 0 or applied[at][0]["index"] != segment["index"] or applied[at][1]["qpc"] > event["qpc"]:
                errors.append("GPU work requires a prior application event for its scheduled segment")
    selected = [segment for segment in segments if segment["startSeconds"] < end and start < segment["endSeconds"]]
    values = list(dict.fromkeys(segment["waitMs"] for segment in selected))
    scope = "single-segment" if len(selected) == 1 and selected[0]["startSeconds"] <= start < end <= selected[0]["endSeconds"] else "multiple-segments" if len(selected) > 1 else "outside-segments"
    return {"plan": plan, "usesManifestSegments": new_plan, "segments": segments,
            "window": {"scope": scope, "segmentIndices": [segment["index"] for segment in selected], "waitMsValues": values,
                       "withinDeclaredAnalysisWindow": len(selected) == 1 and selected[0]["analysisStartSeconds"] <= start < end <= selected[0]["analysisEndSeconds"]},
            "observedAppliedIndices": [segment["index"] for segment, _ in applied],
            "metadataErrors": sorted(set(errors))}


def display_wait_report(events, start, end, frequency, origin, options, command, image_sources):
    """Validate the bounded idle wait separately from source ownership/readiness."""
    pacing = options.get("displayPacing", "tick")
    errors = []
    if pacing not in ("tick", "ready", "vsync", "vblank"):
        errors.append("Invalid displayPacing")
    if pacing == "ready" and (options.get("mode") != "split" or options.get("output") != "both" or
                              options.get("presentWaitMs") != 0 or options.get("presentWaitPlan") != "fixed"):
        errors.append("Ready display pacing requires split/both, presentWaitMs 0 and fixed plan")
    if pacing == "vsync" and (options.get("output") not in ("fullscreen", "both") or
                              options.get("presentWaitMs", 0) != 0 or options.get("presentWaitPlan", "fixed") != "fixed"):
        errors.append("Vsync display pacing requires display output, presentWaitMs 0 and fixed plan")
    if command is not None and ("displayPacing" in options or "displayPacing" in command):
        if "displayPacing" not in options or "displayPacing" not in command or command["displayPacing"] != pacing:
            errors.append("command displayPacing must explicitly match manifest options")
    pairs = {}
    visible_images = [event for event in events if event["stage"] == "compose.visible" and event.get("worker") == "GPU"]
    visible_times = [event["qpc"] for event in visible_images]
    # compose align: scheduledQpc carries the shared offset, so a slot is validated as a taken GPU compose tick (the ticks
    # themselves are checked against the rebuilt offset in compose_align_report); the bootstrap deadline formula still holds
    # because no correction precedes the first scanout.
    aligned = options.get("composeAlign") == "vblank"
    slots_taken = gpu_compose_slots(events) if aligned else None
    # vblank: a predicted attempt's selection/draw/present deadline is its predicted vblank (the latest same-slot predict before
    # the selection), capped by the common end; only bootstrap attempts keep the compose deadline.
    predicts_by_slot = {}
    for event in events:
        if event["stage"] == "display.vblank.predict" and isinstance(event.get("deadlineQpc"), int) and not isinstance(event.get("deadlineQpc"), bool):
            predicts_by_slot.setdefault((event.get("worker"), event.get("scheduledQpc")), []).append(event)
    for prefix in ("display.wait", "display.select"):
        slots = {}
        for event in events:
            if event["stage"] in (prefix+".start", prefix+".end"):
                slot = slots.setdefault((event.get("worker"), event.get("scheduledQpc")), {"start": [], "end": []})
                slot[event["stage"].rsplit(".", 1)[1]].append(event)
        pairs[prefix] = {}
        for key, slot in slots.items():
            if len(slot["start"]) != 1 or len(slot["end"]) != 1:
                errors.append(prefix+" requires exactly one start/end per worker and scheduled tick")
                continue
            first, last = slot["start"][0], slot["end"][0]
            pairs[prefix][key] = (first, last)
            if first.get("worker") != "GPU" or any(first.get(field) != last.get(field) for field in ("imageId", "generatedQpc", "deadlineQpc")):
                errors.append(prefix+" changed metadata or has a non-GPU worker")
            scheduled, deadline = first.get("scheduledQpc"), first.get("deadlineQpc")
            if isinstance(scheduled, bool) or not isinstance(scheduled, int) or scheduled < origin or isinstance(deadline, bool) or not isinstance(deadline, int):
                errors.append(prefix+" requires a GPU scheduled tick and integer deadline")
                continue
            index = round((scheduled-origin)*float(options["fps"])/frequency)
            common_end = origin+int(float(options["seconds"])*frequency)
            expected, rule = min(origin+round((index+1)*frequency/float(options["fps"])), common_end), "min(next GPU tick, common end)"
            predicted = [item for item in predicts_by_slot.get(key, []) if item["qpc"] <= first["qpc"]] if pacing == "vblank" and prefix == "display.select" else []
            if predicted:
                expected, rule = min(max(predicted, key=lambda item: item["qpc"])["deadlineQpc"], common_end), "min(predicted vblank, common end) for a predicted attempt"
            if aligned and scheduled not in slots_taken:
                errors.append(prefix+" slot must be a taken GPU compose tick under compose align")
            if (not aligned and scheduled != origin+round(index*frequency/float(options["fps"]))) or deadline != expected:
                errors.append(prefix+" deadline must equal "+rule)
            if first["qpc"] < scheduled or last["qpc"] < first["qpc"]:
                errors.append(prefix+" timestamps precede the slot or are reversed")
            if prefix == "display.wait":
                if pacing != "ready":
                    errors.append("display.wait events require ready pacing")
                if first["imageId"] != 0 or first["generatedQpc"] != 0:
                    errors.append("Display wait must not carry a selected image")
                mode, outcome, held, timeout = first.get("detail"), last.get("detail"), last.get("value"), first.get("value")
                if mode not in ("native", "retained") or outcome not in ("ready", "timeout", "cancelled", "deadline", "error", "retained"):
                    errors.append("Invalid display wait mode/outcome")
                if isinstance(held, bool) or held not in (0, 1):
                    errors.append("Display wait permission must be zero or one")
                expected_timeout = 0 if mode == "retained" else math.floor(max(0.0, float(deadline)-float(first["qpc"]))*1000/frequency)
                if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or timeout != expected_timeout:
                    errors.append("Display wait timeout must equal remaining whole milliseconds (retained zero)")
                if first["qpc"] >= deadline:
                    errors.append("Expired display wait should be skipped before starting")
                if outcome in ("ready", "retained") and (held != 1 or last["qpc"] >= deadline):
                    errors.append("Display wait usable return requires permission before deadline")
                if outcome == "timeout" and (held != 0 or last["qpc"] >= deadline):
                    errors.append("Display timeout cannot grant permission or return at/after deadline")
                if outcome == "deadline" and last["qpc"] < deadline:
                    errors.append("Display deadline outcome precedes deadline")
                if mode == "retained" and (held != 1 or outcome not in ("retained", "deadline", "cancelled", "error")):
                    errors.append("Retained display wait must preserve permission")
                if mode == "native" and outcome == "retained":
                    errors.append("Native display wait cannot report retained")
                if outcome == "error":
                    errors.append("Display wait reported error")
            else:
                outcome = last.get("detail")
                latest_at = bisect.bisect_right(visible_times, first["qpc"])-1
                latest = visible_images[latest_at] if latest_at >= 0 else None
                if (outcome == "none" and latest is not None) or (outcome == "latest" and (latest is None or
                        any(latest.get(field) != first.get(field) for field in ("imageId", "generatedQpc")))):
                    errors.append("Display selection must use the latest same-GPU visible image at selection start")
                if outcome == "none":
                    if first["imageId"] != 0 or first["generatedQpc"] != 0:
                        errors.append("Null display selection must have zero image metadata")
                elif outcome == "latest":
                    source = image_sources.get(first["imageId"], [])
                    if first["imageId"] <= 0 or len(source) != 1 or source[0]["generatedQpc"] != first["generatedQpc"] or source[0]["qpc"] > last["qpc"]:
                        errors.append("Display selection requires an image published by selection end")
                else:
                    errors.append("Invalid display selection outcome")
                if pacing == "ready":
                    waited = pairs["display.wait"].get(key)
                    if waited is None or waited[1]["qpc"] > first["qpc"] or waited[1].get("value") != 1 or waited[1].get("detail") not in ("ready", "retained"):
                        errors.append("Display selection requires a usable earlier same-slot wait")
                if pacing in ("ready", "vsync", "vblank"):
                    # vblank pacing may present immediately after the wake target passed as long as the
                    # vblank is still >= 1ms away (design rule I3): allow selection up to vblank - 1ms.
                    limit = deadline
                    margin_ms = options.get("presentMarginMs")
                    if pacing == "vblank" and isinstance(margin_ms, (int, float)) and not isinstance(margin_ms, bool):
                        limit = deadline + int(round((float(margin_ms) - 1.0) * frequency / 1000))
                    if first["qpc"] >= limit:
                        errors.append("Display selection started after its deadline")
    if "displayPacing" in options:
        for event in events:
            if event["stage"] not in ("present.ready.start", "display.draw.start", "present.start"):
                continue
            selected = pairs["display.select"].get((event.get("worker"), event.get("scheduledQpc")))
            if selected is None or selected[1]["qpc"] > event["qpc"] or selected[1].get("detail") != "latest" or any(selected[1].get(field) != event.get(field) for field in ("imageId", "generatedQpc")):
                errors.append("Display readiness/draw/Present requires a matching earlier selected image")
    if pacing == "ready":
        gpu_events = [event for event in events if event.get("worker") == "GPU"]
        composed = {}
        for event in gpu_events:
            if event["stage"] == "compose.visible" or (event["stage"] == "skip" and event.get("detail") in ("compose.noFreeSlot", "compose.keyedMutexBusy", "compose.sourceNotReady")):
                composed.setdefault(event.get("scheduledQpc"), []).append(event["qpc"])
        work_times = [event["qpc"] for event in gpu_events if event["stage"] in
                      ("compose.start", "compose.publish", "display.select.start", "present.ready.start", "display.draw.start", "present.start")]
        for key, (first, last) in pairs["display.wait"].items():
            completed_compose = any(qpc <= first["qpc"] for qpc in composed.get(key[1], []))
            if not completed_compose:
                errors.append("Display wait requires a completed composition attempt in its slot")
            if bisect.bisect_left(work_times, last["qpc"]) > bisect.bisect_right(work_times, first["qpc"]):
                errors.append("GPU work/selection overlaps a display wait")
        cancelled_at = next((event["qpc"] for event in gpu_events if
                             (event["stage"] == "display.wait.end" and event.get("detail") == "cancelled") or
                             (event["stage"] == "skip" and event.get("detail") == "display.wait.cancelled")), None)
        if cancelled_at is not None and any(event["qpc"] >= cancelled_at and event["stage"] in
                                           ("compose.start", "display.wait.start", "display.select.start", "display.draw.start", "present.start") for event in gpu_events):
            errors.append("New GPU work started after display wait cancellation")
    durations, zero, positive, overruns, requests = [], [], [], [], []
    outcomes, modes = {}, {}
    for first, last in pairs["display.wait"].values():
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        elapsed = (last["qpc"]-first["qpc"])*1000/frequency
        durations.append(elapsed)
        for bucket, name in ((outcomes, last.get("detail")), (modes, first.get("detail"))):
            bucket[str(name)] = bucket.get(str(name), 0)+1
        requested = first.get("value")
        if isinstance(requested, (int, float)) and math.isfinite(requested):
            requests.append(requested)
            if first.get("detail") == "native":
                if requested > 0:
                    positive.append(elapsed)
                    overruns.append(max(0, elapsed-requested))
                else:
                    zero.append(elapsed)
    return {"displayPacing": pacing, "available": pacing == "ready", "pairedAttemptsInWindow": len(durations),
            "durationMs": distribution(durations), "effectiveRequestedTimeoutMs": distribution(requests),
            "nativeZeroTimeoutDurationMs": distribution(zero), "nativePositiveTimeoutDurationMs": distribution(positive),
            "positiveTimeoutOverrunMs": distribution(overruns), "outcomes": outcomes, "modes": modes,
            "metadataErrors": sorted(set(errors)),
            "convention": "One wait and selection per scheduled GPU slot; floor-millisecond timeout has no same-slot retry. Wait carries no image. Selection ordering is observed; absence of held keyed mutexes requires code lifetime review."}


def process_cpu_report(summary, frequency):
    fields = ("appCpuSeconds", "appCpuStartQpc", "appCpuEndQpc", "appCpuScope")
    available = any(field in summary for field in fields)
    result = {"available": available, "cpuSeconds": None, "elapsedSeconds": None, "averageCoreUsage": None,
              "startQpc": summary.get("appCpuStartQpc"), "endQpc": summary.get("appCpuEndQpc"),
              "scope": summary.get("appCpuScope"), "metadataErrors": [],
              "convention": "Whole-process CPU across the reported QPC boundaries, not the analysis window. CPU seconds / elapsed seconds approximates average occupied CPU cores; receiver CPU is excluded."}
    if not available:
        return result
    cpu, first, last = (summary.get(field) for field in fields[:3])
    if (isinstance(cpu, bool) or not isinstance(cpu, (int, float)) or not math.isfinite(cpu) or cpu < 0 or
        isinstance(first, bool) or not isinstance(first, int) or isinstance(last, bool) or not isinstance(last, int) or
        first <= 0 or last <= first or not isinstance(summary.get("appCpuScope"), str) or not summary["appCpuScope"]):
        result["metadataErrors"].append("Invalid or incomplete whole-run process CPU metadata")
        return result
    result.update(cpuSeconds=cpu, elapsedSeconds=(last-first)/frequency, averageCoreUsage=cpu*frequency/(last-first))
    return result


def present_readiness_report(events, start, end, frequency, origin, options, image_sources, wait_plan):
    """Replay readiness ownership across the full log; only Present consumes it."""
    starts, ends = {}, {}
    for event in events:
        if event["stage"] in ("present.ready.start", "present.ready.end"):
            target = starts if event["stage"] == "present.ready.start" else ends
            target.setdefault((event.get("worker"), event.get("scheduledQpc", 0)), []).append(event)
    enabled = "presentWaitMs" in options or bool(starts or ends)
    configured = options.get("presentWaitMs")
    errors = []
    if configured is not None and (isinstance(configured, bool) or not isinstance(configured, int) or not 0 <= configured <= 1 or
                                   (configured > 0 and options["output"] not in ("fullscreen", "both"))):
        errors.append("Invalid configured presentWaitMs")
        configured = None
    durations, native_zero, native_positive, overruns, effective_values = [], [], [], [], []
    outcomes = {name: 0 for name in ("ready", "notReady", "retained", "cancelled", "deadline", "error")}
    modes = {"native": 0, "retained": 0}
    pairs = {}
    common_end = origin+int(float(options["seconds"])*frequency)
    for key in starts.keys() | ends.keys():
        left, right = starts.get(key, []), ends.get(key, [])
        if len(left) != 1 or len(right) != 1:
            errors.append("Readiness attempt requires exactly one start/end per worker and scheduledQpc")
            continue
        first, last = left[0], right[0]
        pairs[key] = (first, last)
        mode, outcome, held = first.get("detail"), last.get("detail"), last.get("value")
        timeout = first.get("value")
        segment = next((part for part in wait_plan["segments"] if part["startQpc"] <= first.get("scheduledQpc", 0) < part["endQpc"]), None)
        attempt_configured = segment["waitMs"] if segment is not None else configured
        if isinstance(attempt_configured, bool) or not isinstance(attempt_configured, int) or attempt_configured not in (0, 1):
            attempt_configured = None
        if wait_plan["usesManifestSegments"] and segment is None:
            errors.append("Readiness attempt has no scheduled plan segment")
        if mode not in modes or outcome not in outcomes:
            errors.append("Unknown readiness mode or outcome")
            continue
        if isinstance(held, bool) or held not in (0, 1):
            errors.append("Readiness end must report permission value zero or one")
        if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or timeout != int(timeout) or not 0 <= timeout <= 1 or (attempt_configured is not None and timeout > attempt_configured):
            errors.append("Invalid effective readiness timeout")
            continue
        if mode == "retained" and timeout != 0:
            errors.append("Retained readiness cannot request a native wait")
        if any(first.get(field) != last.get(field) for field in ("imageId", "generatedQpc", "deadlineQpc")):
            errors.append("Readiness pair changed image or deadline metadata")
        source = image_sources.get(first["imageId"], [])
        if first["imageId"] <= 0 or len(source) != 1 or source[0]["generatedQpc"] != first["generatedQpc"] or source[0]["qpc"] > first["qpc"]:
            errors.append("Readiness image requires a unique earlier compose publication")
        deadline = first.get("deadlineQpc")
        if isinstance(deadline, bool) or not isinstance(deadline, int) or deadline <= 0 or deadline > common_end:
            errors.append("Readiness requires a positive deadline no later than common end")
            continue
        scheduled = first.get("scheduledQpc")
        if isinstance(scheduled, bool) or not isinstance(scheduled, int) or scheduled < origin:
            errors.append("Readiness requires a valid GPU scheduledQpc")
        else:
            # Recover the GPU tick index, then apply the same nearest-even round
            # as TickSchedule. Adding a rounded period would drift at fractional fps.
            index = round((scheduled-origin)*float(options["fps"])/frequency)
            expected_scheduled = origin+round(index*frequency/float(options["fps"]))
            expected_deadline = min(origin+round((index+1)*frequency/float(options["fps"])), common_end)
            if scheduled != expected_scheduled or deadline != expected_deadline:
                errors.append("Readiness deadline must match min(next GPU scheduled tick, common end)")
        if first["qpc"] >= deadline:
            errors.append("Expired readiness attempt should have been skipped before starting")
        remaining = math.floor(max(0.0, float(deadline)-float(first["qpc"]))*1000.0/frequency)
        if timeout > remaining:
            errors.append("Effective readiness timeout exceeds remaining whole milliseconds")
        if wait_plan["usesManifestSegments"] and attempt_configured in (0, 1):
            expected_timeout = 0 if mode == "retained" else min(attempt_configured, remaining)
            if timeout != expected_timeout:
                errors.append("Readiness timeout must equal its scheduled segment's effective request")
        if last["qpc"] < first["qpc"]:
            errors.append("Readiness timestamps reversed")
            continue
        if outcome in ("ready", "retained") and (held != 1 or last["qpc"] >= deadline):
            errors.append("Usable readiness requires retained permission and a return before deadline")
        if outcome == "deadline" and last["qpc"] < deadline:
            errors.append("Readiness deadline outcome precedes its deadline")
        if outcome == "notReady" and held != 0:
            errors.append("notReady cannot grant permission")
        if outcome == "notReady" and last["qpc"] >= deadline:
            errors.append("Late notReady return must be classified as deadline or cancelled")
        if mode == "retained" and (held != 1 or outcome not in ("retained", "cancelled", "deadline", "error")):
            errors.append("Retained readiness must preserve the existing permission")
        if mode == "native" and outcome == "retained":
            errors.append("A native readiness attempt cannot report retained reuse")
        if outcome == "error":
            errors.append("Readiness reported an error")
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        duration = (last["qpc"]-first["qpc"])*1000/frequency
        durations.append(duration)
        modes[mode] += 1
        outcomes[outcome] += 1
        effective_values.append(timeout)
        if mode == "native":
            if timeout > 0:
                native_positive.append(duration)
                overruns.append(max(0, duration-timeout))
            else:
                native_zero.append(duration)
    # QPC sorting is stable: same-worker equal timestamps preserve the order
    # recorded by that worker. Readiness obtained outside the window carries in.
    permissions = {}
    vsync = options.get("displayPacing") in ("vsync", "vblank")
    if enabled:
        for event in events:
            stage = event["stage"]
            if stage not in ("present.ready.start", "present.ready.end", "display.draw.start", "present.start", "display.wait.start", "display.wait.end"):
                continue
            if vsync:
                # Vsync/vblank grants come from their own waits; draw/present replay is in vsync_report / vblank_report.
                if stage in ("present.ready.start", "present.ready.end"):
                    errors.append("Vsync/vblank pacing has no present readiness attempts")
                continue
            worker = event.get("worker")
            key = (worker, event.get("scheduledQpc", 0))
            pair = pairs.get(key)
            held = permissions.get(worker, False)
            if stage == "display.wait.start":
                if event.get("detail") == "native" and held:
                    errors.append("Native display wait repeated while permission is retained")
                if event.get("detail") == "retained" and not held:
                    errors.append("Display wait reuse has no retained permission")
            elif stage == "display.wait.end":
                if options.get("displayPacing") == "ready":
                    permissions[worker] = event.get("value") == 1
            elif stage == "present.ready.start":
                if options.get("displayPacing") == "ready" and event.get("detail") != "retained":
                    errors.append("Ready pacing permits only retained present readiness")
                if event.get("detail") == "native" and held:
                    errors.append("Native readiness wait repeated while permission is retained")
                if event.get("detail") == "retained" and not held:
                    errors.append("Readiness reuse has no retained permission")
            elif stage == "present.ready.end":
                permissions[worker] = event.get("value") == 1
            else:
                if pair is None or pair[1]["qpc"] > event["qpc"]:
                    errors.append("Draw/Present requires a matching earlier readiness pair")
                    continue
                completed = pair[1]
                if not held or completed.get("value") != 1 or completed.get("detail") not in ("ready", "retained"):
                    errors.append("Draw/Present started without usable readiness permission")
                deadline = completed.get("deadlineQpc") or 0
                if not isinstance(deadline, int) or event["qpc"] >= deadline:
                    errors.append("Draw/Present started at or after its deadline")
                if any(event.get(field) != completed.get(field) for field in ("imageId", "generatedQpc")):
                    errors.append("Draw/Present changed the readiness image metadata")
                if stage == "present.start":
                    permissions[worker] = False
    window_values = wait_plan["window"]["waitMsValues"]
    return {"available": enabled, "configuredWaitMs": window_values[0] if len(window_values) == 1 else None, "pairedAttemptsInWindow": len(durations),
            "modes": modes, "outcomes": outcomes, "durationMs": distribution(durations),
            "effectiveRequestedTimeoutMs": distribution(effective_values),
            "nativeZeroTimeoutDurationMs": distribution(native_zero),
            "nativePositiveTimeoutDurationMs": distribution(native_positive),
            "positiveTimeoutOverrunMs": distribution(overruns), "positiveTimeoutOverrunCount": sum(value > 0 for value in overruns),
            "metadataErrors": sorted(set(errors)),
            "convention": "Full-log permission replay; native readiness is not retried while retained. Present attempts alone consume permission. Statistics require both endpoints inside [start,end); zero-wait overhead and retained reuse are excluded from positive-request overruns. Guard QPC does not prove hard real-time presentation."}


def selection_report(events, start, end, frequency, options, image_sources):
    """Bound visibility/selection using observable intervals, never exact lock time."""
    visible, starts, ends = {}, {}, {}
    retries, busy_skips = {}, {}
    for event in events:
        if event["stage"] == "compose.visible":
            visible.setdefault(event["imageId"], []).append(event)
        elif event["stage"] in ("send.select.start", "send.select.end"):
            # value is the attempt index: absent/0 first selection, 1 the same-slot copy retry.
            target = starts if event["stage"] == "send.select.start" else ends
            target.setdefault((event.get("worker"), event.get("scheduledQpc", 0), event.get("value") or 0), []).append(event)
        elif event["stage"] == "copy.retry":
            retries.setdefault((event.get("worker"), event.get("scheduledQpc", 0)), []).append(event)
        elif event["stage"] == "skip" and event.get("detail") == "copy.keyedMutexBusy":
            busy_skips.setdefault((event.get("worker"), event.get("scheduledQpc", 0)), []).append(event)
    enabled = "sendPhaseMs" in options or bool(visible or starts or ends)
    errors = []
    phase = options.get("sendPhaseMs")
    if phase is not None and (isinstance(phase, bool) or not isinstance(phase, (int, float)) or not math.isfinite(phase) or not 0 <= phase < 1000/float(options["fps"]) or
                              (phase != 0 and (options.get("mode") != "split" or options["output"] not in ("spout", "both")))):
        errors.append("Invalid configured sendPhaseMs")
    copy_retry = options.get("copyRetry")
    if copy_retry is not None and copy_retry not in ("off", "signal"):
        errors.append("Invalid configured copyRetry")
    if (retries or any(key[2] == 1 for key in starts.keys() | ends.keys())) and copy_retry is not None and copy_retry != "signal":
        errors.append("Retry selections or copy.retry events require copyRetry signal")
    publications = []
    if enabled:
        for image_id in image_sources.keys() | visible.keys():
            before, after = image_sources.get(image_id, []), visible.get(image_id, [])
            if image_id <= 0 or len(before) != 1 or len(after) != 1:
                errors.append("compose.publish/visible must pair exactly once per positive image ID")
                continue
            published, observed = before[0], after[0]
            if any(published.get(field) != observed.get(field) for field in ("worker", "scheduledQpc", "generatedQpc")):
                errors.append("compose.visible changed publication metadata")
            if observed["qpc"] < published["qpc"]:
                errors.append("compose.visible precedes compose.publish")
            publications.append((image_id, published, observed))

    def indexed_candidates(timestamp_index):
        ordered = sorted(publications, key=lambda item: item[timestamp_index]["qpc"])
        highest = []
        for item in ordered:
            highest.append(max(item[0], highest[-1] if highest else 0))
        return [item[timestamp_index]["qpc"] for item in ordered], highest

    visible_times, visible_max_ids = indexed_candidates(2)
    publish_times, publish_max_ids = indexed_candidates(1)
    publication_by_id = {item[0]: item for item in publications}
    rows = []
    completed_pairs = {}
    for key in starts.keys() | ends.keys():
        left, right = starts.get(key, []), ends.get(key, [])
        attempt = key[2]
        if isinstance(attempt, bool) or not isinstance(attempt, int) or attempt not in (0, 1):
            errors.append("Selection attempt value must be 0 (first) or 1 (retry)")
            continue
        if attempt == 1 and (len(left) > 1 or len(right) > 1):
            errors.append("At most one retry selection per worker and scheduledQpc")
        if len(left) != 1 or len(right) != 1:
            errors.append("Selection start/end must pair exactly once per worker, scheduledQpc and attempt")
            continue
        first, last = left[0], right[0]
        completed_pairs.setdefault(key[:2], {})[attempt] = (first, last)
        if first["qpc"] > last["qpc"]:
            errors.append("Selection end precedes start")
            continue
        if first["imageId"] != last["imageId"] or first["generatedQpc"] != last["generatedQpc"]:
            errors.append("Selection pair changed returned image metadata")
        image_id, outcome = last["imageId"], last.get("detail")
        source = publication_by_id.get(image_id)
        if outcome not in ("latest", "retained", "none"):
            errors.append("Unknown selection outcome")
        if outcome == "none":
            if image_id != 0 or first["generatedQpc"] != 0 or last["generatedQpc"] != 0:
                errors.append("Null selection must have zero image ID and generatedQpc")
        elif image_id <= 0 or source is None or source[1]["generatedQpc"] != last["generatedQpc"]:
            errors.append("Selected image must match a unique compose publication")
        elif source[1]["qpc"] > last["qpc"]:
            errors.append("Selected image was published after selection ended")
        # The returned stamp is attached retroactively to the entire selection
        # interval. It may legitimately have been generated AFTER select.start.
        if last["generatedQpc"] > last["qpc"]:
            errors.append("Selected image was generated after selection ended")
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        confirmed_index = bisect.bisect_left(visible_times, first["qpc"])-1
        possible_index = bisect.bisect_right(publish_times, last["qpc"])-1
        confirmed_id = visible_max_ids[confirmed_index] if confirmed_index >= 0 else None
        possible_id = publish_max_ids[possible_index] if possible_index >= 0 else None
        overlap_ids = [item[0] for item in publications if item[0] > image_id and item[1]["qpc"] <= last["qpc"] and item[2]["qpc"] >= first["qpc"]]
        source_overlap = bool(source and source[1]["qpc"] <= last["qpc"] and source[2]["qpc"] >= first["qpc"])
        rows.append({"startSeconds": first["relativeSeconds"], "endSeconds": last["relativeSeconds"],
                     "worker": key[0], "sendScheduledQpc": key[1], "selectedImageId": image_id, "outcome": outcome,
                     "durationMs": (last["qpc"]-first["qpc"])*1000/frequency,
                     "imageAgeAtStartMs": (first["qpc"]-last["generatedQpc"])*1000/frequency if image_id > 0 else None,
                     "imageAgeAtEndMs": (last["qpc"]-last["generatedQpc"])*1000/frequency if image_id > 0 else None,
                     "composeScheduledToSendScheduledMs": (key[1]-source[1].get("scheduledQpc", 0))*1000/frequency if source else None,
                     "composeVisibleToSelectionStartMs": (first["qpc"]-source[2]["qpc"])*1000/frequency if source else None,
                     "composeVisibleToSelectionEndMs": (last["qpc"]-source[2]["qpc"])*1000/frequency if source else None,
                     "definiteCandidateId": confirmed_id, "possibleCandidateId": possible_id,
                     "definiteCandidateIdMinusSelected": confirmed_id-image_id if confirmed_id is not None and image_id > 0 else None,
                     "possibleCandidateIdMinusSelected": possible_id-image_id if possible_id is not None and image_id > 0 else None,
                     "overlappingNewerPublicationIds": overlap_ids, "selectedPublicationOverlaps": source_overlap,
                     "publicationRaceUncertain": bool(overlap_ids or source_overlap or confirmed_id != possible_id),
                     "attempt": attempt})
    # A retry selection is only meaningful as: first pair -> copy.keyedMutexBusy skip
    # -> copy.retry (its own wait) -> retry pair before the retry deadline.
    retry_newer = 0
    for slot, attempts in completed_pairs.items():
        if 1 not in attempts:
            continue
        first_retry, last_retry = attempts[1]
        if 0 not in attempts:
            errors.append("Retry selection requires a completed first selection in the same slot")
            continue
        first_initial, last_initial = attempts[0]
        if last_initial["qpc"] > first_retry["qpc"]:
            errors.append("Retry selection must start after the first selection ended")
        signals = [event for event in retries.get(slot, []) if last_initial["qpc"] <= event["qpc"] <= first_retry["qpc"]]
        if len(signals) != 1:
            errors.append("Retry selection requires exactly one copy.retry between the first selection end and the retry selection start")
        else:
            signal = signals[0]
            deadline = signal.get("deadlineQpc")
            if isinstance(deadline, bool) or not isinstance(deadline, int) or deadline <= 0:
                errors.append("copy.retry requires a positive integer deadlineQpc")
            elif first_retry["qpc"] >= deadline:
                errors.append("Retry selection started at or after the copy.retry deadline")
            if not any(last_initial["qpc"] <= event["qpc"] <= signal["qpc"] for event in busy_skips.get(slot, [])):
                errors.append("Retry selection requires a copy.keyedMutexBusy skip between the first selection end and copy.retry")
        if (start <= first_retry["relativeSeconds"] < end and start <= last_retry["relativeSeconds"] < end
                and last_retry["imageId"] > last_initial["imageId"]):
            retry_newer += 1
    if "sendPhaseMs" in options and options["output"] in ("spout", "both"):
        for sent in (event for event in events if event["stage"] == "send.start"):
            attempts = completed_pairs.get((sent.get("worker"), sent.get("scheduledQpc", 0)))
            pair = attempts[max(attempts)] if attempts else None
            if pair is None or pair[1]["qpc"] > sent["qpc"]:
                errors.append("SendTexture start requires a matching earlier selection pair")
            # No selected-ID == sent-ID requirement: null selections, retained
            # images and failed/busy copies may intentionally send the old held image.
    rows.sort(key=lambda row: row["startSeconds"])
    metrics = {field: distribution([row[field] for row in rows if row[field] is not None]) for field in (
        "durationMs", "imageAgeAtStartMs", "imageAgeAtEndMs", "composeScheduledToSendScheduledMs",
        "composeVisibleToSelectionStartMs", "composeVisibleToSelectionEndMs",
        "definiteCandidateIdMinusSelected", "possibleCandidateIdMinusSelected")}
    return {"available": enabled, "configuredSendPhaseMs": phase,
            "startEventsInWindow": sum(start <= item["relativeSeconds"] < end for group in starts.values() for item in group),
            "endEventsInWindow": sum(start <= item["relativeSeconds"] < end for group in ends.values() for item in group),
            "pairedSelectionsInWindow": len(rows), "outcomes": {name: sum(row["outcome"] == name for row in rows) for name in ("latest", "retained", "none")},
            **metrics, "overlappingNewerPublicationsCount": sum(len(row["overlappingNewerPublicationIds"]) for row in rows),
            "selectionsWithPublicationOverlap": sum(bool(row["overlappingNewerPublicationIds"] or row["selectedPublicationOverlaps"]) for row in rows),
            "publicationRaceUncertainCount": sum(row["publicationRaceUncertain"] for row in rows),
            "selectedOlderThanDefiniteCandidateCount": sum((row["definiteCandidateIdMinusSelected"] or 0) > 0 for row in rows),
            "metadataErrors": sorted(set(errors)), "rows": rows,
            "convention": "Confirmed candidate: compose.visible < selection.start. Possible candidate: compose.publish <= selection.end. Boundary equality/publication intervals overlapping selection are uncertain; none are exact pool linearization times. Start-age and visible-to-selection differences may be negative. Older IDs alone do not diagnose a selection bug.",
            "retryAttemptsInWindow": sum(row["attempt"] == 1 for row in rows), "retrySelectedNewerImage": retry_newer,
            "retryConvention": "Selections pair by (worker, scheduledQpc, attempt) where attempt is the event value: 0 first, 1 the single same-slot copy retry after a copy.keyedMutexBusy skip and one copy.retry, started before that copy.retry deadlineQpc. Retry counts require both endpoints inside [start,end); newer means retry image ID > first image ID."}


def vsync_report(events, start, end, frequency, origin, options):
    """Notification-driven display: one Present per ready grant, strictly increasing ids, no notReady."""
    pacing = options.get("displayPacing", "tick")
    errors = []
    slots = {}
    for event in events:
        if event["stage"] in ("display.vsync.wait.start", "display.vsync.wait.end"):
            slot = slots.setdefault((event.get("worker"), event.get("scheduledQpc")), {"start": [], "end": []})
            slot[event["stage"].rsplit(".", 1)[1]].append(event)
    if slots and pacing != "vsync":
        errors.append("display.vsync.wait events require vsync pacing")
    common_end = origin+int(float(options["seconds"])*frequency)
    pairs = {}
    for key, slot in slots.items():
        if len(slot["start"]) != 1 or len(slot["end"]) != 1:
            errors.append("display.vsync.wait requires exactly one start/end per worker and compose slot")
            continue
        first, last = slot["start"][0], slot["end"][0]
        pairs[key] = (first, last)
        if first.get("worker") != "GPU" or any(item[field] != 0 for item in (first, last) for field in ("imageId", "generatedQpc")):
            errors.append("Vsync wait must be GPU work without a selected image")
        scheduled, deadline = first.get("scheduledQpc"), first.get("deadlineQpc")
        if (isinstance(scheduled, bool) or not isinstance(scheduled, int) or scheduled < origin or isinstance(deadline, bool) or
                not isinstance(deadline, int) or deadline <= 0 or last.get("deadlineQpc") != deadline):
            errors.append("Vsync wait requires a GPU compose slot and one integer deadline")
            continue
        index = round((scheduled-origin)*float(options["fps"])/frequency)
        expected = min(origin+round((index+1)*frequency/float(options["fps"])), common_end)
        if scheduled != origin+round(index*frequency/float(options["fps"])) or deadline != expected:
            errors.append("Vsync wait deadline must equal min(next compose tick, common end)")
        if first["qpc"] < scheduled or last["qpc"] < first["qpc"]:
            errors.append("Vsync wait timestamps precede the slot or are reversed")
        mode, outcome, held, timeout = first.get("detail"), last.get("detail"), last.get("value"), first.get("value")
        if mode != "native" or outcome not in ("ready", "timeout", "cancelled", "error"):
            errors.append("Invalid vsync wait mode/outcome")
        if isinstance(held, bool) or held not in (0, 1):
            errors.append("Vsync wait permission must be zero or one")
        # The probe floors the budget from a clock read slightly before wait.start is stamped; crossing a
        # millisecond boundary in between makes the recorded remainder one whole millisecond smaller.
        expected_timeout = math.floor(max(0.0, float(deadline)-float(first["qpc"]))*1000/frequency)
        if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or timeout not in (expected_timeout, expected_timeout+1) or timeout < 1:
            errors.append("Vsync wait timeout must equal the remaining whole milliseconds (or one more when the budget clock preceded the stamp) and be at least one")
        if outcome == "ready" and held != 1:
            errors.append("Vsync ready outcome must grant permission")
        if outcome != "ready" and held != 0:
            errors.append("Vsync timeout/cancelled/error cannot grant permission")
        if outcome == "error":
            errors.append("Vsync wait reported error")
    presented = []
    if pacing == "vsync":
        held, latest, last_presented = False, 0, 0
        for event in events:
            if event.get("worker") != "GPU":
                continue
            stage = event["stage"]
            if stage == "compose.publish":
                latest = max(latest, event["imageId"])
            elif stage == "display.vsync.wait.start":
                if held:
                    errors.append("Native vsync wait started while a ready grant is retained")
                if latest <= last_presented:
                    errors.append("Vsync wait started without a newer image than the last present")
            elif stage == "display.vsync.wait.end":
                held = event.get("value") == 1
            elif stage in ("display.select.start", "display.draw.start"):
                if not held:
                    errors.append("Vsync selection/draw requires an unconsumed ready grant")
            elif stage == "present.start":
                if not held:
                    errors.append("Present started without an unconsumed ready grant")
                held = False
                if event["imageId"] <= last_presented:
                    errors.append("Vsync presented image ids must strictly increase (no same-id re-present)")
                last_presented = max(last_presented, event["imageId"])
                presented.append(event)
            elif stage == "skip" and event.get("detail") == "present.notReady":
                errors.append("present.notReady cannot occur with vsync pacing")
    durations, requests, overruns, outcomes = [], [], [], {}
    for first, last in pairs.values():
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        elapsed = (last["qpc"]-first["qpc"])*1000/frequency
        durations.append(elapsed)
        outcomes[str(last.get("detail"))] = outcomes.get(str(last.get("detail")), 0)+1
        requested = first.get("value")
        if isinstance(requested, (int, float)) and math.isfinite(requested):
            requests.append(requested)
            overruns.append(max(0, elapsed-requested))
    skips = {}
    for event in events:
        if event["stage"] == "skip" and str(event.get("detail", "")).startswith("display.vsync.") and start <= event["relativeSeconds"] < end:
            skips[event["detail"]] = skips.get(event["detail"], 0)+1
    return {"displayPacing": pacing, "available": pacing == "vsync", "pairedWaitsInWindow": len(durations),
            "durationMs": distribution(durations), "requestedTimeoutMs": distribution(requests),
            "timeoutOverrunMs": distribution(overruns), "outcomes": outcomes,
            "presentsInWindow": sum(start <= event["relativeSeconds"] < end for event in presented), "skips": skips,
            "metadataErrors": sorted(set(errors)),
            "convention": "Full-log replay: a ready grant (display.vsync.wait.end ready/1) permits at most one present.start; no native wait while a grant is retained or without a newer image; presented ids strictly increase. Durations require both endpoints inside [start,end). OS wake-up latency is not bounded."}


def vblank_report(events, start, end, frequency, origin, options, command, manifest):
    """Predicted-vblank display: one present per predicted vblank (time), each predicted present preceded by its prediction."""
    pacing = options.get("displayPacing", "tick")
    margin = options.get("presentMarginMs")
    errors = []
    vblank_events = [event for event in events if str(event["stage"]).startswith("display.vblank.") or
                     (event["stage"] == "skip" and str(event.get("detail", "")).startswith("display.vblank."))]
    if vblank_events and pacing != "vblank":
        errors.append("display.vblank events require vblank pacing")
    if pacing == "vblank":
        if options.get("output") not in ("fullscreen", "both") or options.get("presentWaitMs", 0) != 0 or options.get("presentWaitPlan", "fixed") != "fixed":
            errors.append("Vblank display pacing requires display output, presentWaitMs 0 and fixed plan")
        if isinstance(margin, bool) or not isinstance(margin, (int, float)) or not math.isfinite(margin) or not 0.5 <= margin <= 8:
            errors.append("Vblank display pacing requires a finite presentMarginMs from 0.5 to 8")
    elif margin is not None and margin != 3:
        errors.append("A non-default presentMarginMs requires vblank pacing")
    if command is not None and ("presentMarginMs" in options or "presentMarginMs" in command):
        if "presentMarginMs" not in options or "presentMarginMs" not in command or command["presentMarginMs"] != margin:
            errors.append("command presentMarginMs must explicitly match manifest options")
    # Waits pair sequentially per worker (several waits per compose slot are possible: compose-bound waits,
    # a deferred attempt, the idle guard). Nesting is invalid.
    pairs, open_waits = [], {}
    for event in events:
        stage = event["stage"]
        if stage not in ("display.vblank.wait.start", "display.vblank.wait.end"):
            continue
        worker = event.get("worker")
        if stage == "display.vblank.wait.start":
            if worker in open_waits:
                errors.append("display.vblank.wait.start without an end for the previous wait")
            open_waits[worker] = event
            continue
        first = open_waits.pop(worker, None)
        if first is None:
            errors.append("display.vblank.wait.end without a start")
            continue
        pairs.append((first, event))
    if open_waits:
        errors.append("display.vblank.wait.start without an end")
    for first, last in pairs:
        kind, outcome, requested, lateness, deadline = first.get("detail"), last.get("detail"), first.get("value"), last.get("value"), first.get("deadlineQpc")
        if first.get("worker") != "GPU" or any(item[field] != 0 for item in (first, last) for field in ("imageId", "generatedQpc")):
            errors.append("Vblank wait must be GPU work without a selected image")
        if kind not in ("target", "compose") or outcome not in ("target", "compose", "cancelled", "error") or (outcome in ("target", "compose") and outcome != kind):
            errors.append("Invalid vblank wait kind/outcome")
        if isinstance(deadline, bool) or not isinstance(deadline, int) or deadline <= 0 or last.get("deadlineQpc") != deadline:
            errors.append("Vblank wait requires one positive deadlineQpc")
        if isinstance(requested, bool) or not isinstance(requested, int) or requested < 0:
            errors.append("Vblank wait.start value must be a non-negative requested microsecond count")
        if isinstance(lateness, bool) or not isinstance(lateness, int):
            errors.append("Vblank wait.end value must be an integer lateness in microseconds")
        if last["qpc"] < first["qpc"] or first.get("scheduledQpc") != last.get("scheduledQpc"):
            errors.append("Vblank wait timestamps reversed or slot changed")
        if outcome == "error":
            errors.append("Vblank wait reported error")
    predicts = [event for event in events if event["stage"] == "display.vblank.predict"]
    for event in predicts:
        detail, refresh, vblank_qpc = str(event.get("detail", "")), event.get("value"), event.get("deadlineQpc")
        if event.get("worker") != "GPU" or event["imageId"] <= 0:
            errors.append("display.vblank.predict must be GPU work naming the candidate image")
        if not detail.isdigit() or int(detail) <= 0:
            errors.append("display.vblank.predict detail must be the estimated period in microseconds")
        if isinstance(refresh, bool) or not isinstance(refresh, int) or refresh <= 0 or isinstance(vblank_qpc, bool) or not isinstance(vblank_qpc, int) or vblank_qpc <= 0:
            errors.append("display.vblank.predict requires a positive predicted refresh value and vblank deadlineQpc")
    bootstraps = [event for event in events if event["stage"] == "display.vblank.bootstrap"]
    bootstrap_slots = {}
    for event in bootstraps:
        if event.get("worker") != "GPU" or event.get("value") != 1:
            errors.append("display.vblank.bootstrap must be GPU work with value 1")
        bootstrap_slots.setdefault((event.get("worker"), event.get("scheduledQpc", 0)), []).append(event["qpc"])
    presented, predicted_presents = [], []
    if pacing == "vblank":
        last_prediction, last_presented, last_vblank = None, 0, None
        for event in events:
            if event.get("worker") != "GPU":
                continue
            stage = event["stage"]
            if stage == "display.vblank.predict":
                last_prediction = event
            elif stage == "present.start":
                slot = (event.get("worker"), event.get("scheduledQpc", 0))
                if event["imageId"] <= last_presented:
                    errors.append("Vblank presented image ids must strictly increase (no same-id re-present)")
                last_presented = max(last_presented, event["imageId"])
                presented.append(event)
                if any(qpc <= event["qpc"] for qpc in bootstrap_slots.get(slot, [])):
                    continue
                if last_prediction is None or last_prediction.get("scheduledQpc", 0) != slot[1] or last_prediction["imageId"] != event["imageId"]:
                    errors.append("present.start outside bootstrap requires a preceding display.vblank.predict for the same worker, slot and image")
                    continue
                # De-duplicate by predicted vblank time: refresh labels (predict.value) are informational only, since the
                # statistics' SyncRefreshCount numbering can be one off the gate's labels.
                vblank_qpc, detail = last_prediction.get("deadlineQpc"), str(last_prediction.get("detail", ""))
                half_period = int(detail)*frequency/2_000_000 if detail.isdigit() else 0
                if last_vblank is not None and isinstance(vblank_qpc, int) and vblank_qpc <= last_vblank+half_period:
                    errors.append("At most one present per predicted vblank (predict.deadlineQpc must increase by at least half the period between presented predictions)")
                if isinstance(vblank_qpc, int):
                    last_vblank = vblank_qpc if last_vblank is None else max(last_vblank, vblank_qpc)
                predicted_presents.append((last_prediction, event))
                last_prediction = None
            elif stage == "skip" and event.get("detail") == "present.notReady":
                errors.append("present.notReady cannot occur with vblank pacing")
    # Prediction error: predict -> present.start -> present.return (same worker/slot/image, value=PresentCount) -> present.scanout.
    returns_by_slot, scanout_by_count = {}, {}
    for event in events:
        if event["stage"] == "present.return" and event.get("value"):
            returns_by_slot.setdefault((event.get("worker"), event.get("scheduledQpc", 0), event["imageId"]), []).append(event)
        elif event["stage"] == "present.scanout":
            parts = str(event.get("detail", "")).split(":")
            if len(parts) == 2 and parts[0].isdigit():
                scanout_by_count.setdefault(int(parts[0]), event)
    prediction_errors = []
    for prediction, started in predicted_presents:
        if not start <= started["relativeSeconds"] < end:
            continue
        returned = [item for item in returns_by_slot.get((started.get("worker"), started.get("scheduledQpc", 0), started["imageId"]), []) if item["qpc"] >= started["qpc"]]
        scanout = scanout_by_count.get(returned[0]["value"]) if len(returned) == 1 else None
        if scanout is not None and isinstance(prediction.get("deadlineQpc"), int):
            prediction_errors.append((scanout["deadlineQpc"]-prediction["deadlineQpc"])*1000/frequency)
    outcomes, lateness_ms, requested_ms = {}, [], []
    for first, last in pairs:
        if not (start <= first["relativeSeconds"] < end and start <= last["relativeSeconds"] < end):
            continue
        outcome = str(last.get("detail"))
        outcomes[outcome] = outcomes.get(outcome, 0)+1
        if isinstance(first.get("value"), int):
            requested_ms.append(first["value"]/1000)
        if outcome == "target" and isinstance(first.get("deadlineQpc"), int):
            lateness_ms.append((last["qpc"]-first["deadlineQpc"])*1000/frequency)
    # Informational: presented after the slot's compose deadline (allowed; the guard deadline is the predicted vblank), of which
    # presentsBeforeDueCompose started before any compose scheduled at/after that deadline had started (a passed target with a
    # reachable vblank presents before the due compose). presentedIdGaps: consecutive presented ids differing by more than one.
    # compose align: the slot index and the next tick both carry the rebuilt shared offset (the offset before the slot's
    # compose event for the index, the offset before the present for the next due, which may have moved since).
    offsets = align_offsets(events, frequency) if options.get("composeAlign") == "vblank" else ([], [])
    slot_events = gpu_compose_slots(events)
    def compose_deadline(scheduled, at_qpc):
        slot = slot_events.get(scheduled)
        slot_offset = offset_before(offsets, slot["qpc"]) if slot is not None else 0
        index = round((scheduled-origin-slot_offset)*float(options["fps"])/frequency)
        return min(origin+offset_before(offsets, at_qpc)+round((index+1)*frequency/float(options["fps"])), origin+int(float(options["seconds"])*frequency))
    compose_starts = sorted((event.get("scheduledQpc", 0), event["qpc"]) for event in events if event["stage"] == "compose.start" and event.get("worker") == "GPU")
    earliest_start = [0]*len(compose_starts)  # Earliest compose.start among composes scheduled at/after each index.
    for index in range(len(compose_starts)-1, -1, -1):
        earliest_start[index] = compose_starts[index][1] if index == len(compose_starts)-1 else min(compose_starts[index][1], earliest_start[index+1])
    def before_due_compose(event):
        index = bisect.bisect_left(compose_starts, (compose_deadline(event.get("scheduledQpc", 0), event["qpc"]), -1))
        return index == len(compose_starts) or earliest_start[index] > event["qpc"]
    windowed = [event for event in presented if start <= event["relativeSeconds"] < end]
    after_compose = [event for event in windowed if event["qpc"] >= compose_deadline(event.get("scheduledQpc", 0), event["qpc"])]
    id_gaps = sum(later["imageId"]-earlier["imageId"] > 1 for earlier, later in zip(windowed, windowed[1:]))
    counts = {"noNewerImage": 0, "notReady": 0, "bootstrap": 0}
    for event in events:
        if not start <= event["relativeSeconds"] < end:
            continue
        if event["stage"] == "display.vblank.bootstrap":
            counts["bootstrap"] += 1
        elif event["stage"] == "skip" and event.get("detail") in ("display.vblank.noNewerImage", "display.vblank.notReady"):
            counts[str(event["detail"]).rsplit(".", 1)[1]] += 1
    return {"displayPacing": pacing, "available": pacing == "vblank", "presentMarginMs": margin if pacing == "vblank" else None,
            "highResolutionTimer": manifest.get("highResolutionTimer") if pacing == "vblank" else None,
            "waitsInWindow": sum(outcomes.values()), "waitOutcomes": outcomes,
            "wakeLatenessMs": distribution(lateness_ms), "requestedWaitMs": distribution(requested_ms),
            "predictionErrorMs": distribution(prediction_errors),
            "presentsInWindow": len(windowed),
            "predictedPresentsInWindow": sum(start <= started["relativeSeconds"] < end for _, started in predicted_presents),
            "presentsAfterComposeDeadline": len(after_compose), "presentsBeforeDueCompose": sum(before_due_compose(event) for event in after_compose),
            "presentedIdGaps": id_gaps,
            "counts": counts, "metadataErrors": sorted(set(errors)),
            "convention": "Full-log replay: outside bootstrap slots each GPU present.start follows a display.vblank.predict for the same slot and image; predicted vblanks (predict.deadlineQpc) of presented predictions increase by at least half the period (predict.detail), so at most one present per predicted vblank (predict.value is informational); presented ids strictly increase. Draw/present guards use the predicted vblank, so present.start may follow the compose deadline (presentsAfterComposeDeadline; presentsBeforeDueCompose of them started before the due compose started, as a passed target with a reachable vblank presents first). presentedIdGaps counts consecutive presented ids in the window differing by more than one (informational). wakeLatenessMs = wait.end.qpc - deadlineQpc for target outcomes; predictionErrorMs = present.scanout SyncQPCTime - predict.deadlineQpc for presented frames (via present.return PresentCount). Window membership uses wait/present.start time. Statistics lag one or more vblanks; the estimate is not a physical photon time."}


def compose_align_report(events, start, end, frequency, origin, options, command, manifest):
    """Compose phase alignment: one bounded correction per scanout, rebuilt offset matches every GPU tick, ticks never regress."""
    align, lead, slew = options.get("composeAlign"), options.get("composeLeadMs"), manifest.get("alignSlewMs")
    errors = []
    align_events = [event for event in events if event["stage"] == "compose.align"]
    if align is not None and align not in ("off", "vblank"):
        errors.append("Invalid composeAlign")
    if align == "vblank":
        if options.get("displayPacing") != "vblank" or options.get("output") not in ("fullscreen", "both"):
            errors.append("Vblank compose align requires vblank display pacing with display output")
        if isinstance(lead, bool) or not isinstance(lead, (int, float)) or not math.isfinite(lead) or not 0.5 <= lead <= 8:
            errors.append("Vblank compose align requires a finite composeLeadMs from 0.5 to 8")
        if isinstance(slew, bool) or not isinstance(slew, (int, float)) or not math.isfinite(slew) or slew <= 0:
            errors.append("Vblank compose align requires a positive manifest alignSlewMs")
    elif lead is not None and lead != 1.5:
        errors.append("A non-default composeLeadMs requires vblank compose align")
    if align_events and align != "vblank":
        errors.append("compose.align events require composeAlign vblank")
    if command is not None:
        for field in ("composeAlign", "composeLeadMs"):
            if (field in options or field in command) and (field not in options or field not in command or command[field] != options[field]):
                errors.append("command "+field+" must explicitly match manifest options")
    slew_us = None
    if not errors and align == "vblank":
        slew_us = max(1, round(slew*frequency/1000))*1_000_000//frequency
    first_scanout = next((event["qpc"] for event in events if event["stage"] == "present.scanout"), None)
    pending_scanout = False
    for event in events:
        if event["stage"] == "present.scanout":
            pending_scanout = True
        if event["stage"] != "compose.align":
            continue
        if event.get("worker") != "GPU" or event["imageId"] != 0 or event["generatedQpc"] != 0:
            errors.append("compose.align must be GPU work without an image")
        error_us, detail, wanted = event.get("value"), str(event.get("detail", "")), event.get("deadlineQpc")
        if isinstance(error_us, bool) or not isinstance(error_us, int) or not detail.lstrip("-").isdigit():
            errors.append("compose.align requires an integer phase error value and a whole-microsecond correction detail")
            continue
        correction = int(detail)
        if isinstance(wanted, bool) or not isinstance(wanted, int) or wanted <= 0:
            errors.append("compose.align requires a positive wanted-time deadlineQpc")
        if slew_us is not None and (abs(correction) > slew_us or correction != -max(-slew_us, min(slew_us, error_us))):
            errors.append("compose.align correction must equal -clamp(error, ±slew)")
        if first_scanout is None or event["qpc"] < first_scanout or not pending_scanout:
            errors.append("compose.align requires a preceding present.scanout observation, at most one correction per observation")
        pending_scanout = False
    # Every taken GPU compose tick equals origin + offset (corrections recorded before the tick's event) + round(i*period),
    # and compose.start scheduled times strictly increase: a moved offset never hands out an earlier tick.
    offsets = align_offsets(events, frequency)
    fps = float(options["fps"])
    if align == "vblank":
        for scheduled, event in gpu_compose_slots(events).items():
            if isinstance(scheduled, bool) or not isinstance(scheduled, int):
                errors.append("GPU compose slot requires an integer scheduledQpc")
                continue
            offset = offset_before(offsets, event["qpc"])
            index = round((scheduled-origin-offset)*fps/frequency)
            if index < 0 or scheduled != origin+offset+round(index*frequency/fps):
                errors.append("GPU compose slot does not match the rebuilt schedule offset")
    starts = [event.get("scheduledQpc", 0) for event in events if event["stage"] == "compose.start" and event.get("worker") == "GPU"]
    if any(right <= left for left, right in zip(starts, starts[1:])):
        errors.append("GPU compose.start scheduledQpc must strictly increase")
    def in_window(event):
        return start <= event["relativeSeconds"] < end
    def first_second(event):
        return 0 <= event["relativeSeconds"] < 1
    def abs_error_ms(selected):
        return distribution([abs(event["value"])/1000 for event in selected if isinstance(event.get("value"), int)])
    def correction_ms(selected):
        return sum(int(str(event.get("detail", ""))) for event in selected if str(event.get("detail", "")).lstrip("-").isdigit())/1000
    windowed = [event for event in align_events if in_window(event)]
    early = [event for event in align_events if first_second(event)]
    publishes = [event["qpc"] for event in events if event["stage"] == "compose.publish" and event.get("worker") == "GPU" and in_window(event)]
    compose_period = sum(right-left for left, right in zip(publishes, publishes[1:]))*1000/frequency/(len(publishes)-1) if len(publishes) > 1 else None
    syncs = [event["deadlineQpc"] for event in events if event["stage"] == "present.scanout" and in_window(event) and isinstance(event.get("deadlineQpc"), int) and not isinstance(event.get("deadlineQpc"), bool)]
    steps = sorted(right-left for left, right in zip(syncs, syncs[1:]))
    vblank_period = (steps[len(steps)//2] if len(steps) % 2 else (steps[len(steps)//2-1]+steps[len(steps)//2])/2)*1000/frequency if steps else None
    return {"available": "composeAlign" in options, "composeAlign": align, "composeLeadMs": lead, "alignSlewMs": slew,
            "alignEvents": len(align_events), "alignEventsInFirstSecond": len(early), "alignEventsInWindow": len(windowed),
            "phaseErrorAbsMs": abs_error_ms(windowed), "phaseErrorAbsMsFirstSecond": abs_error_ms(early),
            "correctionSumMs": correction_ms(windowed), "correctionSumWholeLogMs": correction_ms(align_events),
            "finalOffsetMs": offsets[1][-1]*1000/frequency if offsets[1] else 0,
            "composePeriodMs": compose_period, "vblankPeriodMs": vblank_period,
            "periodDifferenceUs": (compose_period-vblank_period)*1000 if compose_period is not None and vblank_period is not None else None,
            "metadataErrors": sorted(set(errors)),
            "convention": "compose.align: qpc=observation, value=phase error e (µs, signed), deadlineQpc=wanted compose time W, detail=applied correction c (µs, signed) = -clamp(e, ±slew); one per present.scanout at most, none before the first. The offset is rebuilt as the running sum of c (µs -> ticks truncated) and every taken GPU compose tick must equal origin + offset-before-its-event + round(i*period); compose.start scheduled times strictly increase. composePeriodMs is the mean compose.publish interval in the window (while aligned it follows the display's actual period); vblankPeriodMs is the median consecutive present.scanout SyncQPCTime step in the window. Old logs: available=false."}


def source_sync_report(events, start, end, options, command):
    """Fence source sync: no keyed mutex evidence; every copy/draw follows its own fence wait."""
    sync = options.get("sourceSync")
    errors = []
    if sync is not None and sync not in ("keyed", "fence"):
        errors.append("Invalid sourceSync")
    if sync == "fence" and (options.get("mode") != "split" or options.get("output") not in ("spout", "both") or options.get("copyRetry") == "signal"):
        errors.append("Fence source sync requires split mode with Spout output and copyRetry off")
    if command is not None and ("sourceSync" in options or "sourceSync" in command):
        if "sourceSync" not in options or "sourceSync" not in command or command["sourceSync"] != sync:
            errors.append("command sourceSync must explicitly match manifest options")
    mutex_stages = ("display.mutex.acquire", "display.mutex.release", "copy.mutex.acquire", "copy.mutex.release")
    busy_details = ("copy.keyedMutexBusy", "copy.keyedMutexBusy.retry", "present.keyedMutexBusy", "compose.keyedMutexBusy")
    mutex_events, busy = 0, {detail: 0 for detail in busy_details}
    fence_waits = {"copy": {}, "display": {}}
    consumers = {"copy": {}, "display": {}}
    fence_in_window = {"copy": 0, "display": 0}
    for event in events:
        stage = event["stage"]
        key = (event.get("worker"), event.get("scheduledQpc", 0), event["imageId"])
        if stage in mutex_stages:
            mutex_events += 1
        elif stage == "skip" and event.get("detail") in busy_details:
            busy[event["detail"]] += 1
        elif stage in ("copy.fence.wait", "display.fence.wait"):
            kind = stage.split(".", 1)[0]
            fence_waits[kind].setdefault(key, []).append(event)
            if start <= event["relativeSeconds"] < end:
                fence_in_window[kind] += 1
            if event["imageId"] <= 0 or event.get("value") != event["imageId"]:
                errors.append("Fence wait value must equal its positive image id")
        elif stage in ("copy.start", "display.draw.start"):
            consumers["copy" if stage == "copy.start" else "display"].setdefault(key, []).append(event)
    if sync == "fence":
        if mutex_events:
            errors.append("Fence source sync must not record keyed mutex acquire/release events")
        if any(busy.values()):
            errors.append("Fence source sync cannot report keyed mutex busy skips")
        for kind, name in (("copy", "copy.start"), ("display", "display.draw.start")):
            for key, uses in consumers[kind].items():
                waits = fence_waits[kind].get(key, [])
                if len(waits) != 1 or any(waits[0]["qpc"] > use["qpc"] for use in uses):
                    errors.append(name+" requires exactly one earlier "+kind+".fence.wait for the same worker, slot and image")
    elif fence_waits["copy"] or fence_waits["display"]:
        errors.append("Fence wait events require fence source sync")
    return {"sourceSync": sync, "available": sync is not None,
            "copyFenceWaitsInWindow": fence_in_window["copy"], "displayFenceWaitsInWindow": fence_in_window["display"],
            "keyedMutexEvents": mutex_events, "keyedMutexBusySkips": busy,
            "metadataErrors": sorted(set(errors)),
            "convention": "Whole-log validation. Fence value == image id. Absence of keyed mutex events proves no CPU exclusion was recorded, not GPU ordering; the reverse hazard remains covered by the CPU lease contract."}


def scanout_report(events, start, end, frequency, image_sources, summary):
    """DXGI frame statistics: present.scanout maps to the present.return whose value is the same PresentCount."""
    returns = [event for event in events if event["stage"] == "present.return" and event["imageId"] > 0]
    scanouts = [event for event in events if event["stage"] == "present.scanout"]
    disjoint = [event for event in events if event["stage"] == "present.stats.disjoint"]
    counted = [event for event in returns if event.get("value")]
    available = bool(scanouts or counted or disjoint)
    result = {"available": available, "scanoutsInWindow": 0, "mappedInWindow": 0, "unmappedInWindow": 0,
              "unmappedCount": 0, "disjointCount": len(disjoint), "presentReturnsWithCount": len(counted),
              "appScanoutPending": summary.get("scanoutPending"), "appScanoutDisjoint": summary.get("scanoutDisjoint"),
              "presentStartToSyncMs": distribution([]), "generatedToSyncMs": distribution([]),
              "composePublishToSyncMs": distribution([]), "observationLagMs": distribution([]),
              "syncRefreshStep": {"notOneCount": 0, "counts": {}}, "presentRefreshMinusSyncRefreshCounts": {},
              "metadataErrors": [],
              "convention": "PresentCount pairs present.return.value with present.scanout detail PresentCount:PresentRefreshCount[:unmapped]; value=SyncRefreshCount, deadlineQpc=SyncQPCTime (vblank QPC), qpc=observation time. Differences use SyncQPCTime; PresentRefreshCount!=SyncRefreshCount means the vblank of the present is offset from SyncQPCTime. Window membership uses observation time. Statistics lag one or more vblanks and, in windowed mode, describe DWM's flip of the frame, not a measured photon time."}
    if not available:
        return result
    errors = []
    by_count, last = {}, 0
    for event in returns:
        count = event.get("value")
        if isinstance(count, bool) or not isinstance(count, int) or count <= 0:
            errors.append("present.return requires a positive integer PresentCount value in a scanout-instrumented log")
            continue
        if count <= last:
            errors.append("present.return PresentCount values must strictly increase")
        last = max(last, count)
        if count in by_count:
            errors.append("Duplicate present.return PresentCount")
        by_count.setdefault(count, event)
    starts = {}
    for event in events:
        if event["stage"] == "present.start":
            starts.setdefault((event.get("worker"), event.get("scheduledQpc", 0), event["imageId"]), []).append(event)
    last_count = 0
    rows, steps, refresh_offsets = [], [], {}
    previous_sync_refresh = None
    for event in scanouts:
        parts = str(event.get("detail", "")).split(":")
        if len(parts) not in (2, 3) or not all(part.isdigit() for part in parts[:2]) or (len(parts) == 3 and parts[2] != "unmapped"):
            errors.append("Invalid present.scanout detail (expected PresentCount:PresentRefreshCount[:unmapped])")
            continue
        count, present_refresh, mapped = int(parts[0]), int(parts[1]), len(parts) == 2
        sync_refresh, sync_qpc = event.get("value"), event.get("deadlineQpc")
        if any(isinstance(field, bool) or not isinstance(field, int) for field in (sync_refresh, sync_qpc)) or sync_qpc <= 0 or sync_refresh < 0:
            errors.append("present.scanout requires an integer SyncRefreshCount value and a positive SyncQPCTime deadlineQpc")
            continue
        if event.get("worker") != "GPU":
            errors.append("present.scanout must be recorded by the GPU worker")
        if count <= last_count:
            errors.append("Duplicate present.scanout PresentCount" if count == last_count else "present.scanout PresentCount regression")
        last_count = max(last_count, count)
        returned = by_count.get(count)
        if mapped:
            if returned is None:
                errors.append("Mapped present.scanout has no present.return with the same PresentCount")
                continue
            if returned["qpc"] > event["qpc"]:
                errors.append("present.scanout observed before its present.return")
            if returned["imageId"] != event["imageId"] or returned["generatedQpc"] != event["generatedQpc"]:
                errors.append("present.scanout image differs from its present.return")
        else:
            result["unmappedCount"] += 1
            if event["imageId"] != 0 or event["generatedQpc"] != 0:
                errors.append("Unmapped present.scanout must carry zero image metadata")
        if not start <= event["relativeSeconds"] < end:
            continue
        result["scanoutsInWindow"] += 1
        if previous_sync_refresh is not None:
            steps.append(sync_refresh-previous_sync_refresh)
        previous_sync_refresh = sync_refresh
        offset = str(present_refresh-sync_refresh)
        refresh_offsets[offset] = refresh_offsets.get(offset, 0)+1
        if not mapped:
            result["unmappedInWindow"] += 1
            continue
        result["mappedInWindow"] += 1
        started = [item for item in starts.get((returned.get("worker"), returned.get("scheduledQpc", 0), returned["imageId"]), []) if item["qpc"] <= returned["qpc"]]
        source = image_sources.get(event["imageId"], [])
        rows.append({"presentStartToSyncMs": (sync_qpc-started[0]["qpc"])*1000/frequency if len(started) == 1 else None,
                     "generatedToSyncMs": (sync_qpc-event["generatedQpc"])*1000/frequency,
                     "composePublishToSyncMs": (sync_qpc-source[0]["qpc"])*1000/frequency if len(source) == 1 else None,
                     "observationLagMs": (event["qpc"]-sync_qpc)*1000/frequency})
    for field in ("presentStartToSyncMs", "generatedToSyncMs", "composePublishToSyncMs", "observationLagMs"):
        result[field] = distribution([row[field] for row in rows if row[field] is not None])
    result["syncRefreshStep"] = {"notOneCount": sum(step != 1 for step in steps),
                                 "counts": {str(step): steps.count(step) for step in sorted(set(steps))}}
    result["presentRefreshMinusSyncRefreshCounts"] = refresh_offsets
    result["metadataErrors"] = sorted(set(errors))
    return result


def source_report(events, start, end, options, summary):
    """--source contract-fake: source.acquire outcomes (imageId = source sequence, generatedQpc = decode tick, value = position in
    microseconds, detail = Ready|NotReady|Ended) and compose.sourceNotReady skips in the window; sourceDiagnostics passed through
    from the app summary. Old logs (no `source` option, no source.acquire events) report available=false unchanged."""
    source = options.get("source")
    acquires = [event for event in events if event["stage"] == "source.acquire"]
    errors = []
    if acquires and source != "contract-fake":
        errors.append("source.acquire events require the contract-fake source option")
    outcomes = {"Ready": 0, "NotReady": 0, "Ended": 0}
    for event in acquires:
        detail = event.get("detail")
        if detail not in outcomes or event.get("worker") != "GPU":
            errors.append("source.acquire requires GPU worker and detail Ready, NotReady, or Ended")
            continue
        if (detail == "Ready") != (event["imageId"] > 0 and event["generatedQpc"] > 0):
            errors.append("source.acquire Ready carries a source sequence and decode time; other outcomes carry none")
        if start <= event["relativeSeconds"] < end:
            outcomes[detail] += 1
    not_ready_skips = sum(1 for event in events if event["stage"] == "skip" and event.get("detail") == "compose.sourceNotReady" and start <= event["relativeSeconds"] < end)
    return {"source": source, "available": source == "contract-fake", "acquiresInWindow": sum(outcomes.values()), "outcomes": outcomes,
            "sourceNotReadySkipsInWindow": not_ready_skips, "sourceDiagnostics": summary.get("sourceDiagnostics"), "metadataErrors": sorted(set(errors))}


def analyze(log_directory: Path, start_seconds=None, end_seconds=None):
    manifest = read_json(log_directory / "manifest.json")
    summary = read_json(log_directory / "summary.json")
    if manifest.get("schemaVersion") != 1:
        raise ValueError("Unsupported or missing manifest schemaVersion (expected 1)")
    frequency = int(manifest["qpcFrequency"])
    origin = int(manifest["originQpc"])
    if frequency <= 0:
        raise ValueError("qpcFrequency must be positive")
    options = manifest["options"]
    duration = float(options["seconds"])
    warmup = float(options["warmup"])
    start = warmup if start_seconds is None else start_seconds
    end = duration - warmup if end_seconds is None else end_seconds
    if not (0 <= start < end <= duration):
        raise ValueError("Analysis window must satisfy 0 <= start < end <= duration")
    reasons = []
    events = []
    with (log_directory / "events.jsonl").open(encoding="utf-8-sig") as stream:
        for number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                event = json.loads(line)
                if not isinstance(event.get("stage"), str):
                    raise ValueError("stage must be a string")
                event["qpc"] = int(event["qpc"])
                event["imageId"] = int(event.get("imageId") or 0)
                event["generatedQpc"] = int(event.get("generatedQpc") or 0)
                event["relativeSeconds"] = (event["qpc"] - origin) / frequency
                events.append(event)
            except (ValueError, TypeError, KeyError) as error:
                reasons.append(f"Malformed event line {number}: {error}")
    # Workers may log in interleaved order. QPC remains the ordering authority.
    events.sort(key=lambda item: item["qpc"])
    if not events:
        reasons.append("No events")
    if summary.get("validPerformanceResult") is not True:
        reasons.append("App summary did not mark validPerformanceResult=true")
    if summary.get("outcome") != "completed":
        reasons.append("App outcome is not completed")
    if summary.get("droppedEvents") != 0:
        reasons.append("Missing/nonzero droppedEvents in app summary")
    errors = [event for event in events if event["stage"] == "error"]
    if errors:
        reasons.append(f"App reported {len(errors)} error event(s), including outside analysis window")
    if any("drop" in event["stage"].lower() for event in events):
        reasons.append("App reported an event-drop stage")
    image_sources = {}
    for event in events:
        if event["stage"] == "compose.publish" and event["imageId"] > 0:
            image_sources.setdefault(event["imageId"], []).append(event)
    for stage in ("present.return", "send.publish"):
        published = [event for event in events if event["stage"] == stage and event["imageId"] > 0]
        if any(right["imageId"] < left["imageId"] for left, right in zip(published, published[1:])):
            reasons.append(f"Image ID regression in {stage}")
        bad_ids = set()
        for event in published:
            matches = image_sources.get(event["imageId"], [])
            if len(matches) != 1 or matches[0]["generatedQpc"] != event["generatedQpc"] or matches[0]["qpc"] > event["qpc"]:
                bad_ids.add(event["imageId"])
        if bad_ids:
            reasons.append(f"{stage} references {len(bad_ids)} image ID(s) without exactly one earlier compose.publish and matching generatedQpc")
    window = [event for event in events if start <= event["relativeSeconds"] < end]
    if not events or events[-1]["relativeSeconds"] < end:
        reasons.append("Event log does not cover the end of the requested window")

    # A runner directory is optional for synthetic/offline logs, but supplied runner
    # failures and missing receiver coverage must never be ignored.
    runner_path = log_directory.parent / "runner-result.json"
    runner = read_json(runner_path) if runner_path.exists() else None
    command_path = log_directory.parent / "command.json"
    command = read_json(command_path) if command_path.exists() else None
    if command is not None and ("presentWaitMs" in options or "presentWaitMs" in command):
        if "presentWaitMs" not in options or "presentWaitMs" not in command or command["presentWaitMs"] != options["presentWaitMs"]:
            reasons.append("command presentWaitMs must explicitly match manifest options")
        command_wait = command.get("presentWaitMs")
        if isinstance(command_wait, bool) or not isinstance(command_wait, int) or not 0 <= command_wait <= 1:
            reasons.append("Invalid command presentWaitMs")
    runner_mode = runner.get("receiverMode", "official") if runner is not None else "official"
    command_mode = command.get("receiverMode", "official") if command is not None else "official"
    if runner_mode not in ("official", "none") or command_mode not in ("official", "none"):
        reasons.append("Unknown receiverMode in command or runner result")
    if runner_mode != command_mode:
        reasons.append("command and runner receiverMode disagree; none requires both explicit records")
    # Missing metadata never converts a failed official-receiver trial into a
    # successful no-receiver control. Legacy logs default to official.
    receiver_mode = "none" if runner_mode == "none" and command_mode == "none" else "official"
    measurement_kind = "sender-control-without-owned-receiver" if receiver_mode == "none" else "output-probe"
    receiver_coverage = None
    if runner is not None:
        if runner.get("completedNormally") is not True:
            reasons.append("Runner did not complete normally")
        if receiver_mode == "none":
            if options["output"] not in ("spout", "both"):
                reasons.append("ReceiverMode none requires Spout output to measure the sender control")
            if "receiver" not in runner or runner["receiver"] is not None:
                reasons.append("ReceiverMode none requires an explicit null receiver")
            if runner.get("receiverExit") is not None:
                reasons.append("ReceiverMode none cannot have a receiver exit code")
            if runner.get("receiverForced") is True or (runner.get("receiverCloseMessages") or 0) > 0 or runner.get("receiverBindingVerified") is True:
                reasons.append("ReceiverMode none contradicts receiver cleanup or binding evidence")
            sample_path = log_directory.parent / "receiver-samples.json"
            samples = read_json(sample_path) if sample_path.exists() else None
            if not isinstance(samples, list) or samples:
                reasons.append("ReceiverMode none requires an existing empty receiver-samples array")
            if (log_directory.parent / "receiver-owned.json").exists():
                reasons.append("ReceiverMode none contradicts a receiver-owned record")
        elif options["output"] in ("spout", "both"):
            receiver = runner.get("receiver") or {}
            sample_path = log_directory.parent / "receiver-samples.json"
            samples = read_json(sample_path) if sample_path.exists() else []
            if isinstance(samples, dict):
                samples = [samples]
            first = receiver.get("observedAliveQpc")
            last = max((sample["qpc"] for sample in samples if sample.get("alive") is True), default=None)
            receiver_coverage = {"firstObservedSeconds": (first-origin)/frequency if first else None,
                                 "lastObservedSeconds": (last-origin)/frequency if last else None,
                                 "bindingVerified": runner.get("receiverBindingVerified") is True}
            if not first or first > origin + start * frequency or not last or last < origin + end * frequency:
                reasons.append("Owned receiver was not observed alive across the complete analysis window")

    stages = sorted({event["stage"] for event in window})
    metrics = {}
    for stage in stages:
        selected = [event for event in window if event["stage"] == stage]
        intervals = [(right["qpc"]-left["qpc"])*1000/frequency for left, right in zip(selected, selected[1:])]
        ages = [(event["qpc"]-event["generatedQpc"])*1000/frequency for event in selected if event["generatedQpc"] > 0]
        image_ids = [event["imageId"] for event in selected if event["imageId"] > 0]
        if stage not in ("send.select.start", "display.select.start") and any(age < 0 for age in ages):
            reasons.append(f"Negative image age in {stage}")
        metrics[stage] = {"count": len(selected), "rateHz": len(selected)/(end-start),
                          "intervalMs": distribution(intervals), "imageAgeMs": distribution(ages),
                          "distinctImageIds": len(set(image_ids)),
                          "repeatedImageEvents": len(image_ids)-len(set(image_ids)),
                          "consecutiveResends": sum(left == right for left, right in zip(image_ids, image_ids[1:])),
                          "imageIdRegressions": sum(right < left for left, right in zip(image_ids, image_ids[1:])),
                          "forwardImageIdGaps": sum(max(0, right-left-1) for left, right in zip(image_ids, image_ids[1:]))}
        if stage in ("compose.publish", "present.return", "send.publish"):
            # Only complete 1-second bins; a custom fractional tail is excluded.
            bins = [sum(start+index <= event["relativeSeconds"] < start+index+1 for event in selected)
                    for index in range(math.floor(end-start))]
            metrics[stage]["oneSecondPublicationCounts"] = {
                "counts": bins, "min": min(bins) if bins else None, "max": max(bins) if bins else None,
                "startSeconds": start, "excludedTailSeconds": end-start-len(bins)}
    expected = ["compose.publish"]
    if options["output"] in ("fullscreen", "both"):
        expected.append("present.return")
    if options["output"] in ("spout", "both"):
        expected.append("send.publish")
    for stage in expected:
        if stage not in metrics:
            reasons.append(f"No {stage} events in analysis window")
    skips = {}
    for event in window:
        if event["stage"] != "skip":
            continue
        reason = f"{event.get('worker', '')}:{event.get('detail', '')}"
        entry = skips.setdefault(reason, {"events": 0, "skippedCount": 0})
        entry["events"] += 1
        entry["skippedCount"] += float(event.get("value") or 1)

    # As-of comparison: each successful publication versus the most recent
    # publication by the other output. This is not physical display synchronization.
    present = [event for event in events if event["stage"] == "present.return" and event["imageId"] > 0]
    sends = [event for event in events if event["stage"] == "send.publish" and event["imageId"] > 0]
    present_times = [event["qpc"] for event in present]
    gap_rows = []
    for event in sends:
        if not start <= event["relativeSeconds"] < end:
            continue
        position = bisect.bisect_right(present_times, event["qpc"])-1
        if position < 0:
            continue
        other = present[position]
        gap_rows.append({"seconds": event["relativeSeconds"], "sendImageId": event["imageId"],
                         "lastPresentImageId": other["imageId"], "signedIdDifference": event["imageId"]-other["imageId"],
                         "millisecondsSincePresent": (event["qpc"]-other["qpc"])*1000/frequency})

    durations = pair_durations(events, start, end, frequency)
    for name, paired in durations.items():
        if paired["duplicateKeys"] or paired["reversedPairs"]:
            reasons.append(f"Ambiguous or reversed phase pairing in {name}")
    acquisition = acquisition_report(events, start, end, frequency, options, image_sources)
    reasons.extend(acquisition["metadataErrors"])
    selection = selection_report(events, start, end, frequency, options, image_sources)
    reasons.extend(selection["metadataErrors"])
    wait_plan = present_wait_plan_report(manifest, command, events, start, end)
    reasons.extend(wait_plan["metadataErrors"])
    readiness = present_readiness_report(events, start, end, frequency, origin, options, image_sources, wait_plan)
    reasons.extend(readiness["metadataErrors"])
    display_wait = display_wait_report(events, start, end, frequency, origin, options, command, image_sources)
    reasons.extend(display_wait["metadataErrors"])
    process_cpu = process_cpu_report(summary, frequency)
    reasons.extend(process_cpu["metadataErrors"])
    vsync = vsync_report(events, start, end, frequency, origin, options)
    reasons.extend(vsync["metadataErrors"])
    vblank = vblank_report(events, start, end, frequency, origin, options, command, manifest)
    reasons.extend(vblank["metadataErrors"])
    compose_align = compose_align_report(events, start, end, frequency, origin, options, command, manifest)
    reasons.extend(compose_align["metadataErrors"])
    source_sync = source_sync_report(events, start, end, options, command)
    reasons.extend(source_sync["metadataErrors"])
    scanout = scanout_report(events, start, end, frequency, image_sources, summary)
    reasons.extend(scanout["metadataErrors"])
    source = source_report(events, start, end, options, summary)
    reasons.extend(source["metadataErrors"])
    result = {"schemaVersion": 1, "sourceDirectory": str(log_directory.resolve()),
              "analysisStartSeconds": start, "analysisEndSeconds": end, "windowSeconds": end-start,
              "intervalConvention": "Adjacent events both inside [start,end); percentile nearest rank ceil(n*p)",
              "targetFps": options["fps"], "options": options,
              "validPerformanceResult": not reasons, "invalidReasons": reasons,
              "appOutcome": summary.get("outcome"), "appDroppedEvents": summary.get("droppedEvents"),
              "runnerAvailable": runner is not None, "receiverMode": receiver_mode,
              "measurementKind": measurement_kind, "receiverCoverage": receiver_coverage,
              "presentWaitPlan": wait_plan["plan"], "presentWaitSegments": wait_plan["segments"],
              "presentWaitWindow": wait_plan["window"], "presentWaitPlanValidation": {key: value for key, value in wait_plan.items() if key not in ("plan", "segments", "window")},
              "metrics": metrics, "phaseDurations": durations, "acquisition": acquisition, "selection": selection,
              "presentReadiness": readiness, "displayPacing": display_wait["displayPacing"],
              "displayWait": display_wait, "vsync": vsync, "vblank": vblank, "composeAlign": compose_align, "sourceSync": source_sync, "scanout": scanout, "source": source,
              "placement": manifest.get("placement"),  # contract-fake canvas placement (source size, canvas, fitId, destination, sourceCrop); passed through, not validated.
              "processCpu": process_cpu, "loopTimerHighResolution": manifest.get("loopTimerHighResolution"), "skips": skips, "errorEvents": errors,
              "outputIdDifferenceAtSendPublish": distribution([abs(row["signedIdDifference"]) for row in gap_rows]),
              "limitations": ["Present return and Spout publication do not prove unique images received or physical display refresh.",
                              "Frame IDs identify generated images, not decoded mpv frames.",
                              "Rates count events including resends; distinctImageIds is reported separately.",
                              "No optical/receiver pixel verification. Official receiver binding may be unverified." if receiver_mode == "official" else "No receiver pixel verification was performed in this control.",
                              "GPU-generated patterns do not measure mpv decode/upload performance."]}
    if receiver_mode == "none":
        result["limitations"].append("No owned receiver was launched. This does not prove other applications are disconnected and is not receiver performance validation.")
    return result, gap_rows


def save_result(result, gaps, output: Path):
    output.mkdir(parents=True, exist_ok=False)
    with (output / "analysis.json").open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2, allow_nan=False)
    columns = ["stage", "count", "rateHz", "distinctImageIds", "consecutiveResends", "imageIdRegressions", "forwardImageIdGaps", "intervalP95Ms", "intervalP99Ms", "intervalMaxMs", "imageAgeP95Ms", "imageAgeP99Ms", "imageAgeMaxMs"]
    with (output / "metrics.csv").open("x", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns)
        writer.writeheader()
        for stage, metric in result["metrics"].items():
            row = {key: metric[key] for key in columns if key in metric}
            row.update(stage=stage, intervalP95Ms=metric["intervalMs"]["p95"], intervalP99Ms=metric["intervalMs"]["p99"], intervalMaxMs=metric["intervalMs"]["max"], imageAgeP95Ms=metric["imageAgeMs"]["p95"], imageAgeP99Ms=metric["imageAgeMs"]["p99"], imageAgeMaxMs=metric["imageAgeMs"]["max"])
            writer.writerow(row)

    if result["selection"]["available"]:
        selection_rows = result["selection"]["rows"]
        # Keep old logs' four-file output unchanged; new phase logs add this table.
        fields = list(selection_rows[0]) if selection_rows else ["startSeconds", "endSeconds", "selectedImageId", "outcome"]
        with (output / "selection.csv").open("x", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(selection_rows)
    with (output / "output-id-differences.csv").open("x", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["seconds", "sendImageId", "lastPresentImageId", "signedIdDifference", "millisecondsSincePresent"])
        writer.writeheader()
        writer.writerows(gaps)
    with (output / "phase-durations.csv").open("x", encoding="utf-8-sig", newline="") as stream:
        fields = ["phase", "count", "meanMs", "p95Ms", "p99Ms", "maxMs", "missingStartKeys", "missingEndKeys", "duplicateKeys", "reversedPairs", "omittedBoundaryPairs", "omittedOutsidePairs"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for name, phase in result["phaseDurations"].items():
            row = {key: phase[key] for key in fields if key in phase}
            row.update(phase=name, count=phase["durationMs"]["count"], meanMs=phase["durationMs"]["mean"], p95Ms=phase["durationMs"]["p95"], p99Ms=phase["durationMs"]["p99"], maxMs=phase["durationMs"]["max"])
            writer.writerow(row)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log_directory", type=Path, help="App log directory containing manifest, events and summary")
    parser.add_argument("--start", type=float, help="Inclusive seconds from manifest originQpc; default warmup")
    parser.add_argument("--end", type=float, help="Exclusive seconds; default seconds-warmup")
    parser.add_argument("--output", type=Path, help="New directory, must not already exist")
    args = parser.parse_args()
    result, gaps = analyze(args.log_directory, args.start, args.end)
    target = args.output or args.log_directory.parent / ("analysis-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex)
    save_result(result, gaps, target)
    print(json.dumps({"output": str(target.resolve()), "validPerformanceResult": result["validPerformanceResult"], "invalidReasons": result["invalidReasons"]}, ensure_ascii=False))
    return 0 if result["validPerformanceResult"] else 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError) as exception:
        print(f"Analysis failed: {exception}", file=sys.stderr)
        sys.exit(1)
