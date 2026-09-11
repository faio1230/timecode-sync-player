"""Synthetic run-directory tests for the keyed-mutex hold summary; no GPU or process launch."""
import json
from pathlib import Path
import tempfile
import unittest

from analyze_mutex_holds import analyze_run


class MutexHoldAnalysisTests(unittest.TestCase):
    # Window [5,27) at 60000 Hz from origin 1000000: qpc [1300000, 2620000).
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="gpu-probe-mutex-holds-test-")
        self.run = Path(self.temp.name) / "run"
        self.app = self.run / "app"
        self.app.mkdir(parents=True)
        self.manifest = {"schemaVersion": 1, "originQpc": 1000000, "qpcFrequency": 60000,
                         "options": {"seconds": 32, "warmup": 5, "fps": 60, "output": "both", "mode": "split"}}
        self.summary = {"outcome": "completed", "validPerformanceResult": True, "droppedEvents": 0,
                        "metrics": {"send.publish": {"uniqueImageCount": 7, "intervalMs": {"max": 17.5},
                                                     "imageAgeMs": {"mean": 3, "p99": 5, "max": 6}}}}
        self.events = []

    def tearDown(self):
        self.temp.cleanup()

    def add(self, stage, qpc, worker="Spout", scheduled=1360000, image_id=5, **fields):
        self.events.append({"stage": stage, "worker": worker, "qpc": qpc, "scheduledQpc": scheduled,
                            "imageId": image_id, "generatedQpc": 0, "detail": None, "value": 0, "deadlineQpc": 0, **fields})

    def run_analysis(self):
        (self.app / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.app / "summary.json").write_text(json.dumps(self.summary), encoding="utf-8")
        (self.app / "events.jsonl").write_text("\n".join(json.dumps(event) for event in self.events)+"\n", encoding="utf-8")
        return analyze_run(self.run)

    def add_display_hold(self, first, last, image_id=5, scheduled=1360000):
        self.add("display.mutex.acquire", first, "GPU", scheduled, image_id, detail="1")
        self.add("display.mutex.release", last, "GPU", scheduled, image_id, detail="1")

    def test_old_log_without_mutex_events_has_null_holds_and_all_busy_unexplained(self):
        self.add("skip", 1360050, detail="copy.keyedMutexBusy", value=1)
        self.add("skip", 1400050, scheduled=1400000, image_id=8, detail="copy.keyedMutexBusy", value=1)
        result = self.run_analysis()
        self.assertIsNone(result["display_hold_ms"])
        self.assertIsNone(result["copy_hold_ms"])
        self.assertIsNone(result["display_wait_native_ms"])
        self.assertEqual(result["copy_busy_first"], 2)
        self.assertEqual(result["unexplained_busy"], 2)
        self.assertEqual((result["copy_retry_events"], result["copy_retry_acquire_success"], result["copy_retry_failed"]), (0, 0, 0))
        self.assertEqual(result["send_publish"]["uniqueImageCount"], 7)
        self.assertEqual(result["windowSeconds"], {"start": 5, "end": 27})

    def test_busy_inside_same_image_display_hold_is_explained(self):
        self.add_display_hold(1360000, 1360120)
        self.add("skip", 1360050, detail="copy.keyedMutexBusy", value=1)
        result = self.run_analysis()
        self.assertEqual(result["copy_busy_first"], 1)
        self.assertEqual(result["unexplained_busy"], 0)
        self.assertEqual(result["display_hold_ms"], {"mean": 2, "max": 2, "p99": 2})

    def test_busy_outside_any_hold_or_for_another_image_is_unexplained(self):
        self.add_display_hold(1360000, 1360120)
        self.add("skip", 1360200, detail="copy.keyedMutexBusy", value=1)             # after release
        self.add("skip", 1360050, image_id=6, detail="copy.keyedMutexBusy", value=1)  # other image
        result = self.run_analysis()
        self.assertEqual(result["copy_busy_first"], 2)
        self.assertEqual(result["unexplained_busy"], 2)

    def test_copy_retry_followed_by_retry_acquire_of_newer_image(self):
        self.add("skip", 1360050, detail="copy.keyedMutexBusy", value=1)
        self.add("copy.retry", 1360060, value=4, deadlineQpc=1361000)
        self.add("copy.mutex.acquire", 1360100, image_id=6, detail="retry:1")
        self.add("copy.mutex.release", 1360130, image_id=6, detail="retry:1")
        result = self.run_analysis()
        self.assertEqual(result["copy_retry_events"], 1)
        self.assertEqual(result["copy_retry_acquire_success"], 1)
        self.assertEqual(result["copy_retry_failed"], 0)
        self.assertEqual(result["retry_acquire_newer_id"], 1)
        self.assertEqual(result["copy_busy_retry"], 0)
        self.assertEqual(result["copy_hold_ms"]["max"], 0.5)

    def test_copy_retry_followed_by_second_busy_is_failed(self):
        self.add("skip", 1360050, detail="copy.keyedMutexBusy", value=1)
        self.add("copy.retry", 1360060, value=4, deadlineQpc=1361000)
        self.add("skip", 1360110, detail="copy.keyedMutexBusy.retry", value=1)
        result = self.run_analysis()
        self.assertEqual(result["copy_retry_events"], 1)
        self.assertEqual(result["copy_retry_acquire_success"], 0)
        self.assertEqual(result["copy_retry_failed"], 1)
        self.assertEqual(result["copy_busy_retry"], 1)
        self.assertEqual(result["retry_acquire_newer_id"], 0)

    def test_retry_acquire_outside_window_does_not_raise(self):
        # Retry signalled just inside the window, acquired after it: not a success, never an exception.
        self.add("copy.retry", 2619990, scheduled=2619000, value=1, deadlineQpc=2620000)
        self.add("copy.mutex.acquire", 2620005, scheduled=2619000, image_id=6, detail="retry:1")
        self.add("copy.mutex.release", 2620010, scheduled=2619000, image_id=6, detail="retry:1")
        self.add("copy.retry", 1290000, scheduled=1289000, value=1, deadlineQpc=1290500)  # before window
        result = self.run_analysis()
        self.assertEqual(result["copy_retry_events"], 1)
        self.assertEqual(result["copy_retry_acquire_success"], 0)
        self.assertEqual(result["copy_retry_failed"], 1)

    def test_native_display_wait_pairs_produce_duration_stats(self):
        self.add("display.wait.start", 1360000, "GPU", 1360000, 0, detail="native", value=15)
        self.add("display.wait.end", 1360180, "GPU", 1360000, 0, detail="ready", value=1)
        self.add("display.wait.start", 1361000, "GPU", 1361000, 0, detail="native", value=15)
        self.add("display.wait.end", 1361060, "GPU", 1361000, 0, detail="ready", value=1)
        self.add("display.wait.start", 1362000, "GPU", 1362000, 0, detail="retained", value=0)
        self.add("display.wait.end", 1362600, "GPU", 1362000, 0, detail="retained", value=1)
        result = self.run_analysis()
        self.assertEqual(result["display_wait_native_ms"], {"mean": 2, "max": 3})

    def test_fence_log_yields_null_holds_and_zero_busy(self):
        self.manifest["options"].update(sourceSync="fence", copyRetry="off")
        self.add("copy.fence.wait", 1360010, detail="first:1", value=5)
        self.add("copy.start", 1360012)
        self.add("copy.complete", 1360040)
        self.add("display.fence.wait", 1360005, "GPU", detail="1", value=5)
        self.add("display.draw.start", 1360006, "GPU")
        self.add("display.draw.complete", 1360030, "GPU")
        result = self.run_analysis()
        self.assertEqual(result["sourceSync"], "fence")
        self.assertIsNone(result["display_hold_ms"])
        self.assertIsNone(result["copy_hold_ms"])
        self.assertEqual((result["copy_busy_first"], result["copy_busy_retry"], result["unexplained_busy"]), (0, 0, 0))
        self.assertEqual((result["copy_retry_events"], result["copy_retry_acquire_success"], result["copy_retry_failed"]), (0, 0, 0))



if __name__ == "__main__":
    unittest.main()
