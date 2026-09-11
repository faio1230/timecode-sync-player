"""Synthetic log tests only: no GPU, receiver, process launch or installed settings."""
import json
import math
from pathlib import Path
import tempfile
import unittest

from analyze_probe import analyze, percentile, save_result


class ProbeAnalysisTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="gpu-probe-analysis-test-")
        self.root = Path(self.temp.name)
        self.logs = self.root / "app"
        self.logs.mkdir()
        self.manifest = {"schemaVersion": 1, "originQpc": 1000000, "qpcFrequency": 60000,
                         "options": {"seconds": 32, "warmup": 5, "fps": 60, "output": "both", "mode": "split"}}
        self.summary = {"outcome": "completed", "validPerformanceResult": True, "droppedEvents": 0}
        self.events = []
        # Include events exactly on both analysis boundaries. 1320 events belong
        # to [5,27), proving inclusion/exclusion independently of wall-clock tests.
        for index in range(32*60+1):
            tick = 1000000+index*1000
            for stage in ("compose.publish", "present.return", "send.publish"):
                self.events.append({"stage": stage, "worker": "test", "qpc": tick,
                                    "scheduledQpc": tick, "generatedQpc": tick-60, "imageId": index+1})
        self.write()

    def tearDown(self):
        self.temp.cleanup()

    def write(self):
        (self.logs / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.logs / "summary.json").write_text(json.dumps(self.summary), encoding="utf-8")
        (self.logs / "events.jsonl").write_text("\n".join(json.dumps(event) for event in self.events)+"\n", encoding="utf-8")

    def add_acquisition(self, seconds, requested_ms, actual_ms, outcome="acquired", deadline_ms=16, worker="acquire-test"):
        frequency = self.manifest["qpcFrequency"]
        tick = round(self.manifest["originQpc"]+seconds*frequency)
        source = next(event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == 1)
        common = {"worker": worker, "scheduledQpc": tick, "imageId": 1, "generatedQpc": source["generatedQpc"]}
        if deadline_ms is not None:
            common["deadlineQpc"] = tick+round(deadline_ms*frequency/1000)
        self.events.extend([
            dict(common, stage="send.acquire.start", qpc=tick, value=requested_ms),
            dict(common, stage="send.acquire.end", qpc=tick+round(actual_ms*frequency/1000), detail=outcome)])
        return common

    def add_phase_metadata(self, phase_ms=0):
        self.manifest["options"]["sendPhaseMs"] = phase_ms
        self.events.extend(dict(event, stage="compose.visible", qpc=event["qpc"]+10)
                           for event in list(self.events) if event["stage"] == "compose.publish")

    def add_selection(self, first_qpc, last_qpc, image_id, outcome="latest", scheduled_qpc=None, attempt=None):
        source = next((event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == image_id), None)
        common = {"worker": "Spout", "scheduledQpc": first_qpc if scheduled_qpc is None else scheduled_qpc,
                  "imageId": image_id, "generatedQpc": source["generatedQpc"] if source else 0}
        if attempt is not None:  # None keeps the old-log shape without a value field.
            common["value"] = attempt
        self.events.extend([dict(common, stage="send.select.start", qpc=first_qpc),
                            dict(common, stage="send.select.end", qpc=last_qpc, detail=outcome)])
        return common

    def add_readiness(self, seconds, requested_ms=1, actual_ms=.2, outcome="ready", permission=1,
                      mode="native", image_id=1, scheduled_qpc=None, deadline_qpc=None):
        origin, frequency, fps = self.manifest["originQpc"], self.manifest["qpcFrequency"], self.manifest["options"]["fps"]
        first = round(origin+seconds*frequency)
        index = math.floor(seconds*fps) if scheduled_qpc is None else round((scheduled_qpc-origin)*fps/frequency)
        scheduled = origin+round(index*frequency/fps) if scheduled_qpc is None else scheduled_qpc
        deadline = min(origin+round((index+1)*frequency/fps), origin+int(self.manifest["options"]["seconds"]*frequency)) if deadline_qpc is None else deadline_qpc
        source = next(event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == image_id)
        common = {"worker": "GPU", "scheduledQpc": scheduled, "imageId": image_id,
                  "generatedQpc": source["generatedQpc"], "deadlineQpc": deadline}
        self.events.extend([dict(common, stage="present.ready.start", qpc=first, value=requested_ms, detail=mode),
                            dict(common, stage="present.ready.end", qpc=first+round(actual_ms*frequency/1000), value=permission, detail=outcome)])
        return common, first

    def add_wait_plan(self, plan="abba", wait_ms=0):
        options = self.manifest["options"]
        options.update(presentWaitPlan=plan, presentWaitMs=wait_ms, warmup=2)
        origin, frequency, duration = self.manifest["originQpc"], self.manifest["qpcFrequency"], options["seconds"]
        waits = [0, 1, 1, 0] if plan == "abba" else [1, 0, 0, 1] if plan == "baab" else [wait_ms]
        length = duration/len(waits)
        segments = [{"index": index, "waitMs": wait, "startSeconds": index*length, "endSeconds": (index+1)*length,
                     "analysisStartSeconds": index*length+2, "analysisEndSeconds": (index+1)*length-2,
                     "startQpc": origin+int(index*length*frequency), "endQpc": origin+int((index+1)*length*frequency)}
                    for index, wait in enumerate(waits)]
        self.manifest["presentWaitSegments"] = segments
        # This fixture's GPU work has the real worker name for application-event
        # validation. Retain an explicit completion event instead of publishing at end.
        self.events = [event for event in self.events if event["qpc"] < origin+int(duration*frequency)]
        for event in self.events:
            if event["stage"] == "compose.publish":
                event["worker"] = "GPU"
        self.events.append({"stage": "lifecycle", "worker": "GPU", "qpc": origin+int(duration*frequency), "detail": "finished"})
        for segment in segments:
            self.events.append({"stage": "present.wait.segment", "worker": "GPU", "qpc": segment["startQpc"],
                                "scheduledQpc": segment["startQpc"], "deadlineQpc": segment["endQpc"],
                                "imageId": 0, "generatedQpc": 0, "detail": str(segment["index"]), "value": segment["waitMs"]})

    def test_exact_window_rate_age_and_id_comparison(self):
        result, gaps = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        metric = result["metrics"]["send.publish"]
        self.assertEqual(metric["count"], 1320)
        self.assertEqual(metric["rateHz"], 60)
        self.assertEqual(metric["distinctImageIds"], 1320)
        self.assertEqual(metric["imageAgeMs"]["max"], 1)
        self.assertEqual(metric["oneSecondPublicationCounts"]["min"], 60)
        self.assertEqual(metric["oneSecondPublicationCounts"]["max"], 60)
        self.assertAlmostEqual(metric["intervalMs"]["p99"], 1000/60)
        self.assertEqual(len(gaps), 1320)
        self.assertTrue(all(row["signedIdDifference"] == 0 for row in gaps))
        self.assertFalse(result["acquisition"]["available"])
        self.assertIsNone(result["acquisition"]["configuredWaitMs"])
        self.assertFalse(result["selection"]["available"])
        self.assertFalse(result["presentReadiness"]["available"])

    def enable_display_pacing(self, pacing="ready"):
        self.add_wait_plan("fixed")
        self.manifest["options"]["displayPacing"] = pacing
        self.events.extend(dict(event, stage="compose.visible", qpc=event["qpc"]+1)
                           for event in list(self.events) if event["stage"] == "compose.publish")

    def add_display_wait(self, seconds=6, duration_ms=2, mode="native", outcome="ready", permission=1):
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        scheduled = origin+round(seconds*frequency)
        first, deadline = scheduled+60, scheduled+1000
        common = {"worker": "GPU", "scheduledQpc": scheduled, "deadlineQpc": deadline, "imageId": 0, "generatedQpc": 0}
        self.events.extend([dict(common, stage="display.wait.start", qpc=first, detail=mode, value=0 if mode == "retained" else 15),
                            dict(common, stage="display.wait.end", qpc=first+round(duration_ms*frequency/1000), detail=outcome, value=permission)])
        return common, first+round(duration_ms*frequency/1000)

    def add_display_selection(self, common, first, image_id=None):
        if image_id is None:
            visible = [event for event in self.events if event["stage"] == "compose.visible" and event["qpc"] <= first]
            image_id = max(visible, key=lambda event: event["qpc"])["imageId"] if visible else 0
        source = next((event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == image_id), None)
        selected = dict(common, imageId=image_id, generatedQpc=source["generatedQpc"] if source else 0)
        self.events.extend([dict(selected, stage="display.select.start", qpc=first),
                            dict(selected, stage="display.select.end", qpc=first+1, detail="latest" if image_id else "none")])
        return selected

    def add_ready_presentation(self, common, first, image_id=None):
        selected = self.add_display_selection(common, first, image_id)
        self.events.extend([dict(selected, stage="present.ready.start", qpc=first+2, detail="retained", value=0),
                            dict(selected, stage="present.ready.end", qpc=first+3, detail="retained", value=1),
                            dict(selected, stage="display.draw.start", qpc=first+4),
                            dict(selected, stage="present.start", qpc=first+5)])

    def test_display_ready_mid_slot_and_early_timeout(self):
        self.enable_display_pacing()
        slot, returned = self.add_display_wait()
        self.add_ready_presentation(slot, returned+1)
        self.add_display_wait(7, duration_ms=15.2, outcome="timeout", permission=0)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual(result["displayWait"]["outcomes"], {"ready": 1, "timeout": 1})
        self.assertAlmostEqual(result["displayWait"]["positiveTimeoutOverrunMs"]["max"], .2)
        self.assertEqual(result["presentReadiness"]["modes"], {"native": 0, "retained": 1})

    def test_display_ready_late_grant_carries_into_next_slot_and_window(self):
        self.enable_display_pacing()
        # The next GPU tick is skipped while the prior wait is still returning.
        slot, returned = self.add_display_wait(4+59/60, duration_ms=16, outcome="deadline")
        self.events = [event for event in self.events if event.get("scheduledQpc") != 1300000]
        next_slot, next_return = self.add_display_wait(5+1/60, duration_ms=0, mode="retained", outcome="retained")
        self.add_ready_presentation(next_slot, next_return+1)
        self.write()
        result, _ = analyze(self.logs, 5, 27)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual(result["phaseDurations"]["display.wait.start->display.wait.end"]["omittedBoundaryPairs"], 1)
        self.assertEqual(result["displayWait"]["modes"], {"retained": 1})

    def test_display_ready_null_or_busy_selection_preserves_grant(self):
        self.enable_display_pacing()
        # No source has ever been published when this first acquisition is null.
        self.events = [event for event in self.events if event.get("scheduledQpc", event["qpc"]) > 1360000 or event["stage"] == "present.wait.segment"]
        self.events.append({"stage": "skip", "worker": "GPU", "scheduledQpc": 1360000, "qpc": 1360000,
                            "imageId": 0, "generatedQpc": 0, "detail": "compose.noFreeSlot", "value": 1})
        slot, returned = self.add_display_wait()
        self.add_display_selection(slot, returned+1, image_id=0)
        slot, returned = self.add_display_wait(7, duration_ms=0, mode="retained", outcome="retained")
        selected = self.add_display_selection(slot, returned+1)
        self.events.append(dict(selected, stage="skip", qpc=returned+3, detail="present.keyedMutexBusy", value=1))
        slot, returned = self.add_display_wait(8, duration_ms=0, mode="retained", outcome="retained")
        self.add_ready_presentation(slot, returned+1)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_display_ready_protocol_faults_are_invalid_outside_window(self):
        self.enable_display_pacing()
        baseline = list(self.events)
        for fault in ("missing_end", "duplicate_wait", "wrong_budget", "far_deadline", "has_image", "timeout_permission",
                      "late_ready", "early_deadline", "native_rewait", "native_present_wait", "select_before_wait", "no_wait",
                      "double_present", "missing_selection", "compose_overlap", "wait_before_compose"):
            with self.subTest(fault=fault):
                self.events = list(baseline)
                slot, returned = self.add_display_wait(1)
                self.add_ready_presentation(slot, returned+1)
                wait_start = next(event for event in self.events if event["stage"] == "display.wait.start")
                wait_end = next(event for event in self.events if event["stage"] == "display.wait.end")
                if fault == "missing_end": self.events.remove(wait_end)
                elif fault == "duplicate_wait": self.events.append(dict(wait_start))
                elif fault == "wrong_budget": wait_start["value"] = 14
                elif fault == "far_deadline": wait_start["deadlineQpc"] += 1000; wait_end["deadlineQpc"] += 1000
                elif fault == "has_image": wait_start["imageId"] = wait_end["imageId"] = 1
                elif fault == "timeout_permission": wait_end["detail"] = "timeout"
                elif fault == "late_ready": wait_end["qpc"] = wait_end["deadlineQpc"]
                elif fault == "early_deadline": wait_end["detail"] = "deadline"
                elif fault == "native_rewait":
                    self.events = [event for event in self.events if event["stage"] != "present.start"]
                    self.add_display_wait(2)
                elif fault == "native_present_wait":
                    next(event for event in self.events if event["stage"] == "present.ready.start")["detail"] = "native"
                elif fault == "select_before_wait":
                    next(event for event in self.events if event["stage"] == "display.select.start")["qpc"] = wait_start["qpc"]
                elif fault == "no_wait": self.events = [event for event in self.events if not event["stage"].startswith("display.wait.")]
                elif fault == "double_present":
                    self.events.append(dict(next(event for event in self.events if event["stage"] == "present.start")))
                elif fault == "missing_selection": self.events = [event for event in self.events if not event["stage"].startswith("display.select.")]
                elif fault == "compose_overlap":
                    self.events.append(dict(slot, stage="compose.start", qpc=wait_start["qpc"]+1))
                elif fault == "wait_before_compose":
                    self.events = [event for event in self.events if not (event["stage"] == "compose.visible" and event["scheduledQpc"] == slot["scheduledQpc"])]
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_display_ready_cancelled_grant_prevents_new_work(self):
        self.enable_display_pacing()
        slot, returned = self.add_display_wait(30, outcome="cancelled")
        self.events = [event for event in self.events if event["qpc"] <= returned or event["stage"] == "lifecycle"]
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.add_ready_presentation(slot, returned+1)
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_display_pacing_config_tick_and_command_compatibility(self):
        self.enable_display_pacing("tick")
        ready, first = self.add_readiness(6.001, requested_ms=0, image_id=361)
        self.add_display_selection(ready, first-2)
        self.events.append(dict(ready, stage="present.start", qpc=first+18))
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        baseline = dict(self.manifest["options"])
        for field, value in (("displayPacing", "unknown"), ("mode", "common"), ("output", "fullscreen"), ("presentWaitMs", 1), ("presentWaitPlan", "abba")):
            with self.subTest(field=field):
                self.manifest["options"] = dict(baseline, displayPacing="ready")
                self.manifest["options"][field] = value
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest["options"] = baseline
        self.write()
        command = {"presentWaitMs": 0, "presentWaitPlan": "fixed", "displayPacing": "tick"}
        (self.root / "command.json").write_text(json.dumps(command), encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        command["displayPacing"] = "ready"
        (self.root / "command.json").write_text(json.dumps(command), encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_display_selection_visibility_tie_latest_and_none_validation(self):
        self.enable_display_pacing("tick")
        origin = self.manifest["originQpc"]
        common = {"worker": "GPU", "scheduledQpc": origin+360000, "deadlineQpc": origin+361000}
        self.add_display_selection(common, origin+360001, image_id=361)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        baseline = list(self.events)
        for stale in (1, 0):
            self.events = [event for event in baseline if not event["stage"].startswith("display.select.")]
            self.add_display_selection(common, origin+360001, image_id=stale)
            self.write()
            self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_whole_process_cpu_boundaries_and_invalid_metadata(self):
        self.summary.update(appCpuSeconds=16, appCpuStartQpc=940000, appCpuEndQpc=2980000,
                            appCpuScope="whole-run: engine startup through native cleanup; excludes log serialization")
        self.write()
        result, _ = analyze(self.logs, 5, 6)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["processCpu"]["elapsedSeconds"], 34)
        self.assertAlmostEqual(result["processCpu"]["averageCoreUsage"], 16/34)
        for field, value in (("appCpuSeconds", -1), ("appCpuSeconds", float("nan")), ("appCpuEndQpc", 940000), ("appCpuScope", None)):
            old = self.summary[field]
            self.summary[field] = value
            self.write()
            self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
            self.summary[field] = old

    def test_custom_window(self):
        result, _ = analyze(self.logs, 1, 3)
        self.assertEqual(result["metrics"]["send.publish"]["count"], 120)
        with self.assertRaises(ValueError):
            analyze(self.logs, 27, 5)

    def test_resends_and_unordered_worker_logs(self):
        for event in self.events:
            if event["stage"] == "send.publish":
                event["imageId"] = (event["imageId"]+1)//2
                event["generatedQpc"] = 1000000+(event["imageId"]-1)*1000-60
        self.events.reverse()
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["metrics"]["send.publish"]["distinctImageIds"], 660)
        self.assertEqual(result["metrics"]["send.publish"]["consecutiveResends"], 660)

    def test_dropped_logs_error_outside_window_and_cancelled_rejected(self):
        self.summary.update(droppedEvents=1, outcome="cancelled")
        self.events.append({"stage": "error", "qpc": 1000000, "detail": "synthetic failure"})
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(len(result["invalidReasons"]), 3)

    def test_truncation_and_missing_output_rejected(self):
        self.events = [event for event in self.events if event["qpc"] < 1500000 and event["stage"] != "send.publish"]
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("cover" in reason for reason in result["invalidReasons"]))
        self.assertTrue(any("send.publish" in reason for reason in result["invalidReasons"]))

    def test_malformed_line_rejected_but_diagnostics_preserved(self):
        with (self.logs / "events.jsonl").open("a", encoding="utf-8") as stream:
            stream.write('{"stage":"send.publish"\n')
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(result["metrics"]["send.publish"]["count"], 1320)

    def test_receiver_window_coverage_and_runner_failure(self):
        runner = {"completedNormally": True, "receiver": {"observedAliveQpc": 1000000+4*60000}}
        samples = [{"qpc": 1000000+28*60000, "alive": True}]
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        (self.root / "receiver-samples.json").write_text(json.dumps(samples), encoding="utf-8")
        initial, _ = analyze(self.logs)
        self.assertTrue(initial["validPerformanceResult"])
        self.assertEqual(initial["receiverMode"], "official")
        runner["receiver"]["observedAliveQpc"] = 1000000+6*60000
        runner["completedNormally"] = False
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(len(result["invalidReasons"]), 2)

    def test_skip_counts_and_negative_age_rejected(self):
        self.events.append({"stage": "skip", "worker": "sender", "qpc": 1000000+6*60000, "detail": "schedule", "value": 3})
        self.events.append({"stage": "copy.complete", "qpc": 1000000+6*60000, "generatedQpc": 1000000+7*60000, "imageId": 15})
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(result["skips"]["sender:schedule"]["skippedCount"], 3)

    def test_percentile_definition(self):
        self.assertIsNone(percentile([], .95))
        self.assertEqual(percentile([12], .99), 12)
        self.assertEqual(percentile([0, 100], .95), 100)

    def test_output_refuses_overwrite_and_keeps_source(self):
        before = {file.name: file.read_bytes() for file in self.logs.iterdir()}
        result, gaps = analyze(self.logs)
        output = self.root / "analysis"
        save_result(result, gaps, output)
        with self.assertRaises(FileExistsError):
            save_result(result, gaps, output)
        self.assertEqual(before, {file.name: file.read_bytes() for file in self.logs.iterdir()})
        self.assertEqual(len(list(output.iterdir())), 4)

    def test_phase_pairing_boundary_and_resends(self):
        # Same image sent in several ticks must pair by scheduledQpc as well.
        for seconds in (4.999, 6, 7, 26.999):
            tick = int(1000000+seconds*60000)
            for stage, delta in (("send.start", 0), ("send.return", 120), ("send.gpuComplete", 180)):
                self.events.append({"stage": stage, "worker": "spout", "scheduledQpc": tick,
                                    "qpc": tick+delta, "imageId": 1, "generatedQpc": 999940})
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        pairs = result["phaseDurations"]["send.start->send.return"]
        self.assertEqual(pairs["durationMs"]["count"], 2)
        self.assertEqual(pairs["durationMs"]["max"], 2)
        self.assertEqual(pairs["omittedBoundaryPairs"], 2)
        self.assertEqual(pairs["duplicateKeys"], 0)
        self.assertEqual(result["phaseDurations"]["send.return->send.gpuComplete"]["durationMs"]["max"], 1)

    def test_missing_duplicate_and_reverse_phase_pairs(self):
        tick = 1000000+6*60000
        self.events.extend([
            {"stage": "copy.start", "qpc": tick, "scheduledQpc": 1, "imageId": 1},
            {"stage": "copy.start", "qpc": tick+1, "scheduledQpc": 1, "imageId": 1},
            {"stage": "copy.complete", "qpc": tick+2, "scheduledQpc": 1, "imageId": 1},
            {"stage": "copy.start", "qpc": tick+3, "scheduledQpc": 2, "imageId": 1},
            {"stage": "copy.complete", "qpc": tick+4, "scheduledQpc": 3, "imageId": 1},
            {"stage": "copy.complete", "qpc": tick+5, "scheduledQpc": 4, "imageId": 1},
            {"stage": "copy.start", "qpc": tick+6, "scheduledQpc": 4, "imageId": 1}])
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        pairs = result["phaseDurations"]["copy.start->copy.complete"]
        self.assertEqual(pairs["missingStartKeys"], 1)
        self.assertEqual(pairs["missingEndKeys"], 1)
        self.assertEqual(pairs["duplicateKeys"], 1)
        self.assertEqual(pairs["reversedPairs"], 1)

    def test_unpublished_duplicate_source_mutated_age_and_regression_rejected(self):
        tick = 1000000+6*60000
        self.events.extend([
            {"stage": "send.publish", "worker": "other", "qpc": tick+1, "imageId": 99999, "generatedQpc": tick},
            {"stage": "send.publish", "worker": "other", "qpc": tick+2, "imageId": 1, "generatedQpc": 999940},
            {"stage": "present.return", "worker": "other", "qpc": tick+3, "imageId": 2, "generatedQpc": tick}])
        # Duplicate source publication is invalid even when the metadata matches.
        self.events.append(dict(self.events[0]))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("regression" in reason for reason in result["invalidReasons"]))
        self.assertTrue(any("exactly one" in reason for reason in result["invalidReasons"]))

    def test_zero_mutex_wait_reports_overhead_not_positive_timeout_overrun(self):
        self.manifest["options"]["mutexWaitMs"] = 0
        self.add_acquisition(6, 0, .2)
        self.add_acquisition(7, 0, .3, "busy")
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        acquisition = result["acquisition"]
        self.assertEqual(acquisition["configuredWaitMs"], 0)
        self.assertEqual(acquisition["actualDurationMs"]["count"], 2)
        self.assertEqual(acquisition["pairedOutcomesInWindow"], {"acquired": 1, "busy": 1, "abandoned": 0})
        self.assertEqual(acquisition["zeroTimeoutCallDurationMs"]["p95"], .3)
        self.assertEqual(acquisition["positiveTimeoutOverrunMs"]["count"], 0)
        self.assertEqual(acquisition["positiveTimeoutOverrunCount"], 0)

    def test_one_ms_wait_oversleep_and_effective_zero_wait(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        self.add_acquisition(6, 1, .5)
        self.add_acquisition(7, 1, 8, "busy")
        self.add_acquisition(8, 0, .2, "busy")
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        acquisition = result["acquisition"]
        self.assertEqual(acquisition["effectiveRequestedTimeoutMs"]["count"], 3)
        self.assertEqual(acquisition["positiveTimeoutOverrunMs"]["count"], 2)
        self.assertEqual(acquisition["positiveTimeoutOverrunMs"]["max"], 7)
        self.assertEqual(acquisition["positiveTimeoutOverrunCount"], 1)
        self.assertEqual(acquisition["zeroTimeoutCallDurationMs"]["max"], .2)

    def test_late_acquisition_drops_send_and_preserves_skip_reasons(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        common = self.add_acquisition(6, 1, 8, deadline_ms=2)
        self.events.append(dict(common, stage="skip", qpc=common["scheduledQpc"]+481, detail="send.deadlineExpired", value=1))
        self.events.append(dict(common, stage="skip", qpc=common["scheduledQpc"]+482, detail="send.cancelled", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        acquisition = result["acquisition"]
        self.assertEqual(acquisition["deadlineReachedOrExceededCount"], 1)
        self.assertEqual(acquisition["acquiredAtOrAfterDeadlineCount"], 1)
        self.assertEqual(acquisition["lateAcquiredWithoutSendStartCount"], 1)
        self.assertEqual(acquisition["lateAcquiredWithSendStartCount"], 0)
        self.assertEqual(result["skips"]["acquire-test:send.deadlineExpired"]["skippedCount"], 1)
        self.assertEqual(result["skips"]["acquire-test:send.cancelled"]["skippedCount"], 1)
        self.events.append(dict(common, stage="send.start", qpc=common["scheduledQpc"]+483))
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_acquisition_boundary_pairs(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        self.add_acquisition(4.999, 1, 2)
        self.add_acquisition(6, 1, .5)
        self.add_acquisition(26.999, 1, 2)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["acquisition"]["actualDurationMs"]["count"], 1)
        self.assertEqual(result["acquisition"]["pairsWithoutDeadline"], 0)
        self.assertEqual(result["phaseDurations"]["send.acquire.start->send.acquire.end"]["omittedBoundaryPairs"], 2)

    def test_acquisition_inconsistent_metadata_and_missing_pair_rejected(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        self.add_acquisition(6, 2, .5) # Effective timeout cannot exceed configured budget.
        self.add_acquisition(7, 1, .5)
        self.events[-1]["generatedQpc"] += 1
        self.events[-1]["deadlineQpc"] += 1
        self.add_acquisition(8, 1, .5)
        self.events.pop() # Missing terminal acquisition event cannot count as busy.
        self.add_acquisition(9, 1, .5, deadline_ms=None)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        acquisition = result["acquisition"]
        self.assertTrue(any("timeout" in error for error in acquisition["metadataErrors"]))
        self.assertTrue(any("generatedQpc" in error for error in acquisition["metadataErrors"]))
        self.assertTrue(any("deadlineQpc" in error for error in acquisition["metadataErrors"]))
        self.assertTrue(any("exactly once" in error for error in acquisition["metadataErrors"]))
        self.assertTrue(any("positive deadlineQpc" in error for error in acquisition["metadataErrors"]))

    def test_acquisition_exact_deadline_and_abandoned_outcome(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        self.add_acquisition(6, 1, 1, deadline_ms=1)
        self.add_acquisition(7, 1, .2, outcome="abandoned")
        self.events.append({"stage": "error", "qpc": 1000000+7*60000+13, "detail": "Abandoned mutex"})
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(result["acquisition"]["pairedOutcomesInWindow"]["abandoned"], 1)
        self.assertEqual(result["acquisition"]["acquiredAtOrAfterDeadlineCount"], 1)
        self.assertEqual(result["acquisition"]["positiveTimeoutOverrunCount"], 0)

    def test_outside_window_late_or_busy_acquisition_cannot_send(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        late = self.add_acquisition(1, 1, 2, deadline_ms=1)
        busy = self.add_acquisition(30, 1, .2, outcome="busy")
        self.events.append(dict(late, stage="send.start", qpc=late["scheduledQpc"]+121))
        self.events.append(dict(busy, stage="send.start", qpc=busy["scheduledQpc"]+13))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("at/after deadline" in error for error in result["invalidReasons"]))
        self.assertTrue(any("without successful" in error for error in result["invalidReasons"]))
        self.assertEqual(result["acquisition"]["actualDurationMs"]["count"], 0)

    def test_instrumented_log_send_requires_acquisition_and_earlier_return(self):
        self.manifest["options"]["mutexWaitMs"] = 0
        self.events.append({"stage": "send.start", "worker": "missing", "qpc": 1360000,
                            "scheduledQpc": 1360000, "imageId": 1, "generatedQpc": 999940})
        common = self.add_acquisition(7, 0, .5)
        self.events.append(dict(common, stage="send.start", qpc=common["scheduledQpc"]+1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("requires exactly one" in error for error in result["invalidReasons"]))
        self.assertTrue(any("before mutex acquisition returned" in error for error in result["invalidReasons"]))

    def test_effective_timeout_cannot_borrow_submillisecond_remainder(self):
        self.manifest["options"]["mutexWaitMs"] = 1
        self.add_acquisition(6, 0, .1, deadline_ms=.5)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.add_acquisition(7, 1, .1, deadline_ms=.5)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("remaining whole milliseconds" in error for error in result["invalidReasons"]))

    def test_zero_phase_previous_frame_and_publication_overlap_bounds(self):
        self.add_phase_metadata()
        self.add_selection(1360000, 1360002, 360)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        selection = result["selection"]
        self.assertAlmostEqual(selection["composeScheduledToSendScheduledMs"]["mean"], 1000/60)
        self.assertEqual(selection["rows"][0]["definiteCandidateId"], 360)
        self.assertEqual(selection["rows"][0]["possibleCandidateId"], 361)
        self.assertEqual(selection["publicationRaceUncertainCount"], 1)
        self.assertEqual(selection["selectedOlderThanDefiniteCandidateCount"], 0)
        self.assertEqual(result["metrics"]["send.publish"]["imageAgeMs"]["mean"], 1)

    def test_positive_phase_current_frame_selection_and_new_csv(self):
        self.add_phase_metadata(3)
        self.add_selection(1360180, 1360182, 361)
        self.write()
        result, gaps = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["selection"]["configuredSendPhaseMs"], 3)
        self.assertEqual(result["selection"]["composeScheduledToSendScheduledMs"]["mean"], 3)
        self.assertEqual(result["selection"]["publicationRaceUncertainCount"], 0)
        save_result(result, gaps, self.root / "selection-analysis")
        self.assertTrue((self.root / "selection-analysis" / "selection.csv").exists())

    def test_selection_visibility_and_publication_ties_are_uncertain(self):
        self.add_phase_metadata()
        self.add_selection(1360010, 1360012, 360) # visible(current) == selection.start
        self.add_selection(1360998, 1361000, 361) # publish(next) == selection.end
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        rows = result["selection"]["rows"]
        self.assertEqual((rows[0]["definiteCandidateId"], rows[0]["possibleCandidateId"]), (360, 361))
        self.assertEqual((rows[1]["definiteCandidateId"], rows[1]["possibleCandidateId"]), (361, 362))
        self.assertTrue(all(row["publicationRaceUncertain"] for row in rows))

    def test_selection_start_age_can_be_negative_for_a_frame_generated_during_selection(self):
        self.add_phase_metadata()
        self.add_selection(1359930, 1360020, 361) # Frame generated at1359940 inside selection.
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertLess(result["selection"]["imageAgeAtStartMs"]["mean"], 0)
        self.assertGreater(result["selection"]["imageAgeAtEndMs"]["mean"], 0)
        self.assertEqual(result["selection"]["publicationRaceUncertainCount"], 1)
        self.add_selection(1360930, 1360935, 362) # Next frame generated AFTER select.end.
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("generated after selection ended" in error for error in result["invalidReasons"]))

    def test_selection_window_boundaries_and_pair_metadata_outside_window(self):
        self.add_phase_metadata()
        self.add_selection(1299999, 1300001, 300) # [5,27) opening boundary crossed.
        self.add_selection(2619999, 2620001, 1620) # Closing boundary crossed.
        self.add_selection(1360060, 1360062, 361)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["selection"]["pairedSelectionsInWindow"], 1)
        self.assertEqual(result["phaseDurations"]["send.select.start->send.select.end"]["omittedBoundaryPairs"], 2)
        self.add_selection(1060060, 1060062, 61)
        self.events[-1]["generatedQpc"] += 1
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_null_retained_and_copybusy_selection_allow_old_held_send(self):
        self.add_phase_metadata()
        self.add_selection(1360060, 1360062, 361)
        self.add_selection(1360120, 1360122, 361, "retained")
        none = self.add_selection(1360180, 1360182, 0, "none")
        held = {"imageId": 361, "generatedQpc": 1359940}
        self.events.append(dict(none, **{"stage": "send.start", "qpc": 1360183}) | held)
        busy = self.add_selection(1361060, 1361062, 362)
        self.events.append(dict(busy, stage="skip", qpc=1361063, detail="copy.keyedMutexBusy", value=1))
        self.events.append(dict(busy, stage="send.start", qpc=1361064) | held)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["selection"]["outcomes"], {"latest": 2, "retained": 1, "none": 1})

    def test_new_log_send_requires_selection_even_outside_window(self):
        self.add_phase_metadata()
        self.events.append({"stage": "send.start", "worker": "Spout", "qpc": 1060060,
                            "scheduledQpc": 1060000, "imageId": 61, "generatedQpc": 1059940})
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("matching earlier selection pair" in error for error in result["invalidReasons"]))

    def test_new_visibility_and_selection_metadata_must_match(self):
        self.add_phase_metadata()
        self.add_selection(1360060, 1360062, 361)
        self.events.append(dict(self.events[-1])) # Duplicate selection end.
        observed = next(event for event in self.events if event["stage"] == "compose.visible" and event["imageId"] == 2)
        observed["scheduledQpc"] += 1
        self.events = [event for event in self.events if not (event["stage"] == "compose.visible" and event["imageId"] == 3)]
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("changed publication metadata" in error for error in result["invalidReasons"]))
        self.assertTrue(any("compose.publish/visible must pair" in error for error in result["invalidReasons"]))
        self.assertTrue(any("Selection start/end must pair" in error for error in result["invalidReasons"]))

    def add_retry_slot(self, retry_image=361, retry_first=1360400, retry_last=1360402, deadline=1361240,
                       with_busy=True, with_retry=True, send_qpc=None):
        # Phase 4ms slot 1360240 (next 1361240): image 361 visible 1360010, image 362 published 1361000.
        self.manifest["options"]["copyRetry"] = "signal"
        first = self.add_selection(1360300, 1360302, 361, scheduled_qpc=1360240, attempt=0)
        if with_busy:
            self.events.append(dict(first, stage="skip", qpc=1360303, detail="copy.keyedMutexBusy", value=1))
        if with_retry:
            self.events.append(dict(first, stage="copy.retry", qpc=1360304, value=4, deadlineQpc=deadline))
        retry = self.add_selection(retry_first, retry_last, retry_image, scheduled_qpc=1360240, attempt=1)
        self.events.append(dict(retry, stage="send.start", qpc=retry_last+8 if send_qpc is None else send_qpc))
        return first, retry

    def test_same_image_copy_retry_pairs_separately_and_is_valid(self):
        self.add_phase_metadata(4)
        self.add_retry_slot()
        self.write()
        result, gaps = analyze(self.logs)
        self.assertEqual(result["invalidReasons"], [])
        self.assertTrue(result["validPerformanceResult"])
        selection = result["selection"]
        self.assertEqual(selection["retryAttemptsInWindow"], 1)
        self.assertEqual(selection["retrySelectedNewerImage"], 0)
        self.assertEqual([row["attempt"] for row in selection["rows"]], [0, 1])
        self.assertEqual(selection["outcomes"], {"latest": 2, "retained": 0, "none": 0})
        pairs = result["phaseDurations"]["send.select.start->send.select.end"]
        self.assertEqual(pairs["duplicateKeys"], 0)
        self.assertEqual(pairs["durationMs"]["count"], 2)
        self.assertEqual(pairs["pairKey"], ["worker", "scheduledQpc", "imageId", "attempt"])
        self.assertEqual(result["phaseDurations"]["copy.start->copy.complete"]["pairKey"], ["worker", "scheduledQpc", "imageId"])
        save_result(result, gaps, self.root / "retry-analysis")
        self.assertIn("attempt", (self.root / "retry-analysis" / "selection.csv").read_text(encoding="utf-8-sig").splitlines()[0])

    def test_copy_retry_selecting_newer_image_is_counted(self):
        self.add_phase_metadata(4)
        self.add_retry_slot(retry_image=362, retry_first=1361050, retry_last=1361052)
        self.write()
        result, _ = analyze(self.logs)
        self.assertEqual(result["invalidReasons"], [])
        self.assertEqual(result["selection"]["retryAttemptsInWindow"], 1)
        self.assertEqual(result["selection"]["retrySelectedNewerImage"], 1)

    def test_retry_selection_without_copy_retry_or_busy_skip_is_invalid(self):
        self.add_phase_metadata(4)
        self.add_retry_slot(with_retry=False)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("exactly one copy.retry" in error for error in result["invalidReasons"]))
        self.events = [event for event in self.events if event["stage"] not in ("send.select.start", "send.select.end", "skip", "send.start")]
        self.add_retry_slot(with_busy=False)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("copy.keyedMutexBusy skip" in error for error in result["invalidReasons"]))

    def test_retry_selection_without_first_selection_is_invalid(self):
        self.add_phase_metadata(4)
        self.manifest["options"]["copyRetry"] = "signal"
        retry = self.add_selection(1360400, 1360402, 361, scheduled_qpc=1360240, attempt=1)
        self.events.append(dict(retry, stage="copy.retry", qpc=1360304, value=4, deadlineQpc=1361240))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("completed first selection" in error for error in result["invalidReasons"]))

    def test_second_retry_selection_in_same_slot_is_invalid(self):
        self.add_phase_metadata(4)
        self.add_retry_slot()
        self.add_selection(1360500, 1360502, 361, scheduled_qpc=1360240, attempt=1)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("At most one retry selection" in error for error in result["invalidReasons"]))

    def test_retry_selection_at_or_after_deadline_is_invalid(self):
        self.add_phase_metadata(4)
        self.add_retry_slot(deadline=1360400)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("at or after the copy.retry deadline" in error for error in result["invalidReasons"]))

    def test_copy_retry_events_require_signal_option_when_declared(self):
        self.add_phase_metadata(4)
        self.add_retry_slot()
        self.manifest["options"]["copyRetry"] = "off"
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("require copyRetry signal" in error for error in result["invalidReasons"]))
        del self.manifest["options"]["copyRetry"]  # Older manifests without the option stay valid.
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_selection_attempt_value_other_than_zero_or_one_is_invalid(self):
        self.add_phase_metadata(4)
        self.add_selection(1360300, 1360302, 361, scheduled_qpc=1360240, attempt=2)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("attempt value must be 0 (first) or 1 (retry)" in error for error in result["invalidReasons"]))

    def test_send_after_retry_must_follow_the_retry_selection(self):
        self.add_phase_metadata(4)
        self.add_retry_slot(send_qpc=1360350)  # after the first pair, before the retry pair
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("matching earlier selection pair" in error for error in result["invalidReasons"]))

    def write_receiver_fixture(self, mode="none", runner_extra=None):
        receiver = None if mode == "none" else {"observedAliveQpc": 1240000}
        runner = {"completedNormally": True, "receiverMode": mode, "receiver": receiver,
                  "receiverExit": None if mode == "none" else 0, "receiverForced": False,
                  "receiverCloseMessages": 0, "receiverBindingVerified": False}
        if runner_extra:
            runner.update(runner_extra)
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        (self.root / "command.json").write_text(json.dumps({"receiverMode": mode}), encoding="utf-8")
        samples = [] if mode == "none" else [{"qpc": 2680000, "alive": True}]
        (self.root / "receiver-samples.json").write_text(json.dumps(samples), encoding="utf-8")
        return runner

    def test_explicit_none_is_sender_control_and_retains_sender_metrics(self):
        self.write_receiver_fixture()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["receiverMode"], "none")
        self.assertEqual(result["measurementKind"], "sender-control-without-owned-receiver")
        self.assertIsNone(result["receiverCoverage"])
        self.assertEqual(result["metrics"]["send.publish"]["rateHz"], 60)
        self.assertTrue(any("does not prove other applications" in text for text in result["limitations"]))

    def test_new_official_requires_receiver_coverage_and_never_downgrades(self):
        self.write_receiver_fixture("official")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.write_receiver_fixture("official", {"receiver": {"observedAliveQpc": 1360000}})
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertEqual(result["receiverMode"], "official")
        self.assertEqual(result["measurementKind"], "output-probe")
        self.write_receiver_fixture("official", {"receiver": None, "receiverExit": None})
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_none_requires_both_mode_records_explicit(self):
        # A missing command file defaults to official, never to receiver absence.
        runner = {"completedNormally": True, "receiverMode": "none", "receiver": None}
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        (self.root / "receiver-samples.json").write_text("[]", encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        for command in ({}, {"receiverMode": "official"}, {"receiverMode": "unknown"}):
            with self.subTest(command=command):
                (self.root / "command.json").write_text(json.dumps(command), encoding="utf-8")
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "command.json").write_text('{"receiverMode":"none"}', encoding="utf-8")
        for replacement in ({"completedNormally": True, "receiver": None}, {"completedNormally": True, "receiverMode": "official", "receiver": None}):
            (self.root / "runner-result.json").write_text(json.dumps(replacement), encoding="utf-8")
            self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_none_command_without_runner_result_is_invalid(self):
        (self.root / "command.json").write_text('{"receiverMode":"none"}', encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_none_requires_explicit_null_receiver_and_consistent_flags(self):
        for updates in ({"receiver": {"pid": 12}}, {"receiverExit": 0}, {"receiverForced": True},
                        {"receiverCloseMessages": 1}, {"receiverBindingVerified": True}, {"completedNormally": False}):
            with self.subTest(updates=updates):
                self.write_receiver_fixture(runner_extra=updates)
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        runner = self.write_receiver_fixture()
        del runner["receiver"]
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_none_requires_existing_empty_samples_and_no_owned_record(self):
        runner = {"completedNormally": True, "receiverMode": "none", "receiver": None}
        (self.root / "runner-result.json").write_text(json.dumps(runner), encoding="utf-8")
        (self.root / "command.json").write_text('{"receiverMode":"none"}', encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        for samples in (None, {}, [{"qpc": 2680000, "alive": True}]):
            with self.subTest(samples=samples):
                (self.root / "receiver-samples.json").write_text(json.dumps(samples), encoding="utf-8")
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "receiver-samples.json").write_text("[]", encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "receiver-owned.json").write_text("{}", encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_none_is_invalid_for_fullscreen_only(self):
        self.manifest["options"]["output"] = "fullscreen"
        self.write()
        self.write_receiver_fixture()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_eight_ms_wait_and_os_oversleep_before_deadline_are_valid(self):
        self.manifest["options"]["mutexWaitMs"] = 8
        self.add_acquisition(6, 8, 8)
        self.add_acquisition(7, 8, 10, "busy", deadline_ms=16)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["acquisition"]["configuredWaitMs"], 8)
        self.assertEqual(result["acquisition"]["positiveTimeoutOverrunCount"], 1)
        self.assertEqual(result["acquisition"]["positiveTimeoutOverrunMs"]["max"], 2)
        self.assertEqual(result["acquisition"]["deadlineReachedOrExceededCount"], 0)

    def test_nine_ms_and_effective_wait_above_configured_are_invalid(self):
        for configured, requested in ((9, 8), (8, 9), (7, 8)):
            with self.subTest(configured=configured, requested=requested):
                self.manifest["options"]["mutexWaitMs"] = configured
                # Reuse one acquisition pair, changing only the declared budgets.
                self.events = [event for event in self.events if not event["stage"].startswith("send.acquire.")]
                self.add_acquisition(6, requested, .2)
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_eight_ms_budget_cannot_borrow_7_999_ms_remainder(self):
        # Use a representable7.999ms deadline rather than rounding it to8ms.
        origin = self.manifest["originQpc"]
        self.manifest["qpcFrequency"] *= 100
        for event in self.events:
            for field in ("qpc", "scheduledQpc", "generatedQpc"):
                event[field] = origin+(event[field]-origin)*100
        self.manifest["options"]["mutexWaitMs"] = 8
        self.add_acquisition(6, 7, 7, deadline_ms=7.999)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.add_acquisition(7, 8, .2, deadline_ms=7.999)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("remaining whole milliseconds" in error for error in result["invalidReasons"]))

    def test_eight_ms_late_acquisition_requires_skipping_send(self):
        self.manifest["options"]["mutexWaitMs"] = 8
        common = self.add_acquisition(6, 8, 10, deadline_ms=9)
        self.events.append(dict(common, stage="skip", qpc=common["scheduledQpc"]+601, detail="send.deadlineExpired", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["acquisition"]["lateAcquiredWithoutSendStartCount"], 1)
        self.events.append(dict(common, stage="send.start", qpc=common["scheduledQpc"]+602))
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_ready_and_timeout_keep_positive_overrun_separate(self):
        self.manifest["options"]["presentWaitMs"] = 1
        ready, first = self.add_readiness(6)
        self.events.append(dict(ready, stage="present.start", qpc=first+18))
        self.add_readiness(7, actual_ms=2, outcome="notReady", permission=0)
        self.add_readiness(8, requested_ms=0, actual_ms=.1, outcome="notReady", permission=0)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        report = result["presentReadiness"]
        self.assertEqual(report["modes"], {"native": 3, "retained": 0})
        self.assertEqual(report["outcomes"]["notReady"], 2)
        self.assertEqual(report["positiveTimeoutOverrunCount"], 1)
        self.assertEqual(report["positiveTimeoutOverrunMs"]["max"], 1)
        self.assertEqual(report["nativeZeroTimeoutDurationMs"]["max"], .1)

    def test_present_late_success_permission_carries_across_window_boundary(self):
        self.manifest["options"]["presentWaitMs"] = 1
        self.add_readiness(4.999, actual_ms=2, outcome="deadline")
        reused, first = self.add_readiness(6, requested_ms=0, actual_ms=.1, mode="retained", outcome="retained", image_id=2)
        self.events.append(dict(reused, stage="present.start", qpc=first+12))
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["presentReadiness"]["modes"], {"native": 0, "retained": 1})
        self.assertEqual(result["phaseDurations"]["present.ready.start->present.ready.end"]["omittedBoundaryPairs"], 1)

    def test_present_draw_expiry_and_cancellation_retain_permission(self):
        self.manifest["options"]["presentWaitMs"] = 1
        ready, first = self.add_readiness(6.015666666666667, actual_ms=.1)
        self.events.append(dict(ready, stage="display.draw.start", qpc=first+12))
        self.events.append(dict(ready, stage="skip", qpc=ready["deadlineQpc"]+1, detail="present.deadline", value=1))
        reused, second = self.add_readiness(7, requested_ms=0, actual_ms=.1, mode="retained", outcome="retained", image_id=2)
        self.events.append(dict(reused, stage="present.start", qpc=second+12))
        self.add_readiness(8, actual_ms=.2, outcome="cancelled")
        reused, third = self.add_readiness(9, requested_ms=0, actual_ms=.1, mode="retained", outcome="retained", image_id=3)
        self.events.append(dict(reused, stage="present.start", qpc=third+12))
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_permission_cannot_be_rewaited_reused_without_grant_or_consumed_twice(self):
        self.manifest["options"]["presentWaitMs"] = 1
        base = list(self.events)
        for fault in ("native_rewait", "reuse_without_permission", "double_present", "retry_same_tick"):
            with self.subTest(fault=fault):
                self.events = list(base)
                if fault == "reuse_without_permission":
                    self.add_readiness(6, requested_ms=0, mode="retained", outcome="retained")
                else:
                    ready, first = self.add_readiness(6)
                    if fault == "native_rewait":
                        self.add_readiness(7)
                    elif fault == "retry_same_tick":
                        self.add_readiness(6.001, image_id=2)
                    else:
                        self.events.append(dict(ready, stage="present.start", qpc=first+18))
                        self.events.append(dict(ready, stage="present.start", qpc=first+19))
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_cannot_follow_timeout_missing_readiness_or_expired_draw(self):
        self.manifest["options"]["presentWaitMs"] = 1
        base = list(self.events)
        for fault in ("missing", "notReady", "deadline", "late_draw"):
            with self.subTest(fault=fault):
                self.events = list(base)
                if fault == "missing":
                    self.events.append({"stage": "present.start", "worker": "GPU", "scheduledQpc": 1360000,
                                        "qpc": 1360020, "imageId": 1, "generatedQpc": 999940})
                else:
                    ready, first = self.add_readiness(6, outcome="notReady" if fault == "notReady" else "ready", permission=0 if fault == "notReady" else 1)
                    self.events.append(dict(ready, stage="display.draw.start" if fault == "late_draw" else "present.start",
                                            qpc=first+18 if fault == "notReady" else ready["deadlineQpc"]))
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_config_command_and_manifest_must_agree(self):
        for invalid in (-1, 2, True, .5, "1"):
            self.manifest["options"]["presentWaitMs"] = invalid
            self.write()
            self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest["options"].update(presentWaitMs=1, output="spout")
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest["options"].update(presentWaitMs=1, output="both")
        self.write()
        for command in ({}, {"presentWaitMs": 0}, {"presentWaitMs": True}):
            (self.root / "command.json").write_text(json.dumps(command), encoding="utf-8")
            self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "command.json").write_text('{"presentWaitMs":1}', encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_fractional_fps_deadline_and_common_end_cap(self):
        self.manifest["options"].update(presentWaitMs=1, fps=59.94)
        scheduled = 1000000+round(499*60000/59.94)
        self.add_readiness((scheduled+10-1000000)/60000, actual_ms=.1, scheduled_qpc=scheduled)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.events[-1]["deadlineQpc"] -= 1
        self.events[-2]["deadlineQpc"] -= 1
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = [event for event in self.events if not event["stage"].startswith("present.ready.")]
        self.manifest["options"].update(fps=60, seconds=32.005)
        self.add_readiness(32.001, actual_ms=.1)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_present_requested_budget_metadata_and_late_notready_are_checked(self):
        self.manifest["options"]["presentWaitMs"] = 1
        base = list(self.events)
        for fault in ("excess_budget", "changed_image", "missing_end", "late_notReady", "far_deadline"):
            with self.subTest(fault=fault):
                self.events = list(base)
                if fault == "excess_budget":
                    self.add_readiness(6.016, requested_ms=1, actual_ms=.1)
                elif fault == "late_notReady":
                    self.add_readiness(6.015666666666667, actual_ms=2, outcome="notReady", permission=0)
                else:
                    self.add_readiness(6, actual_ms=.1)
                    if fault == "changed_image":
                        self.events[-1]["imageId"] = 2
                    elif fault == "missing_end":
                        self.events.pop()
                    else:
                        self.events[-1]["deadlineQpc"] += 1000
                        self.events[-2]["deadlineQpc"] += 1000
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_wait_plan_abba_window_membership_and_fixed_compatibility(self):
        self.add_wait_plan()
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["presentWaitPlan"], "abba")
        self.assertEqual(result["presentWaitWindow"]["scope"], "multiple-segments")
        self.assertIsNone(result["presentReadiness"]["configuredWaitMs"])
        for window, indices, values, scope in (((10, 14), [1], [1], "single-segment"),
                                                ((16, 24), [2], [1], "single-segment"),
                                                ((8, 24), [1, 2], [1], "multiple-segments")):
            selected, _ = analyze(self.logs, *window)
            self.assertTrue(selected["validPerformanceResult"])
            self.assertEqual(selected["presentWaitWindow"]["segmentIndices"], indices)
            self.assertEqual(selected["presentWaitWindow"]["waitMsValues"], values)
            self.assertEqual(selected["presentWaitWindow"]["scope"], scope)

    def test_wait_plan_native_requests_must_use_scheduled_segment_budget(self):
        self.add_wait_plan()
        self.add_readiness(6, requested_ms=0, outcome="notReady", permission=0)
        self.add_readiness(9, requested_ms=1, outcome="notReady", permission=0)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        self.events[-2]["value"] = 0 # B segment must not silently continue polling at zero.
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("scheduled segment's effective request" in error for error in result["invalidReasons"]))

    def test_wait_plan_ready_permission_crosses_segment_without_reset(self):
        self.add_wait_plan()
        # The signal from the0ms segment returns late and remains owned. The GPU
        # skips the8.000 tick and applies B at8.016666..., without resetting it.
        skipped = 1000000+8*60000
        self.events = [event for event in self.events if event.get("scheduledQpc") != skipped]
        self.events.append({"stage": "present.wait.segment", "worker": "GPU", "qpc": skipped+1000,
                            "scheduledQpc": skipped+1000, "deadlineQpc": 1000000+16*60000,
                            "imageId": 0, "generatedQpc": 0, "detail": "1", "value": 1})
        self.add_readiness(7.999, requested_ms=0, actual_ms=2, outcome="deadline")
        reused, first = self.add_readiness(9, requested_ms=0, mode="retained", outcome="retained", image_id=2)
        self.events.append(dict(reused, stage="present.start", qpc=first+18))
        self.write()
        result, _ = analyze(self.logs, 10, 14)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["presentReadiness"]["configuredWaitMs"], 1)
        self.events[-3]["detail"] = "native" # Discarding/resetting the permission is not legal.
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_wait_plan_late_segment_skip_is_valid_but_same_value_transition_is_required(self):
        self.add_wait_plan()
        first, last = 1000000+8*60000, 1000000+16*60000
        self.events = [event for event in self.events if not first <= event.get("scheduledQpc", 0) < last]
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual(result["presentWaitPlanValidation"]["observedAppliedIndices"], [0, 2, 3])
        # Restoring work in B1 but omitting the separate B2 transition is invalid,
        # even though both settings have waitMs1.
        self.events.append({"stage": "present.wait.segment", "worker": "GPU", "qpc": first,
                            "scheduledQpc": first, "deadlineQpc": last, "imageId": 0, "generatedQpc": 0, "detail": "1", "value": 1})
        self.events = [event for event in self.events if not (event["stage"] == "present.wait.segment" and event["detail"] == "2")]
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_wait_plan_baab_and_new_fixed_definitions(self):
        self.add_wait_plan("baab")
        self.write()
        result, _ = analyze(self.logs, 2, 6)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual([part["waitMs"] for part in result["presentWaitSegments"]], [1, 0, 0, 1])
        self.assertEqual(result["presentReadiness"]["configuredWaitMs"], 1)
        self.manifest["options"].update(presentWaitPlan="fixed", presentWaitMs=1)
        self.manifest["presentWaitSegments"] = [{"index": 0, "waitMs": 1, "startSeconds": 0, "endSeconds": 32,
                                                "analysisStartSeconds": 2, "analysisEndSeconds": 30,
                                                "startQpc": 1000000, "endQpc": 2920000}]
        self.events = [event for event in self.events if event["stage"] != "present.wait.segment"]
        self.events.append({"stage": "present.wait.segment", "worker": "GPU", "qpc": 1000000, "scheduledQpc": 1000000,
                            "deadlineQpc": 2920000, "imageId": 0, "generatedQpc": 0, "detail": "0", "value": 1})
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    def test_wait_plan_invalid_definitions_commands_and_events(self):
        self.add_wait_plan()
        original_manifest = json.loads(json.dumps(self.manifest))
        original_events = list(self.events)
        for fault in ("plan", "base_wait", "spout_only", "guard", "missing_segments", "wrong_segments", "wrong_event", "missing_first_event"):
            with self.subTest(fault=fault):
                self.manifest = json.loads(json.dumps(original_manifest))
                self.events = [dict(event) for event in original_events]
                if fault == "plan":
                    self.manifest["options"]["presentWaitPlan"] = "random"
                elif fault == "base_wait":
                    self.manifest["options"]["presentWaitMs"] = 1
                elif fault == "spout_only":
                    self.manifest["options"]["output"] = "spout"
                elif fault == "guard":
                    self.manifest["options"]["warmup"] = 4
                elif fault == "missing_segments":
                    del self.manifest["presentWaitSegments"]
                elif fault == "wrong_segments":
                    self.manifest["presentWaitSegments"][1]["waitMs"] = 0
                elif fault == "wrong_event":
                    self.events[-1]["value"] = 1
                else:
                    self.events = [event for event in self.events if not (event["stage"] == "present.wait.segment" and event["detail"] == "0")]
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest = original_manifest
        self.events = original_events
        self.write()
        (self.root / "command.json").write_text('{"presentWaitMs":0,"presentWaitPlan":"baab"}', encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "command.json").write_text('{"presentWaitMs":0,"presentWaitPlan":"abba"}', encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    # --- fence source sync -------------------------------------------------------------
    def enable_fence(self):
        self.manifest["options"].update(sourceSync="fence", copyRetry="off")

    def add_fence_copy(self, seconds=6, image_id=361, wait=True, worker="Spout"):
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        scheduled = origin+round(seconds*frequency)
        source = next(event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == image_id)
        common = {"worker": worker, "scheduledQpc": scheduled, "imageId": image_id, "generatedQpc": source["generatedQpc"]}
        if wait:
            self.events.append(dict(common, stage="copy.fence.wait", qpc=scheduled+10, detail="first:1", value=image_id))
        self.events.extend([dict(common, stage="copy.start", qpc=scheduled+12), dict(common, stage="copy.complete", qpc=scheduled+40)])
        return common

    def add_fence_display(self, seconds=6, image_id=361, wait=True):
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        scheduled = origin+round(seconds*frequency)
        source = next(event for event in self.events if event["stage"] == "compose.publish" and event["imageId"] == image_id)
        common = {"worker": "GPU", "scheduledQpc": scheduled, "imageId": image_id, "generatedQpc": source["generatedQpc"]}
        if wait:
            self.events.append(dict(common, stage="display.fence.wait", qpc=scheduled+5, detail="1", value=image_id))
        self.events.extend([dict(common, stage="display.draw.start", qpc=scheduled+6), dict(common, stage="display.draw.complete", qpc=scheduled+30)])
        return common

    def test_fence_log_without_keyed_mutex_evidence_is_valid(self):
        self.enable_fence()
        self.add_fence_copy(6)
        self.add_fence_copy(7, image_id=421)
        self.add_fence_display(6)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["sourceSync"]
        self.assertEqual((section["sourceSync"], section["available"]), ("fence", True))
        self.assertEqual((section["copyFenceWaitsInWindow"], section["displayFenceWaitsInWindow"], section["keyedMutexEvents"]), (2, 1, 0))
        self.assertEqual(sum(section["keyedMutexBusySkips"].values()), 0)
        self.assertEqual(result["phaseDurations"]["copy.start->copy.complete"]["durationMs"]["count"], 2)

    def test_old_log_has_no_source_sync_or_vsync_sections_enabled(self):
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertFalse(result["sourceSync"]["available"])
        self.assertIsNone(result["sourceSync"]["sourceSync"])
        self.assertFalse(result["vsync"]["available"])

    def test_fence_log_with_stray_keyed_mutex_event_is_invalid(self):
        self.enable_fence()
        common = self.add_fence_copy(6)
        self.events.append(dict(common, stage="copy.mutex.acquire", qpc=common["scheduledQpc"]+11, detail="first:1"))
        self.events.append(dict(common, stage="copy.mutex.release", qpc=common["scheduledQpc"]+41, detail="first:1"))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("keyed mutex acquire/release" in reason for reason in result["invalidReasons"]))
        self.assertEqual(result["sourceSync"]["keyedMutexEvents"], 2)

    def test_fence_log_with_keyed_busy_skip_is_invalid(self):
        self.enable_fence()
        common = self.add_fence_copy(6)
        self.events.append(dict(common, stage="skip", qpc=common["scheduledQpc"]+50, detail="copy.keyedMutexBusy", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("busy skips" in reason for reason in result["invalidReasons"]))

    def test_fence_copy_start_without_fence_wait_is_invalid(self):
        self.enable_fence()
        self.add_fence_copy(6, wait=False)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("copy.start requires exactly one earlier copy.fence.wait" in reason for reason in result["invalidReasons"]))
        # A wait recorded after the copy started is also invalid.
        self.events = [event for event in self.events if event["stage"] != "copy.start"]
        common = self.add_fence_copy(7, image_id=421, wait=False)
        self.events.append(dict(common, stage="copy.fence.wait", qpc=common["scheduledQpc"]+13, detail="first:1", value=421))
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_fence_wait_value_must_equal_image_id_and_draw_needs_wait(self):
        self.enable_fence()
        common = self.add_fence_copy(6)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        next(event for event in self.events if event["stage"] == "copy.fence.wait")["value"] = 360
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = [event for event in self.events if not event["stage"].startswith("copy.")]
        self.add_fence_display(6, wait=False)
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_fence_option_scope_and_keyed_logs_reject_fence_events(self):
        self.enable_fence()
        self.add_fence_copy(6)
        self.write()
        baseline = dict(self.manifest["options"])
        for field, value in (("mode", "common"), ("output", "fullscreen"), ("copyRetry", "signal"), ("sourceSync", "other")):
            with self.subTest(field=field):
                self.manifest["options"] = dict(baseline, **{field: value})
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest["options"] = dict(baseline, sourceSync="keyed")
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.manifest["options"] = baseline
        self.write()
        (self.root / "command.json").write_text('{"sourceSync":"keyed"}', encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        (self.root / "command.json").write_text('{"sourceSync":"fence"}', encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])

    # --- vsync display pacing ------------------------------------------------------------
    def add_vsync_wait(self, seconds=6, duration_ms=2, outcome="ready", permission=1, timeout=15):
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        scheduled = origin+round(seconds*frequency)
        first, deadline = scheduled+60, scheduled+1000  # 940 ticks = 15.67 ms remain at wait.start
        common = {"worker": "GPU", "scheduledQpc": scheduled, "deadlineQpc": deadline, "imageId": 0, "generatedQpc": 0}
        self.events.extend([dict(common, stage="display.vsync.wait.start", qpc=first, detail="native", value=timeout),
                            dict(common, stage="display.vsync.wait.end", qpc=first+round(duration_ms*frequency/1000), detail=outcome, value=permission)])
        return common, first+round(duration_ms*frequency/1000)

    def add_vsync_presentation(self, common, first, image_id=None):
        selected = self.add_display_selection(common, first, image_id)
        self.events.extend([dict(selected, stage="display.draw.start", qpc=first+4),
                            dict(selected, stage="display.draw.complete", qpc=first+8),
                            dict(selected, stage="present.start", qpc=first+10),
                            dict(selected, stage="present.return", qpc=first+12)])
        return selected

    def test_vsync_valid_log_counts_waits_presents_and_timeouts(self):
        self.enable_display_pacing("vsync")
        for seconds in (6, 7):
            slot, returned = self.add_vsync_wait(seconds)
            self.add_vsync_presentation(slot, returned+1)
        self.add_vsync_wait(8, duration_ms=15.3, outcome="timeout", permission=0)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["vsync"]
        self.assertTrue(section["available"])
        self.assertEqual(section["outcomes"], {"ready": 2, "timeout": 1})
        self.assertEqual(section["pairedWaitsInWindow"], 3)
        self.assertEqual(section["presentsInWindow"], 2)
        self.assertAlmostEqual(section["timeoutOverrunMs"]["max"], .3)
        self.assertEqual(result["displayWait"]["available"], False)
        self.assertEqual(result["presentReadiness"]["modes"], {"native": 0, "retained": 0})

    def test_vsync_wait_timeout_allows_budget_clock_one_millisecond_ahead_of_stamp(self):
        # Budget floored before wait.start is stamped: 15 (same ms) and 16 (boundary crossed) are valid; 14 and 17 are not.
        base = list(self.events)
        for timeout, valid in ((15, True), (16, True), (14, False), (17, False)):
            with self.subTest(timeout=timeout):
                self.events = list(base)
                self.enable_display_pacing("vsync")
                slot, returned = self.add_vsync_wait(6, timeout=timeout)
                self.add_vsync_presentation(slot, returned+1)
                self.write()
                self.assertEqual(analyze(self.logs)[0]["validPerformanceResult"], valid)

    def test_vsync_late_grant_carries_to_next_slot_without_new_wait(self):
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6, duration_ms=15.9)
        self.events.append(dict(slot, stage="skip", qpc=returned+1, detail="display.vsync.deadline", value=1))
        origin = self.manifest["originQpc"]
        next_slot = {"worker": "GPU", "scheduledQpc": origin+361000, "deadlineQpc": origin+362000, "imageId": 0, "generatedQpc": 0}
        self.add_vsync_presentation(next_slot, origin+361050)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual(result["vsync"]["skips"], {"display.vsync.deadline": 1})

    def test_vsync_present_without_ready_grant_is_invalid(self):
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6, outcome="timeout", permission=0)
        self.add_vsync_presentation(slot, returned+1)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("without an unconsumed ready grant" in reason for reason in result["invalidReasons"]))
        self.events = [event for event in self.events if not event["stage"].startswith("display.vsync.")]
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_vsync_one_grant_permits_one_present_and_no_rewait_while_retained(self):
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6)
        self.add_vsync_presentation(slot, returned+1)
        self.events.append(dict(next(event for event in self.events if event["stage"] == "present.start"), qpc=returned+20))
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = [event for event in self.events if not (event["stage"].startswith("display.") or event["stage"] == "present.start" or event["worker"] == "GPU" and event["stage"] == "present.return")]
        self.add_vsync_wait(6)
        self.add_vsync_wait(7)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("while a ready grant is retained" in reason for reason in result["invalidReasons"]))

    def test_vsync_same_id_or_decreasing_id_re_present_is_invalid(self):
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6)
        self.add_vsync_presentation(slot, returned+1)
        baseline = list(self.events)
        for stale in (361, 300):
            with self.subTest(stale=stale):
                self.events = list(baseline)
                next_slot, next_return = self.add_vsync_wait(7)
                self.add_vsync_presentation(next_slot, next_return+1, image_id=stale)
                self.write()
                result, _ = analyze(self.logs)
                self.assertFalse(result["validPerformanceResult"])
                self.assertTrue(any("strictly increase" in reason for reason in result["invalidReasons"]))

    def test_vsync_rejects_not_ready_readiness_attempts_and_bad_scope(self):
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6)
        self.add_vsync_presentation(slot, returned+1)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        baseline = list(self.events)
        self.events.append(dict(slot, stage="skip", qpc=returned+30, detail="present.notReady", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("present.notReady cannot occur" in reason for reason in result["invalidReasons"]))
        self.events = list(baseline)
        selected = next(event for event in self.events if event["stage"] == "display.select.end")
        self.events.extend([dict(selected, stage="present.ready.start", qpc=returned+2, detail="retained", value=0),
                            dict(selected, stage="present.ready.end", qpc=returned+3, detail="retained", value=1)])
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = list(baseline)
        for fault in ("wrong_budget", "has_image", "far_deadline", "retained_mode", "timeout_grant"):
            with self.subTest(fault=fault):
                self.events = list(baseline)
                wait_start = next(event for event in self.events if event["stage"] == "display.vsync.wait.start")
                wait_end = next(event for event in self.events if event["stage"] == "display.vsync.wait.end")
                if fault == "wrong_budget": wait_start["value"] = 14
                elif fault == "has_image": wait_start["imageId"] = 1
                elif fault == "far_deadline": wait_start["deadlineQpc"] += 1000; wait_end["deadlineQpc"] += 1000
                elif fault == "retained_mode": wait_start["detail"] = "retained"
                else: wait_end["detail"] = "timeout"
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = list(baseline)
        self.write()
        for field, value in (("output", "spout"), ("presentWaitMs", 1), ("presentWaitPlan", "abba")):
            with self.subTest(field=field):
                original = self.manifest["options"][field]
                self.manifest["options"][field] = value
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
                self.manifest["options"][field] = original
        self.manifest["options"]["displayPacing"] = "tick"
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    # --- DXGI frame statistics (scanout) --------------------------------------------------
    def enable_scanout(self, observe_after=800, sync_after=500):
        # present.return value = PresentCount (1-based per Present). Each scanout is observed observe_after ticks
        # after the return; its vblank (SyncQPCTime) is sync_after ticks after the return; SyncRefreshCount steps by one.
        count = 0
        for event in list(self.events):
            if event["stage"] != "present.return":
                continue
            count += 1
            event.update(worker="GPU", value=count)
            self.events.append({key: event[key] for key in ("worker", "scheduledQpc", "imageId", "generatedQpc")} | {"stage": "present.start", "qpc": event["qpc"]-5})
            self.events.append({"stage": "present.scanout", "worker": "GPU", "qpc": event["qpc"]+observe_after, "scheduledQpc": 0,
                                "imageId": event["imageId"], "generatedQpc": event["generatedQpc"],
                                "detail": f"{count}:{100+count}", "value": 100+count, "deadlineQpc": event["qpc"]+sync_after})
        self.summary.update(scanoutPending=0, scanoutDisjoint=False)

    def scanout(self, count):
        return next(event for event in self.events if event["stage"] == "present.scanout" and event["detail"].split(":")[0] == str(count))

    def test_scanout_valid_log_reports_display_latency_and_refresh_steps(self):
        self.enable_scanout()
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["scanout"]
        self.assertTrue(section["available"])
        self.assertEqual((section["scanoutsInWindow"], section["mappedInWindow"], section["unmappedInWindow"], section["unmappedCount"], section["disjointCount"]), (1320, 1320, 0, 0, 0))
        self.assertAlmostEqual(section["generatedToSyncMs"]["mean"], 560/60)
        self.assertAlmostEqual(section["presentStartToSyncMs"]["max"], 505/60)
        self.assertAlmostEqual(section["composePublishToSyncMs"]["p99"], 500/60)
        self.assertAlmostEqual(section["observationLagMs"]["mean"], 300/60)
        self.assertEqual(section["syncRefreshStep"], {"notOneCount": 0, "counts": {"1": 1319}})
        self.assertEqual(section["presentRefreshMinusSyncRefreshCounts"], {"0": 1320})
        self.assertEqual((section["appScanoutPending"], section["appScanoutDisjoint"]), (0, False))
        self.assertEqual(result["metrics"]["present.scanout"]["count"], 1320)

    def test_scanout_jump_unmapped_and_disjoint_are_counted_not_invalid(self):
        self.enable_scanout()
        skipped = self.scanout(400)  # PresentCount 400 was never observed: the next observation jumps by two refreshes.
        self.events.remove(skipped)
        self.events.append({"stage": "present.stats.disjoint", "worker": "GPU", "qpc": 1000001, "detail": "disjoint"})
        last = max(event["qpc"] for event in self.events)
        self.events.append({"stage": "present.scanout", "worker": "GPU", "qpc": last+1, "imageId": 0, "generatedQpc": 0,
                            "detail": "5000:9999:unmapped", "value": 9999, "deadlineQpc": last})
        evicted = self.scanout(600)  # Table capacity exceeded: the count is known to statistics but no longer mapped.
        evicted.update(imageId=0, generatedQpc=0, detail="600:700:unmapped")
        self.summary.update(scanoutPending=1, scanoutDisjoint=True)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["scanout"]
        self.assertEqual((section["scanoutsInWindow"], section["mappedInWindow"], section["unmappedCount"], section["unmappedInWindow"], section["disjointCount"]), (1319, 1318, 2, 1, 1))
        self.assertEqual(section["syncRefreshStep"], {"notOneCount": 1, "counts": {"1": 1317, "2": 1}})
        self.assertEqual((section["appScanoutPending"], section["appScanoutDisjoint"]), (1, True))
        self.assertEqual(section["generatedToSyncMs"]["count"], 1318)

    def test_scanout_present_count_regression_is_invalid(self):
        self.enable_scanout()
        stale = self.scanout(500)
        stale.update(detail="400:600", value=600)  # Stats reported an older PresentCount after a newer one.
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("PresentCount regression" in reason for reason in result["invalidReasons"]))

    def test_scanout_duplicate_present_count_is_invalid(self):
        self.enable_scanout()
        again = dict(self.scanout(500))
        again["qpc"] += 1
        self.events.append(again)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("Duplicate present.scanout PresentCount" in reason for reason in result["invalidReasons"]))
        self.events.remove(again)
        duplicate = dict(next(event for event in self.events if event["stage"] == "present.return" and event["value"] == 500))
        duplicate.update(qpc=duplicate["qpc"]+1, scheduledQpc=duplicate["scheduledQpc"]+1)
        self.events.append(duplicate)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("Duplicate present.return PresentCount" in reason for reason in result["invalidReasons"]))

    def test_scanout_mapping_must_match_an_earlier_present_return(self):
        self.enable_scanout()
        baseline = [dict(event) for event in self.events]
        for fault, message in (("early", "before its present.return"), ("image", "image differs"), ("missing", "no present.return"),
                               ("worker", "GPU worker"), ("detail", "Invalid present.scanout detail"), ("uncounted", "positive integer PresentCount"),
                               ("unmapped_image", "zero image metadata")):
            with self.subTest(fault=fault):
                self.events = [dict(event) for event in baseline]
                target = self.scanout(500)
                if fault == "early": target["qpc"] = target["qpc"]-800-1
                elif fault == "image": target["imageId"] += 1
                elif fault == "missing": target["detail"] = "9000:600"
                elif fault == "worker": target["worker"] = "Spout"
                elif fault == "detail": target["detail"] = "500"
                elif fault == "uncounted": next(event for event in self.events if event["stage"] == "present.return" and event["value"] == 500)["value"] = 0
                else: target["detail"] = "500:600:unmapped"
                self.write()
                result, _ = analyze(self.logs)
                self.assertFalse(result["validPerformanceResult"])
                self.assertTrue(any(message in reason for reason in result["invalidReasons"]), result["invalidReasons"])

    def test_old_log_without_scanout_is_unavailable_and_valid(self):
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        section = result["scanout"]
        self.assertFalse(section["available"])
        self.assertEqual(section["metadataErrors"], [])
        self.assertEqual((section["scanoutsInWindow"], section["unmappedCount"], section["disjointCount"]), (0, 0, 0))
        self.assertIsNone(section["appScanoutPending"])
        self.assertNotIn("present.scanout", result["metrics"])

    # --- vblank display pacing ------------------------------------------------------------
    def enable_vblank(self, margin=3):
        self.enable_display_pacing("vblank")
        self.manifest["options"]["presentMarginMs"] = margin
        self.manifest["highResolutionTimer"] = True
        self.manifest["loopTimerHighResolution"] = {"gpu": True, "spout": None}

    def vblank_slot(self, seconds, shift=0):
        # shift: compose align offset (ticks) carried by the slot's scheduledQpc.
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        scheduled = origin+round(seconds*frequency)+shift
        return {"worker": "GPU", "scheduledQpc": scheduled, "deadlineQpc": scheduled+1000, "imageId": 0, "generatedQpc": 0}

    def add_vblank_bootstrap(self, seconds=6):
        slot = self.vblank_slot(seconds)
        self.events.append(dict(slot, stage="display.vblank.bootstrap", qpc=slot["scheduledQpc"]+50, value=1))
        return self.add_vsync_presentation(slot, slot["scheduledQpc"]+60)

    def add_vblank_wait(self, slot, first, deadline, kind="target", outcome=None, lateness=3):
        # Ends `lateness` ticks (3 ticks = 50 us at 60 kHz) after the deadline; compose/cancelled outcomes record lateness 0.
        frequency = self.manifest["qpcFrequency"]
        outcome = kind if outcome is None else outcome
        end = deadline+lateness
        self.events.extend([dict(slot, stage="display.vblank.wait.start", qpc=first, detail=kind, value=round((deadline-first)*1e6/frequency), deadlineQpc=deadline),
                            dict(slot, stage="display.vblank.wait.end", qpc=end, detail=outcome, value=round((end-deadline)*1e6/frequency) if outcome == "target" else 0, deadlineQpc=deadline)])
        return end

    def add_vblank_predicted(self, seconds=7, refresh=101, present=True, image_id=None, lateness=3, vblank_offset=900, present_offset=None, shift=0):
        # Predicted vblank 15 ms after the slot (vblank_offset ticks), target 3 ms earlier; predict is stamped after the wait, before
        # the selection. The selection/draw/present deadline of a predicted attempt is the predicted vblank, not the compose deadline.
        slot = self.vblank_slot(seconds, shift)
        vblank_qpc = slot["scheduledQpc"]+vblank_offset
        end = self.add_vblank_wait(slot, slot["scheduledQpc"]+60, vblank_qpc-180, lateness=lateness)
        first = end+2 if present_offset is None else slot["scheduledQpc"]+present_offset
        if image_id is None:
            visible = [event for event in self.events if event["stage"] == "compose.visible" and event["qpc"] <= first]
            image_id = max(visible, key=lambda event: event["qpc"])["imageId"]
        predict = dict(slot, stage="display.vblank.predict", qpc=end+1, imageId=image_id, generatedQpc=0, value=refresh, deadlineQpc=vblank_qpc, detail="16667")
        self.events.append(predict)
        if present:
            self.add_vsync_presentation(dict(slot, deadlineQpc=vblank_qpc), first, image_id)
        return slot, predict

    def add_vblank_scanout(self, error_ticks=15):
        # PresentCount in present.return order. SyncQPCTime = predicted vblank + error for predicted presents (0.25 ms
        # at 15 ticks), return + 500 ticks otherwise. No present.start is synthesized for the fixture's own presents.
        predictions = {(event["scheduledQpc"], event["imageId"]): event for event in self.events if event["stage"] == "display.vblank.predict"}
        count = 0
        for event in sorted([event for event in self.events if event["stage"] == "present.return"], key=lambda event: event["qpc"]):
            count += 1
            event["value"] = count
            prediction = predictions.get((event["scheduledQpc"], event["imageId"])) if event["worker"] == "GPU" else None
            sync = prediction["deadlineQpc"]+error_ticks if prediction else event["qpc"]+500
            self.events.append({"stage": "present.scanout", "worker": "GPU", "qpc": max(event["qpc"], sync)+300, "scheduledQpc": 0,
                                "imageId": event["imageId"], "generatedQpc": event["generatedQpc"],
                                "detail": f"{count}:{100+count}", "value": 100+count, "deadlineQpc": sync})
        self.summary.update(scanoutPending=0, scanoutDisjoint=False)

    def test_vblank_valid_log_reports_waits_predictions_and_bootstrap(self):
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        self.add_vblank_predicted(8, refresh=101)  # Repeated refresh label (statistics numbering one off): distinct vblank, still valid.
        self.add_vblank_scanout()
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["vblank"]
        self.assertTrue(section["available"])
        self.assertEqual(section["presentsAfterComposeDeadline"], 0)
        self.assertEqual((section["presentMarginMs"], section["highResolutionTimer"]), (3, True))
        self.assertEqual(result["loopTimerHighResolution"], {"gpu": True, "spout": None})
        self.assertEqual((section["waitsInWindow"], section["waitOutcomes"]), (2, {"target": 2}))
        self.assertAlmostEqual(section["wakeLatenessMs"]["mean"], .05)
        self.assertAlmostEqual(section["requestedWaitMs"]["max"], 11)
        self.assertEqual(section["predictionErrorMs"]["count"], 2)
        self.assertAlmostEqual(section["predictionErrorMs"]["mean"], .25)
        self.assertEqual((section["presentsInWindow"], section["predictedPresentsInWindow"]), (3, 2))
        self.assertEqual(section["counts"], {"noNewerImage": 0, "notReady": 0, "bootstrap": 1})
        self.assertFalse(result["vsync"]["available"])
        self.assertFalse(result["displayWait"]["available"])
        self.assertEqual(result["presentReadiness"]["modes"], {"native": 0, "retained": 0})
        self.assertTrue(result["scanout"]["available"])
        self.assertEqual(result["scanout"]["syncRefreshStep"]["notOneCount"], 0)

    def test_vblank_two_presents_within_half_a_period_is_invalid(self):
        # Consecutive compose slots (1/60 s apart, 1000 ticks): the second predicted vblank is 400 ticks after the first, less than
        # half the 16667 us period (500 ticks at 60 kHz); with offsets 900/900 (1000 apart) the same log is valid.
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        baseline = [dict(event) for event in self.events]
        for offset, valid in ((900, True), (300, False), (-500, False)):
            with self.subTest(offset=offset):
                self.events = [dict(event) for event in baseline]
                self.add_vblank_predicted(7+1/60, refresh=102, vblank_offset=offset, present_offset=offset-150)
                self.write()
                result, _ = analyze(self.logs)
                self.assertEqual(result["validPerformanceResult"], valid, result["invalidReasons"])
                self.assertEqual(any("one present per predicted vblank" in reason for reason in result["invalidReasons"]), not valid, result["invalidReasons"])

    def test_vblank_present_after_compose_deadline_before_predicted_vblank_is_valid(self):
        # Predicted vblank 1100 ticks after the slot, i.e. after the compose deadline (slot+1000); the selection starts after the
        # compose deadline (once the next tick's image is visible) and present.start follows it, both before the predicted vblank,
        # which is the recorded deadline. (No scanout fixture: the per-tick fixture presents would reorder PresentCounts.)
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        slot, _ = self.add_vblank_predicted(8, refresh=102, vblank_offset=1100, present_offset=1002)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual((result["vblank"]["presentsInWindow"], result["vblank"]["presentsAfterComposeDeadline"]), (3, 1))
        self.assertEqual((result["vblank"]["presentsBeforeDueCompose"], result["vblank"]["presentedIdGaps"]), (1, 2))  # Presents a second apart skip ids.
        # The old compose deadline on a predicted attempt's selection is invalid (and here the selection also started after it).
        for event in self.events:
            if event["stage"].startswith("display.select.") and event["scheduledQpc"] == slot["scheduledQpc"]:
                event["deadlineQpc"] = slot["scheduledQpc"]+1000
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("min(predicted vblank, common end)" in reason for reason in result["invalidReasons"]), result["invalidReasons"])

    def test_vblank_present_before_due_compose_is_valid_and_counted_with_id_gaps(self):
        # Ordering fix: the target wake-up landed after the compose deadline (slot+1000) and the present (slot+1002) ran before
        # the due compose started (compose.start at slot+1000 scheduled tick, qpc after the present); still before the
        # predicted vblank (slot+1100). A compose that started before the present is not counted.
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        slot, _ = self.add_vblank_predicted(8, refresh=102, vblank_offset=1100, present_offset=1002)
        due = dict(self.vblank_slot(8+1/60), imageId=0, generatedQpc=0)
        self.events.append(dict(due, stage="compose.start", qpc=slot["scheduledQpc"]+1030))
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["vblank"]
        self.assertEqual((section["presentsInWindow"], section["presentsAfterComposeDeadline"], section["presentsBeforeDueCompose"], section["presentedIdGaps"]), (3, 1, 1, 2))
        self.events[-1]["qpc"] = slot["scheduledQpc"]+1001  # The due compose started before the present: not before-due.
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual((result["vblank"]["presentsAfterComposeDeadline"], result["vblank"]["presentsBeforeDueCompose"]), (1, 0))
        # presentedIdGaps: consecutive compose slots present consecutive ids (only the bootstrap -> 7 s gap remains); skipping
        # the next slot skips one id and adds a gap.
        baseline = [event for event in self.events if event["stage"] != "compose.start" and not (event["stage"].startswith(("display.", "present.")) and event.get("scheduledQpc") == slot["scheduledQpc"])]
        for ticks, gaps in ((1, 1), (2, 2)):
            with self.subTest(ticks=ticks):
                self.events = [dict(event) for event in baseline]
                self.add_vblank_predicted(7+ticks/60, refresh=102)
                self.write()
                result, _ = analyze(self.logs)
                self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
                self.assertEqual((result["vblank"]["presentsInWindow"], result["vblank"]["presentedIdGaps"]), (3, gaps))

    def test_vblank_present_without_matching_prediction_is_invalid(self):
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        slot = self.vblank_slot(7)
        self.add_vsync_presentation(slot, slot["scheduledQpc"]+60)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("requires a preceding display.vblank.predict" in reason for reason in result["invalidReasons"]), result["invalidReasons"])
        self.events = [event for event in self.events if not (event["worker"] == "GPU" and event.get("scheduledQpc") == slot["scheduledQpc"])]
        _, predict = self.add_vblank_predicted(8, refresh=101)
        predict["imageId"] += 1  # Prediction named a different image than the one presented.
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_vblank_old_and_vsync_logs_are_unaffected(self):
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertFalse(result["vblank"]["available"])
        self.assertEqual(result["vblank"]["metadataErrors"], [])
        self.assertIsNone(result["vblank"]["presentMarginMs"])
        self.enable_display_pacing("vsync")
        slot, returned = self.add_vsync_wait(6)
        self.add_vsync_presentation(slot, returned+1)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertTrue(result["vsync"]["available"])
        self.assertFalse(result["vblank"]["available"])

    def test_vblank_not_ready_no_newer_image_and_bootstrap_are_counted(self):
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        slot, predict = self.add_vblank_predicted(7, refresh=101, present=False)
        self.events.append(dict(slot, stage="skip", qpc=predict["qpc"]+1, detail="display.vblank.notReady", value=1))
        self.add_vblank_wait(slot, predict["qpc"]+2, slot["deadlineQpc"], kind="compose")  # Deferred: the next target is after the compose deadline.
        self.add_vblank_predicted(8, refresh=102)
        idle = self.vblank_slot(9)
        self.events.append(dict(idle, stage="skip", qpc=idle["scheduledQpc"]+700, detail="display.vblank.noNewerImage", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["vblank"]
        self.assertEqual(section["counts"], {"noNewerImage": 1, "notReady": 1, "bootstrap": 1})
        self.assertEqual(section["waitOutcomes"], {"target": 2, "compose": 1})
        self.assertEqual((section["presentsInWindow"], section["predictedPresentsInWindow"]), (2, 1))
        self.assertEqual(section["wakeLatenessMs"]["count"], 2)
        self.events.append(dict(idle, stage="skip", qpc=idle["scheduledQpc"]+710, detail="present.notReady", value=1))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("present.notReady cannot occur with vblank" in reason for reason in result["invalidReasons"]))

    def test_vblank_option_scope_wait_metadata_and_id_faults_are_invalid(self):
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        baseline = [dict(event) for event in self.events]
        for field, value in (("output", "spout"), ("presentWaitMs", 1), ("presentWaitPlan", "abba"), ("presentMarginMs", 9), ("presentMarginMs", 0.4), ("presentMarginMs", "x"), ("displayPacing", "tick")):
            with self.subTest(field=field, value=value):
                original = self.manifest["options"][field]
                self.manifest["options"][field] = value
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
                self.manifest["options"][field] = original
        for fault in ("negative_request", "zero_deadline", "error", "bad_kind", "has_image", "unpaired", "lateness_text"):
            with self.subTest(fault=fault):
                self.events = [dict(event) for event in baseline]
                wait_start = next(event for event in self.events if event["stage"] == "display.vblank.wait.start")
                wait_end = next(event for event in self.events if event["stage"] == "display.vblank.wait.end")
                if fault == "negative_request": wait_start["value"] = -1
                elif fault == "zero_deadline": wait_start["deadlineQpc"] = 0; wait_end["deadlineQpc"] = 0
                elif fault == "error": wait_end["detail"] = "error"
                elif fault == "bad_kind": wait_start["detail"] = "other"
                elif fault == "has_image": wait_start["imageId"] = 1
                elif fault == "unpaired": self.events.remove(wait_end)
                else: wait_end["value"] = "late"
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
        self.events = [dict(event) for event in baseline]
        self.add_vblank_predicted(8, refresh=102, image_id=300)
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("strictly increase" in reason for reason in result["invalidReasons"]))
        # A tick log may not carry vblank events or a non-default margin.
        self.events = [dict(event) for event in baseline]
        self.manifest["options"]["displayPacing"] = "tick"
        self.manifest["options"]["presentMarginMs"] = 3
        self.events = [event for event in self.events if not (event["worker"] == "GPU" and event["stage"] not in ("compose.publish", "compose.visible", "lifecycle", "present.wait.segment"))]
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"], analyze(self.logs)[0]["invalidReasons"])
        self.manifest["options"]["presentMarginMs"] = 2
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_vblank_command_margin_must_match_manifest(self):
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        self.write()
        command = self.root / "command.json"
        command.write_text(json.dumps({"presentWaitMs": 0, "presentWaitPlan": "fixed", "displayPacing": "vblank", "presentMarginMs": 3}), encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"], analyze(self.logs)[0]["invalidReasons"])
        command.write_text(json.dumps({"presentWaitMs": 0, "presentWaitPlan": "fixed", "displayPacing": "vblank", "presentMarginMs": 2}), encoding="utf-8")
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("presentMarginMs must explicitly match" in reason for reason in result["invalidReasons"]))

    # --- compose phase alignment ------------------------------------------------------------
    def enable_compose_align(self, lead=1.5, align="vblank"):
        self.enable_vblank()
        self.manifest["options"].update(composeAlign=align, composeLeadMs=lead)
        self.manifest["alignSlewMs"] = 0.5

    def add_compose_start(self, slot):
        self.events.append(dict(slot, stage="compose.start", qpc=slot["scheduledQpc"]+5, imageId=0, generatedQpc=0))

    def add_compose_align(self, seconds, error_us, correction_us=None, at=801):
        # Recorded `at` ticks after the tick; the fixture's per-tick present.scanout is observed at tick+800.
        origin, frequency = self.manifest["originQpc"], self.manifest["qpcFrequency"]
        tick = origin+round(seconds*frequency)
        correction = -max(-500, min(500, error_us)) if correction_us is None else correction_us
        event = {"stage": "compose.align", "worker": "GPU", "qpc": tick+at, "scheduledQpc": 0, "imageId": 0, "generatedQpc": 0,
                 "value": error_us, "detail": str(correction), "deadlineQpc": tick+1200}
        self.events.append(event)
        return event

    def build_aligned_log(self):
        # Corrections -500, -200 (-700 us = -42 ticks at 60 kHz) before the 7 s slot, +100 before the 8 s slot (-36 ticks):
        # the GPU compose slots carry the running offset; the bootstrap slot precedes every correction.
        self.enable_compose_align()
        self.add_compose_start(self.vblank_slot(6))
        self.add_vblank_bootstrap(6)
        self.add_compose_align(6.5, 700)
        self.add_compose_align(6.5+1/60, 200)
        self.add_compose_start(self.vblank_slot(7, -42))
        self.add_vblank_predicted(7, refresh=101, shift=-42)
        self.add_compose_align(7.5, -100)
        self.add_compose_start(self.vblank_slot(8, -36))
        self.add_vblank_predicted(8, refresh=102, shift=-36)
        self.add_vblank_scanout()
        self.write()

    def test_compose_align_valid_log_reports_phase_error_offset_and_periods(self):
        self.build_aligned_log()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["composeAlign"]
        self.assertTrue(section["available"])
        self.assertEqual((section["composeAlign"], section["composeLeadMs"], section["alignSlewMs"]), ("vblank", 1.5, 0.5))
        self.assertEqual((section["alignEvents"], section["alignEventsInFirstSecond"], section["alignEventsInWindow"]), (3, 0, 3))
        self.assertAlmostEqual(section["phaseErrorAbsMs"]["mean"], 1/3)
        self.assertAlmostEqual(section["phaseErrorAbsMs"]["max"], .7)
        self.assertEqual(section["phaseErrorAbsMsFirstSecond"]["count"], 0)
        self.assertAlmostEqual(section["correctionSumMs"], -.6)
        self.assertAlmostEqual(section["finalOffsetMs"], -.6)
        self.assertAlmostEqual(section["composePeriodMs"], 1000/60)
        self.assertAlmostEqual(section["vblankPeriodMs"], 1000/60)
        self.assertAlmostEqual(section["periodDifferenceUs"], 0)
        self.assertEqual((result["vblank"]["presentsInWindow"], result["vblank"]["presentsAfterComposeDeadline"]), (3, 0))
        # A correction before the window and a first-second correction are counted separately.
        self.add_compose_align(.5, 300)
        self.write()
        section = analyze(self.logs)[0]["composeAlign"]
        self.assertEqual((section["alignEvents"], section["alignEventsInFirstSecond"], section["alignEventsInWindow"]), (4, 1, 3))
        self.assertAlmostEqual(section["phaseErrorAbsMsFirstSecond"]["mean"], .3)
        self.assertAlmostEqual(section["correctionSumWholeLogMs"], -.9)

    def test_compose_align_events_require_align_and_bounded_corrections(self):
        self.build_aligned_log()
        baseline = [dict(event) for event in self.events]
        self.manifest["options"]["composeAlign"] = "off"
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("compose.align events require composeAlign vblank" in reason for reason in result["invalidReasons"]), result["invalidReasons"])
        self.manifest["options"]["composeAlign"] = "vblank"
        for fault in ("over_slew", "not_clamped", "wrong_sign", "zero_deadline", "has_image", "before_scanout", "two_per_scanout", "text_detail"):
            with self.subTest(fault=fault):
                self.events = [dict(event) for event in baseline]
                first = next(event for event in self.events if event["stage"] == "compose.align")
                if fault == "over_slew": first["detail"] = "-600"
                elif fault == "not_clamped": first["detail"] = "-700"
                elif fault == "wrong_sign": first["detail"] = "500"
                elif fault == "zero_deadline": first["deadlineQpc"] = 0
                elif fault == "has_image": first["imageId"] = 1
                elif fault == "before_scanout": self.add_compose_align(0, 100, at=100)
                elif fault == "two_per_scanout": self.add_compose_align(6.5, 100, at=802)
                else: first["detail"] = "late"
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"], fault)
        self.events = [dict(event) for event in baseline]
        self.write()
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"])
        for field, value in (("displayPacing", "tick"), ("output", "spout"), ("composeLeadMs", 0.4), ("composeLeadMs", 9), ("composeAlign", "other")):
            with self.subTest(field=field, value=value):
                original = self.manifest["options"][field]
                self.manifest["options"][field] = value
                self.write()
                self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])
                self.manifest["options"][field] = original
        self.manifest["alignSlewMs"] = 0
        self.write()
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_compose_align_command_must_match_manifest(self):
        self.build_aligned_log()
        command = self.root / "command.json"
        base = {"presentWaitMs": 0, "presentWaitPlan": "fixed", "displayPacing": "vblank", "presentMarginMs": 3, "composeAlign": "vblank", "composeLeadMs": 1.5}
        command.write_text(json.dumps(base), encoding="utf-8")
        self.assertTrue(analyze(self.logs)[0]["validPerformanceResult"], analyze(self.logs)[0]["invalidReasons"])
        for change in ({"composeLeadMs": 2}, {"composeAlign": "off"}):
            with self.subTest(change=change):
                command.write_text(json.dumps(dict(base, **change)), encoding="utf-8")
                result, _ = analyze(self.logs)
                self.assertFalse(result["validPerformanceResult"])
                self.assertTrue(any("must explicitly match manifest options" in reason for reason in result["invalidReasons"]), result["invalidReasons"])
        command.write_text(json.dumps({key: value for key, value in base.items() if key != "composeLeadMs"}), encoding="utf-8")
        self.assertFalse(analyze(self.logs)[0]["validPerformanceResult"])

    def test_compose_align_slots_must_follow_offset_and_never_regress(self):
        self.build_aligned_log()
        baseline = [dict(event) for event in self.events]
        # A slot that ignores the running offset (unshifted 7 s slot) does not match the rebuilt schedule.
        for event in self.events:
            if event["worker"] == "GPU" and event.get("scheduledQpc") == self.vblank_slot(7, -42)["scheduledQpc"]:
                event["scheduledQpc"] += 42
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("rebuilt schedule offset" in reason for reason in result["invalidReasons"]), result["invalidReasons"])
        # A later compose.start with an earlier scheduledQpc (offset moved earlier handing out a past tick) is invalid.
        self.events = [dict(event) for event in baseline]
        late = self.vblank_slot(8, -36)
        self.events.append(dict(late, stage="compose.start", qpc=late["scheduledQpc"]+900, scheduledQpc=late["scheduledQpc"]-1000+964, imageId=0, generatedQpc=0))
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("strictly increase" in reason for reason in result["invalidReasons"]), result["invalidReasons"])
        # A display selection in a slot that was never taken is invalid under align.
        self.events = [event for event in baseline if event["stage"] != "compose.start" or event["scheduledQpc"] != late["scheduledQpc"]]
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("taken GPU compose tick" in reason for reason in result["invalidReasons"]), result["invalidReasons"])

    def test_compose_align_off_and_old_logs_are_unchanged(self):
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual((result["composeAlign"]["available"], result["composeAlign"]["metadataErrors"], result["composeAlign"]["alignEvents"]), (False, [], 0))
        self.enable_vblank()
        self.add_vblank_bootstrap(6)
        self.add_vblank_predicted(7, refresh=101)
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertFalse(result["composeAlign"]["available"])
        self.manifest["options"].update(composeAlign="off", composeLeadMs=1.5)
        self.manifest["alignSlewMs"] = 0.5
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual((result["composeAlign"]["available"], result["composeAlign"]["composeAlign"]), (True, "off"))
        self.manifest["options"]["composeLeadMs"] = 2
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertTrue(any("non-default composeLeadMs" in reason for reason in result["invalidReasons"]))


