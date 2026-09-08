"""Hand-calculated cases for the independent bitmap/LTC analyzer (stdlib only)."""
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "analyze-sync-accuracy.py"
FIXTURE = {"schema": 1, "ltcFps": 25, "clips": [
    {"id": 1, "name": "24", "fpsNumerator": 24, "fpsDenominator": 1,
     "frameCount": 288, "timelineOffset": 0, "mediaIn": 0, "mediaOut": 10, "path": "a.mp4"},
    {"id": 2, "name": "29.97", "fpsNumerator": 30000, "fpsDenominator": 1001,
     "frameCount": 360, "timelineOffset": 12, "mediaIn": 0, "mediaOut": 10, "path": "b.mp4"},
    {"id": 3, "name": "60", "fpsNumerator": 60, "fpsDenominator": 1,
     "frameCount": 720, "timelineOffset": 24, "mediaIn": 0, "mediaOut": 10, "path": "c.mp4"}]}


def frame(t, index=0, clip=1, black=False, valid=True, kind="normal"):
    return {"type": "frame", "ticks": t, "kind": kind, "width": 1920, "height": 1080,
            "clipId": clip if valid else None, "frameIndex": index if valid else None,
            "isBlack": black, "markerValid": valid, "probeTicks": 1}


def ltc(t, seconds):
    return {"type": "ltc", "ticks": t, "seconds": seconds, "fps": 25}


def render_stage(t):
    return {"type": "render-stage", "ticks": t, "sessionId": 1, "attemptId": 1,
            "generation": 0, "sequence": 1, "stage": "publish", "outcome": "normal",
            "startTicks": t - 1, "endTicks": t, "width": 1920, "height": 1080,
            "returnCode": None, "threadId": 7}


def trace(events, **end):
    return [{"type": "meta", "ticks": 0, "schema": 1, "frequency": 1000,
             "boundary": "bitmap-publication", "reference": "decoded-ltc-receipt"}] + events + [
        {"type": "end", "ticks": max((e["ticks"] for e in events), default=0) + 1,
         "dropped": 0, "errors": 0, "events": len(events), **end}]


def phase(name="black-sweep", start=0, duration=35, tick=1000, mode="black"):
    return [{"type": "phase-start", "name": name, "mode": mode, "ticks": tick,
             "frequency": 1000, "startSeconds": start, "durationSeconds": duration},
            {"type": "phase-end", "name": name, "ticks": tick + duration * 1000},
            {"type": "completed", "ticks": tick + duration * 1000 + 1}]


def complete_run():
    events, journal = [], []
    specs = [("black-sweep", 0, 35, "black"), ("freeze-sweep", 0, 35, "freeze"),
             ("seek-a", 3, 5, "black"), ("seek-b", 15, 5, "black"),
             ("seek-c", 27, 5, "black"), ("seek-back", 3, 5, "black")]
    tick = 1000
    for name, start, duration, mode in specs:
        journal.extend(phase(name, start, duration, tick, mode)[:2])
        for i in range(duration * 25):
            seconds = start + i / 25
            clip = next((c for c in FIXTURE["clips"]
                         if c["timelineOffset"] <= seconds < c["timelineOffset"] + 10), None)
            if clip:
                index = math.floor((seconds - clip["timelineOffset"]) *
                                   clip["fpsNumerator"] / clip["fpsDenominator"] + 1e-8)
                events.append(frame(tick + i * 40 - 1, index, clip["id"]))
            elif mode == "black":
                events.append(frame(tick + i * 40 - 1, black=True, valid=False))
            else:
                prev = max((c for c in FIXTURE["clips"] if c["timelineOffset"] < seconds),
                           key=lambda c: c["timelineOffset"])
                index = math.ceil(10 * prev["fpsNumerator"] / prev["fpsDenominator"]) - 1
                events.append(frame(tick + i * 40 - 1, index, prev["id"]))
            events.append(ltc(tick + i * 40, seconds))
        tick += (duration + 1) * 1000
    journal.append({"type": "completed", "ticks": tick})
    return trace(events), journal


class AccuracyTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.exists(), "The independent analyzer has not been implemented")
        spec = importlib.util.spec_from_file_location("sync_accuracy", SCRIPT)
        self.analyzer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.analyzer)

    def analyze(self, events, journal=None, fixture=FIXTURE):
        return self.analyzer.analyze(trace(events), fixture, journal or phase())

    def test_known_delay_and_frame_interval_are_separate(self):
        # Frame 21 at 24 fps = .875 s; target 1.0: -125 ms PTS,
        # frame ends .9166667: -83.3333 ms interval distance.
        summary, rows = self.analyze([ltc(1000, 0), frame(1999, 21), ltc(2000, 1)])
        self.assertAlmostEqual(rows[-1]["signedErrorMs"], -125)
        self.assertAlmostEqual(rows[-1]["intervalErrorMs"], -83.3333333333)
        self.assertEqual(summary["steady"]["measuredSamples"], 1)
        self.assertEqual(summary["steady"]["signed"]["meanSignedMs"], -125)

    def test_held_bitmap_is_measured_at_every_ltc(self):
        summary, rows = self.analyze([frame(999, 0), ltc(1000, 0), ltc(1040, .04), ltc(1080, .08)])
        self.assertEqual(len(rows), 3)
        self.assertEqual([r["signedErrorMs"] for r in rows], [0, -40, -80])
        self.assertEqual(rows[-1]["frameAgeMs"], 81)
        self.assertAlmostEqual(summary["all"]["signed"]["meanAbsMs"], 40)

    def test_frame_pts_uses_media_in_only_for_expected_position(self):
        fixture = json.loads(json.dumps(FIXTURE))
        fixture["clips"][0].update(mediaIn=2, mediaOut=10)
        _, rows = self.analyze([frame(999, 48), ltc(1000, 0)], fixture=fixture)
        self.assertEqual(rows[0]["signedErrorMs"], 0)

    def test_unknown_unpublished_and_wrong_clip_do_not_become_zero(self):
        summary, rows = self.analyze([ltc(1000, 0), frame(1039, valid=False), ltc(1040, .04),
                                     frame(1079, clip=2), ltc(1080, .08)])
        self.assertEqual([r["status"] for r in rows], ["unpublished", "unknown", "wrong-clip"])
        self.assertEqual(summary["all"]["measuredSamples"], 0)
        self.assertIsNone(summary["all"]["signed"]["meanAbsMs"])
        self.assertTrue(all(r["signedErrorMs"] is None for r in rows))

    def test_ticks_are_stably_sorted_before_sampling(self):
        _, rows = self.analyze([ltc(1040, .04), frame(999, 0), ltc(1000, 0), frame(1039, 1)])
        self.assertEqual(rows[1]["frameIndex"], 1)
        self.assertAlmostEqual(rows[1]["signedErrorMs"], 1.6666666667)

    def test_stale_previous_phase_ltc_does_not_start_seek_clock(self):
        _, rows = self.analyze([frame(999, 72), ltc(1000, 30), ltc(1040, 3), ltc(1080, 3.04)],
                               phase("seek-a", 3, 5))
        self.assertEqual(rows[0]["status"], "phase-unmatched")
        self.assertEqual(rows[1]["phaseElapsedMs"], 0)

    def test_input_gap_does_not_claim_seconds_of_threshold_exceedance(self):
        summary, rows = self.analyze([frame(999, 0), ltc(1000, 0), ltc(1040, .04), ltc(3040, 2.04)])
        self.assertEqual(rows[1]["weightMs"], 0)
        self.assertEqual(summary["input"]["longGapCount"], 1)
        self.assertEqual(summary["all"]["signed"]["thresholds"]["20"]["durationMs"], 0)

    def test_recovery_requires_half_second_and_reports_interval_start(self):
        events = [frame(999, 0), ltc(1000, 3)]
        for i in range(1, 20):
            # Good images begin at t=1.08, stay good beyond t=1.60.
            events.extend([frame(1000 + i * 40 - 1, math.floor((3 + i * .04) * 24) if i >= 2 else 0),
                           ltc(1000 + i * 40, 3 + i * .04)])
        summary, _ = self.analyze(events, phase("seek-a", 3, 5))
        self.assertEqual(summary["recovery"][0]["within80Ms"]["recoveryMs"], 80)

    def test_single_good_image_followed_by_bad_is_not_recovered(self):
        events = [frame(999, 72), ltc(1000, 3)]
        events += [ltc(1000 + i * 40, 3 + i * .04) for i in range(1, 25)]
        summary, _ = self.analyze(events, phase("seek-a", 3, 5))
        self.assertIsNone(summary["recovery"][0]["within80Ms"]["recoveryMs"])
        self.assertIsNone(summary["recovery"][0]["within250Ms"]["recoveryMs"])

    def test_recovery_does_not_bridge_input_outage(self):
        summary, _ = self.analyze([frame(999, 72), ltc(1000, 3), frame(1999, 96), ltc(2000, 4)],
                                  phase("seek-a", 3, 5))
        self.assertIsNone(summary["recovery"][0]["within80Ms"]["recoveryMs"])

    def test_gap_black_latency_uses_actual_pixels_not_kind(self):
        events = [frame(999), ltc(1000, 0), frame(10999, 239), ltc(11000, 10),
                  frame(11010, 239, kind="black"), frame(11025, black=True, valid=False), ltc(11040, 10.04)]
        summary, rows = self.analyze(events)
        self.assertEqual(summary["gaps"][0]["latencyMs"], 25)
        self.assertFalse(summary["gaps"][0]["leftCensored"])
        self.assertEqual(rows[-1]["status"], "gap-correct")

    def test_gap_already_black_is_left_censored(self):
        summary, _ = self.analyze([frame(999), ltc(1000, 0), frame(10999, black=True, valid=False), ltc(11000, 10)])
        self.assertEqual(summary["gaps"][0]["latencyMs"], 0)
        self.assertTrue(summary["gaps"][0]["leftCensored"])

    def test_freeze_requires_previous_clips_last_frame(self):
        events = [frame(999), ltc(1000, 0), frame(10999, 100), ltc(11000, 10),
                  frame(11020, 238, kind="frozen"), ltc(11040, 10.04)]
        summary, rows = self.analyze(events, phase("freeze-sweep", mode="freeze"))
        self.assertEqual(rows[-2]["status"], "gap-wrong")
        self.assertEqual(summary["gaps"][0]["latencyMs"], 20)

    def test_complete_measurement_and_bad_accuracy_are_distinct(self):
        events, journal = complete_run()
        for event in events:
            if event["type"] == "frame" and event["markerValid"]:
                event["frameIndex"] = 0  # Intentionally terrible accuracy, valid complete observations.
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertTrue(summary["complete"], summary["incompleteReasons"])
        self.assertGreater(summary["all"]["signed"]["maxAbsMs"], 9000)

    def test_missing_end_dropped_and_short_input_mark_incomplete(self):
        events, journal = complete_run()
        for modified, code in [(events[:-1], "trace-end-missing"),
                               (events[:-1] + [{**events[-1], "dropped": 1}], "trace-dropped-events")]:
            summary, _ = self.analyzer.analyze(modified, FIXTURE, journal)
            self.assertFalse(summary["complete"])
            self.assertIn(code, summary["incompleteReasons"])
        events = [e for e in events if e["type"] != "ltc" or e["ticks"] < 1100]
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertFalse(summary["complete"])
        self.assertTrue(any("input-coverage" in r for r in summary["incompleteReasons"]))

    def test_cli_writes_all_artifacts_and_exit_status(self):
        events, journal = complete_run()
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            (folder / "trace.jsonl").write_text("\n".join(json.dumps(e) for e in events), encoding="utf-8")
            (folder / "phases.jsonl").write_text("\n".join(json.dumps(e) for e in journal), encoding="utf-8")
            (folder / "fixture.json").write_text(json.dumps(FIXTURE), encoding="utf-8")
            args = [sys.executable, str(SCRIPT), "--trace", str(folder / "trace.jsonl"),
                    "--fixture", str(folder / "fixture.json"), "--phases", str(folder / "phases.jsonl"),
                    "--output", str(folder / "output")]
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertGreater((folder / "output/accuracy-samples.csv").stat().st_size, 1000)
            self.assertIn("40 ms", (folder / "output/accuracy-report.md").read_text(encoding="utf-8"))
            (folder / "trace.jsonl").write_text('{"broken":', encoding="utf-8")
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            summary = json.loads((folder / "output/accuracy-summary.json").read_text(encoding="utf-8"))
            self.assertFalse(summary["complete"])

    def test_end_count_detects_missing_records_despite_clean_footer(self):
        events, journal = complete_run()
        events.pop(10)
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertIn("trace-event-count-mismatch", summary["incompleteReasons"])

    def test_render_stage_counts_toward_footer_without_changing_accuracy(self):
        original, journal = complete_run()
        baseline, baseline_rows = self.analyzer.analyze(original, FIXTURE, journal)
        diagnostic = render_stage(1000)
        # Pixel-looking fields on a diagnostic must not replace the bitmap.
        diagnostic.update(clipId=3, frameIndex=700, markerValid=True, isBlack=False)
        events = [{**original[0], "renderStageSchema": 1}, *original[1:-1], diagnostic,
                  {**original[-1], "events": original[-1]["events"] + 1}]
        summary, rows = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertTrue(summary["complete"], summary["incompleteReasons"])
        self.assertEqual(rows, baseline_rows)
        self.assertEqual(summary["all"], baseline["all"])
        self.assertEqual(summary["trace"]["probeOverhead"], baseline["trace"]["probeOverhead"])
        self.assertEqual(summary["trace"]["renderStageEvents"], 1)
        self.assertEqual(summary["trace"]["frameEvents"], baseline["trace"]["frameEvents"])

    def test_render_stage_does_not_hide_a_missing_frame_from_footer(self):
        events, journal = complete_run()
        events[0]["renderStageSchema"] = 1
        events.insert(-1, render_stage(1000))
        events[-1]["events"] += 1
        missing = next(index for index, event in enumerate(events) if event["type"] == "frame")
        events.pop(missing)
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertFalse(summary["complete"])
        self.assertIn("trace-event-count-mismatch", summary["incompleteReasons"])

    def test_render_stage_cannot_supply_missing_frame_coverage(self):
        events = trace([render_stage(999), ltc(1000, 0), render_stage(1039), ltc(1040, .04)])
        events[0]["renderStageSchema"] = 1
        summary, rows = self.analyzer.analyze(events, FIXTURE, phase())
        self.assertNotIn("trace-event-count-mismatch", summary["incompleteReasons"])
        self.assertIn("frame-coverage:trace", summary["incompleteReasons"])
        self.assertEqual([row["status"] for row in rows], ["unpublished", "unpublished"])
        self.assertEqual(summary["trace"]["frameEvents"], 0)

    def test_render_stage_after_end_is_incomplete(self):
        events, journal = complete_run()
        events[0]["renderStageSchema"] = 1
        diagnostic = render_stage(events[-1]["ticks"] + 10)
        events[-1]["events"] += 1
        events.append(diagnostic)
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertIn("trace-events-after-end", summary["incompleteReasons"])

    def test_render_stage_requires_supported_declared_schema_and_valid_envelope(self):
        for schema in (None, 2, True):
            with self.subTest(schema=schema):
                events = trace([frame(999), render_stage(1000), ltc(1040, .04)])
                if schema is not None:
                    events[0]["renderStageSchema"] = schema
                summary, _ = self.analyzer.analyze(events, FIXTURE, phase())
                self.assertIn("unsupported-render-stage-schema", summary["incompleteReasons"])
        events = trace([frame(999), {**render_stage(1000), "endTicks": 999}, ltc(1040, .04)])
        events[0]["renderStageSchema"] = 1
        summary, _ = self.analyzer.analyze(events, FIXTURE, phase())
        self.assertIn("invalid-render-stage-event", summary["incompleteReasons"])

    def test_unknown_event_type_is_not_silently_allowed_by_footer(self):
        events, journal = complete_run()
        # Even an apparently clean old footer cannot authorize an unknown type.
        events.insert(-1, {"type": "future-observation", "ticks": 1000})
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertFalse(summary["complete"])
        self.assertIn("unknown-trace-event-type", summary["incompleteReasons"])

    def test_unknown_only_clip_has_no_measurable_steady_coverage(self):
        events, journal = complete_run()
        for event in events:
            if event["type"] == "frame" and event["clipId"] == 2:
                event.update(markerValid=False, clipId=None, frameIndex=None)
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertFalse(summary["complete"])
        self.assertIn("measurable-steady-coverage:black-sweep:clip2", summary["incompleteReasons"])
        self.assertGreater(summary["all"]["statusCounts"]["unknown"], 0)

    def test_phase_breakdown_and_probe_overhead_are_explicit(self):
        summary, _ = self.analyze([frame(999), ltc(1000, 0),
                                  {**frame(1999, 24), "probeTicks": 3}, ltc(2000, 1)])
        self.assertEqual(summary["trace"]["probeOverhead"]["meanMs"], 2)
        self.assertEqual(summary["trace"]["probeOverhead"]["p95Ms"], 3)
        self.assertEqual(summary["phases"][0]["byClip"]["1"]["steady"]["measuredSamples"], 1)

    def test_frame_after_same_tick_ltc_is_not_left_censored(self):
        events = [frame(999), ltc(1000, 0), frame(10999, 239), ltc(11000, 10),
                  frame(11000, black=True, valid=False), ltc(11040, 10.04)]
        summary, _ = self.analyze(events)
        self.assertEqual(summary["gaps"][0]["latencyMs"], 0)
        self.assertFalse(summary["gaps"][0]["leftCensored"])

    def test_republication_does_not_hide_unchanged_image_hold(self):
        summary, rows = self.analyze([frame(999), ltc(1000, 0), frame(1399), ltc(1400, .4)])
        self.assertEqual(rows[-1]["frameAgeMs"], 1)
        self.assertEqual(summary["trace"]["maxActiveImageHoldMs"], 401)

    def test_rational_fps_and_clip_transition_settling(self):
        events = [frame(999), ltc(1000, 0), frame(12999, 0, clip=2), ltc(13000, 12),
                  frame(13959, 28, clip=2), ltc(13960, 12.96), frame(13999, 30, clip=2), ltc(14000, 13)]
        summary, rows = self.analyze(events)
        self.assertEqual([r["steady"] for r in rows], [False, False, False, True])
        # 30 * 1001 / 30000 = 1.001 s, exactly +1 ms from target 1 s.
        self.assertAlmostEqual(rows[-1]["signedErrorMs"], 1)
        self.assertEqual(summary["byFps"]["30000/1001"]["steady"]["samples"], 1)

    def test_nearest_rank_percentiles_and_observed_threshold_duration(self):
        summary, _ = self.analyze([frame(999), ltc(1000, 0), ltc(1040, .04), ltc(1080, .08)])
        measured = summary["all"]["signed"]
        self.assertEqual(measured["p95AbsMs"], 80)
        self.assertEqual(measured["p99AbsMs"], 80)
        self.assertEqual(measured["thresholds"]["20"], {"count": 2, "durationMs": 40})
        self.assertEqual(measured["thresholds"]["40"], {"count": 1, "durationMs": 0})
        self.assertEqual(measured["timeWeightedMeanAbsMs"], 20)

    def test_footer_errors_and_phase_clock_mismatch_cannot_succeed(self):
        events, journal = complete_run()
        events[-1]["errors"] = 1
        journal[0]["frequency"] = 1000000
        summary, _ = self.analyzer.analyze(events, FIXTURE, journal)
        self.assertFalse(summary["complete"])
        self.assertIn("trace-write-errors", summary["incompleteReasons"])
        self.assertIn("invalid-phase-start", summary["incompleteReasons"])


if __name__ == "__main__":
    unittest.main()
