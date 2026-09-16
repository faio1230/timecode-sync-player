"""Hand-calculated cases for the T2 LTC sample-clock analyzer (stdlib only)."""
import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "analyze-t2-ltc-clock.py"
FREQUENCY = 10_000_000  # 10MHz。1ms = 10000 tick


def ltc(ticks_ms, sample_ticks_ms, callback_ticks_ms, spread_ms):
    return {
        "type": "ltc",
        "ticks": int(ticks_ms * FREQUENCY / 1000),
        "seconds": 0.0,
        "fps": 25,
        "sampleTicks": int(sample_ticks_ms * FREQUENCY / 1000),
        "callbackTicks": int(callback_ticks_ms * FREQUENCY / 1000),
        "anchorSpreadMs": spread_ms,
    }


class T2LtcClockTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("t2_ltc_clock", SCRIPT)
        self.analyzer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.analyzer)

    def analyze(self, events):
        return self.analyzer.analyze([{"type": "meta", "frequency": FREQUENCY}, *events])

    def test_receipt_delay_and_both_interval_series(self):
        result = self.analyze([
            ltc(1000.0, 980.0, 1000.0, 20.0),
            ltc(1000.0, 1020.0, 1000.0, 20.0),
            ltc(1050.0, 1060.0, 1050.0, 25.0),
        ])
        self.assertEqual(result["clockedEvents"], 3)
        self.assertEqual(result["receiptDelay"]["n"], 3)
        self.assertAlmostEqual(result["receiptDelay"]["median"], -10.0)
        self.assertAlmostEqual(result["receiptDelay"]["min"], -20.0)
        self.assertAlmostEqual(result["receiptDelay"]["max"], 20.0)
        self.assertEqual(result["receiptDelayNegative"], 2)
        # sampleTicks は 40ms 間隔（25fps）
        self.assertEqual(result["sampleIntervals"]["n"], 2)
        self.assertAlmostEqual(result["sampleIntervals"]["median"], 40.0)
        self.assertAlmostEqual(result["sampleIntervals"]["min"], 40.0)
        # ticks は 0ms と 50ms（音声コールバックの量子化）
        self.assertEqual(result["receiptIntervals"]["n"], 2)
        self.assertAlmostEqual(result["receiptIntervals"]["median"], 25.0)
        self.assertAlmostEqual(result["receiptIntervals"]["min"], 0.0)
        self.assertAlmostEqual(result["receiptIntervals"]["max"], 50.0)

    def test_anchor_spread_is_deduped_per_callback(self):
        result = self.analyze([
            ltc(1000.0, 980.0, 1000.0, 20.0),
            ltc(1000.0, 1020.0, 1000.0, 20.0),
            ltc(1050.0, 1060.0, 1050.0, 25.0),
        ])
        self.assertEqual(result["anchorSpreadMs"]["n"], 2)
        self.assertAlmostEqual(result["anchorSpreadMs"]["median"], 22.5)

    def test_old_trace_without_sample_clock_is_reported_as_no_samples(self):
        result = self.analyzer.analyze([
            {"type": "meta", "frequency": FREQUENCY},
            {"type": "ltc", "ticks": 1000, "seconds": 0.0, "fps": 25},
        ])
        self.assertEqual(result["clockedEvents"], 0)
        self.assertIsNone(result["sampleIntervals"])
        self.assertIsNone(result["anchorSpreadMs"])


if __name__ == "__main__":
    unittest.main()