class SourceContractTests(unittest.TestCase):
    """--source contract-fake logs: source.acquire events and compose.sourceNotReady skips are accepted and summarized."""
    setUp, tearDown, write = ProbeAnalysisTests.setUp, ProbeAnalysisTests.tearDown, ProbeAnalysisTests.write  # Same fixture, no inherited tests.

    def enable_source(self, ready=True, worker="GPU"):
        self.manifest["options"]["source"] = "contract-fake"
        self.summary["sourceDiagnostics"] = {"decoder": "fake-pattern", "gpu": "test", "format": "BGRA8_UNORM", "generationRejected": 0,
                                             "notReady": 1, "replaced": 5, "dropped": 0, "peakLeases": 1, "offered": 8, "ready": 7, "ended": 0}
        acquires = []
        for event in [event for event in self.events if event["stage"] == "compose.publish"]:
            tick = event["scheduledQpc"]
            acquires.append({"stage": "source.acquire", "worker": worker, "qpc": tick-70, "scheduledQpc": tick,
                             "imageId": (event["imageId"]+1)//2 if ready else 0, "generatedQpc": tick-100 if ready else 0,
                             "detail": "Ready" if ready else "NotReady", "value": (tick-self.manifest["originQpc"])*1000000//self.manifest["qpcFrequency"]})
        self.events.extend(acquires)
        return acquires

    def test_contract_fake_log_is_valid_and_summarized(self):
        acquires = self.enable_source()
        acquires[0]["detail"], acquires[0]["imageId"], acquires[0]["generatedQpc"] = "NotReady", 0, 0
        first_tick = acquires[0]["scheduledQpc"]
        self.events.append({"stage": "skip", "worker": "GPU", "qpc": first_tick-60, "scheduledQpc": first_tick, "detail": "compose.sourceNotReady", "value": 1})
        # One in-window not-ready compose tick as the app records it (skip instead of a publish).
        window_tick = self.manifest["originQpc"]+10*self.manifest["qpcFrequency"]+500
        self.events.append({"stage": "source.acquire", "worker": "GPU", "qpc": window_tick, "scheduledQpc": window_tick, "imageId": 0, "generatedQpc": 0, "detail": "Ended", "value": 10000500})
        self.events.append({"stage": "skip", "worker": "GPU", "qpc": window_tick+5, "scheduledQpc": window_tick, "detail": "compose.sourceNotReady", "value": 1})
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        section = result["source"]
        self.assertEqual((section["source"], section["available"]), ("contract-fake", True))
        self.assertEqual(section["outcomes"], {"Ready": 1320, "NotReady": 0, "Ended": 1})
        self.assertEqual((section["acquiresInWindow"], section["sourceNotReadySkipsInWindow"]), (1321, 1))
        self.assertEqual(section["sourceDiagnostics"]["replaced"], 5)
        self.assertEqual(result["skips"]["GPU:compose.sourceNotReady"], {"events": 1, "skippedCount": 1.0})
        self.assertEqual(result["metrics"]["source.acquire"]["count"], 1321)

    def test_old_log_has_no_source_section_enabled(self):
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"])
        self.assertEqual((result["source"]["available"], result["source"]["source"], result["source"]["sourceDiagnostics"]), (False, None, None))
        self.assertEqual(result["source"]["acquiresInWindow"], 0)
        self.assertIsNone(result["placement"])

    def test_manifest_placement_is_passed_through_unvalidated(self):
        self.enable_source()
        self.manifest["options"].update({"sourceWidth": 2560, "sourceHeight": 1080})
        placement = {"source": {"width": 2560, "height": 1080}, "canvas": {"width": 1920, "height": 1080}, "fitId": "fit-height",
                     "destination": {"x": 0, "y": 0, "width": 1920, "height": 1080}, "sourceCrop": {"x": 320, "y": 0, "width": 1920, "height": 1080}}
        self.manifest["placement"] = placement
        self.write()
        result, _ = analyze(self.logs)
        self.assertTrue(result["validPerformanceResult"], result["invalidReasons"])
        self.assertEqual(result["placement"], placement)

    def test_source_acquire_without_contract_fake_option_is_invalid(self):
        self.enable_source()
        self.manifest["options"]["source"] = "pattern"
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertIn("source.acquire events require the contract-fake source option", result["invalidReasons"])

    def test_source_acquire_outcome_and_metadata_are_checked(self):
        acquires = self.enable_source()
        acquires[3]["detail"] = "Busy"
        acquires[4]["imageId"] = 0  # Ready without a source sequence.
        acquires[5]["worker"] = "Spout"
        self.write()
        result, _ = analyze(self.logs)
        self.assertFalse(result["validPerformanceResult"])
        self.assertIn("source.acquire requires GPU worker and detail Ready, NotReady, or Ended", result["invalidReasons"])
        self.assertIn("source.acquire Ready carries a source sequence and decode time; other outcomes carry none", result["invalidReasons"])


if __name__ == "__main__":
    unittest.main()
