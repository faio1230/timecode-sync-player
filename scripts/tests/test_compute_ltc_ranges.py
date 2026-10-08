"""compute-ltc-ranges.py の自己試験（標準ライブラリだけ）。

実行: python scripts/tests/test_compute_ltc_ranges.py
"""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "compute-ltc-ranges.py"
_spec = importlib.util.spec_from_file_location("compute_ltc_ranges", SCRIPT)
R = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(R)


CHECK_TEXT = """##### std1
  result: 24/0/0/0
  failures: []
  reloc: 35+15=50
  hold: 87
  back3: 31
  backParts: (12, 19, 0)
  bound: 2
  stale: 0
  pump: 86
  assert_: 6
  l2def: 0.0
  l2gpu: 0.0
  prores: {'gpu': 0, 'cpu': 0, 'adapterMismatch': 0}
  composed: (60, 127, 0, 45)
  OUT-OF-RANGE: none
    C-1 {'relocC': 20, 'holdEntries': 10, '_hs': 1, 'pump': 2}
##### prores1
  result: 23/1/0/0
  reloc: 33+15=48
  hold: 65
  l2def: None
  prores: {'gpu': 129, 'cpu': 0, 'adapterMismatch': 0}
"""


class ComputeTests(unittest.TestCase):
    def test_three_runs_min_max_is_wider(self):
        row = R.compute([0, 1, 30], R.SQRT)
        self.assertTrue(row["enough"])
        self.assertEqual(row["median"], 1)
        self.assertEqual(row["band"], (0, 3))  # 1 - 2 = -1 は 0 に、1 + 2 = 3
        self.assertEqual(row["range"], (0, 30))
        self.assertEqual(row["source"], "最小〜最大")

    def test_three_runs_sqrt_band_is_wider(self):
        row = R.compute([50, 51, 52], R.SQRT)
        # 2√51 = 14.28 → 36.72 を切り捨てて 36、65.28 を切り上げて 66
        self.assertEqual(row["band"], (36, 66))
        self.assertEqual(row["range"], (36, 66))
        self.assertEqual(row["source"], "±2√中央")

    def test_not_nested_takes_both_outer_sides(self):
        row = R.compute([10, 11, 40], R.SQRT)
        # 中央 11、2√11 = 6.63 → 4〜18。最小〜最大は 10〜40
        self.assertEqual(row["band"], (4, 18))
        self.assertEqual(row["range"], (4, 40))
        self.assertFalse(row["nested"])
        self.assertEqual(row["source"], "両方の外側（入れ子でない）")

    def test_two_runs_are_not_enough(self):
        row = R.compute([50, 52], R.SQRT)
        self.assertFalse(row["enough"])
        self.assertIsNone(row["range"])
        self.assertEqual(row["band"], (36, 66))  # 参考には出す
        self.assertIsNone(R.compute([88, 89], R.FIXED)["range"])

    def test_fixed_metric_uses_min_max_plus_minus_one(self):
        # 筋書きの数: √ を使わず、最小〜最大 ±1
        row = R.compute([86, 87, 88], R.FIXED)
        self.assertIsNone(row["band"])
        self.assertEqual(row["range"], (85, 89))
        self.assertEqual(row["source"], "最小〜最大 ±1")
        self.assertEqual(R.compute([0, 0, 0], R.FIXED)["range"], (0, 1))  # 下端は 0 未満にしない

    def test_same_values_differ_by_class(self):
        vals = [87, 87, 87]
        self.assertEqual(R.compute(vals, R.FIXED)["range"], (86, 88))
        self.assertEqual(R.compute(vals, R.SQRT)["range"], (68, 106))  # 2√87 = 18.65

    def test_record_and_stop1(self):
        row = R.compute([4, 5, 6], R.RECORD)
        self.assertEqual(row["range"], (0, 10))  # 揺れと同じ計算、止めない
        row = R.compute([0, 12], R.STOP1)
        self.assertTrue(row["enough"])
        self.assertEqual(row["range"], (0, 0))
        self.assertEqual(row["source"], "1 でも止める")

    def test_decimal_metric_uses_min_max_only(self):
        row = R.compute([0.003, 0.001, 0.002], R.DECIMAL)
        self.assertIsNone(row["band"])
        self.assertEqual(row["range"], (0.001, 0.003))
        self.assertFalse(R.compute([0.003, 0.001], R.DECIMAL)["enough"])

    def test_classify(self):
        self.assertEqual(R.classify("std", "reloc", [50]), R.SQRT)
        self.assertEqual(R.classify("l1", "reloc", [3]), R.FIXED)  # 種類:項目 が先に効く
        self.assertEqual(R.classify("std", "hold", [87]), R.FIXED)
        self.assertEqual(R.classify("std", "back3", [31]), R.FIXED)
        self.assertEqual(R.classify("std", "bound", [2]), R.FIXED)
        self.assertEqual(R.classify("std", "pump", [85]), R.SQRT)
        self.assertEqual(R.classify("std", "proresCpu", [0]), R.SQRT)
        self.assertEqual(R.classify("std", "assert", [5]), R.RECORD)
        self.assertEqual(R.classify("std", "refStale", [0]), R.STOP1)
        self.assertEqual(R.classify("std", "recap", [0]), R.STOP1)
        self.assertEqual(R.classify("std", "l2def", [0.0]), R.DECIMAL)
        self.assertEqual(R.classify("std", "other", [1, 0.5]), R.DECIMAL)
        self.assertEqual(R.classify("std", "other", [1]), R.FIXED)
        self.assertEqual(R.classify("l1", "reloc", [3], {"reloc": R.SQRT}), R.SQRT)  # 上書きの表

    def test_zero_median(self):
        row = R.compute([0, 0, 0], R.SQRT)
        self.assertEqual(row["range"], (0, 0))


class ParseTests(unittest.TestCase):
    def test_check_text(self):
        runs = R.parse_text(CHECK_TEXT, "check-x")
        self.assertEqual([r.name for r in runs], ["check-x#std1", "check-x#prores1"])
        m = runs[0].metrics
        self.assertEqual(m["reloc"], 50)
        self.assertEqual(m["hold"], 87)
        self.assertEqual(m["assert"], 6)
        self.assertEqual(m["proresGpu"], 0)
        self.assertEqual(m["l2def"], 0.0)
        self.assertNotIn("backParts", m)
        self.assertEqual(runs[0].failed, 0)
        self.assertEqual(runs[1].failed, 1)
        self.assertNotIn("l2def", runs[1].metrics)  # None は読まない

    def test_run_result_json(self):
        obj = {"schema": "ltc-run-result/1", "label": "std-pass-b-rtx-20261008", "failed": 0,
               "prores": {"gpu": 76, "cpu": 0}, "l2": [{"maxFrameDeficitSeconds": 0.003}],
               "crashes": {"count": 0}, "metrics": {"reloc": 53, "hold": 88}}
        runs = R.parse_text(json.dumps(obj), "run-result")
        self.assertEqual(len(runs), 1)
        self.assertEqual(runs[0].label, "std-pass-b-rtx-20261008")
        self.assertEqual(runs[0].metrics, {"proresGpu": 76, "proresCpu": 0, "l2def": 0.003, "crashes": 0,
                                           "reloc": 53, "hold": 88})

    def test_hand_json_kind_wins(self):
        obj = {"runs": [{"source": "v066", "label": "std a", "kind": "std-a", "metrics": {"reloc": 67}}]}
        runs = R.parse_text(json.dumps(obj), "f")
        R.assign_kinds(runs, [("*", "other")])
        self.assertEqual(runs[0].kind, "std-a")
        self.assertEqual(runs[0].name, "v066#std a")

    def test_failed_runs_and_excludes(self):
        runs = R.parse_text(CHECK_TEXT, "check-x")
        R.assign_kinds(runs, R.parse_pairs("std*=std,prores*=prores"))
        used, dropped = R.select_runs(runs, [], include_failed=False)
        self.assertEqual([r.label for r in used], ["std1"])
        self.assertIn("失敗 1 件", dropped[0][1])
        used, dropped = R.select_runs(runs, [("check-x#std*", "試し")], include_failed=True)
        self.assertEqual([r.label for r in used], ["prores1"])
        self.assertEqual(dropped[0][1], "試し")


class CliTests(unittest.TestCase):
    def test_markdown_end_to_end(self):
        with tempfile.TemporaryDirectory() as d:
            paths = []
            for i, (reloc, hold) in enumerate([(50, 87), (51, 88), (52, 89)]):
                p = Path(d) / f"check-{i}.txt"
                p.write_text(f"##### std{i}\n  result: 24/0/0/0\n  reloc: 35+{reloc - 35}={reloc}\n"
                             f"  hold: {hold}\n  l2gpu: 0.00{i}\n", encoding="utf-8")
                paths.append(str(p))
            p = Path(d) / "check-h.txt"
            p.write_text("##### heavy-a\n  result: 24/0/0/0\n  reloc: 34+15=49\n", encoding="utf-8")
            paths.append(str(p))
            out = Path(d) / "out.md"
            dump = Path(d) / "runs.json"
            subprocess.run([sys.executable, str(SCRIPT), *paths, "--kinds", "std*=std,heavy-a=heavy",
                            "--exclude", "check-2#*=試しに除く", "--include-failed", "--title", "試し",
                            "--out", str(out), "--dump-runs", str(dump)], check=True)
            md = out.read_text(encoding="utf-8")
            self.assertIn("### 試し", md)
            # std は 2 回（check-2 を除いた）なので回が足りない
            self.assertTrue(md.splitlines()[2].startswith(R.RULE_LINE))  # 頭（表題の次）に読み方の 1 行
            self.assertIn("| std | relocate | 揺れ | 2 | 50・51 | 50〜51 | 50.5 | 36〜65 | 回が足りない（2 回、範囲に使わない） | — |", md)
            self.assertIn("| heavy | relocate | 揺れ | 1 | 49 |", md)
            self.assertIn("- check-2#std2: 試しに除く", md)
            self.assertEqual(len(json.loads(dump.read_text(encoding="utf-8"))["runs"]), 4)

            out2 = Path(d) / "out2.md"
            subprocess.run([sys.executable, str(SCRIPT), *paths[:3], "--kinds", "std*=std", "--out", str(out2)],
                           check=True)
            md2 = out2.read_text(encoding="utf-8")
            self.assertIn("| std | relocate | 揺れ | 3 | 50・51・52 | 50〜52 | 51 | 36〜66 | 36〜66 | ±2√中央 |", md2)
            self.assertIn("| std | holdEntries | 筋書き | 3 | 87・88・89 | 87〜89 | 88 | — | 86〜90 | 最小〜最大 ±1 |", md2)
            self.assertIn("| std | L-2 maxGpuDeficit（秒） | 小数 | 3 | 0.000・0.001・0.002 | 0.000〜0.002 | 0.001 | — | 0.000〜0.002 | 最小〜最大（小数） |", md2)

            # --class で上書き: holdEntries を揺れに
            out3 = Path(d) / "out3.md"
            subprocess.run([sys.executable, str(SCRIPT), *paths[:3], "--kinds", "std*=std", "--class", "std:hold=sqrt",
                            "--out", str(out3)], check=True)
            self.assertIn("| std | holdEntries | 揺れ | 3 | 87・88・89 | 87〜89 | 88 | 69〜107 | 69〜107 | ±2√中央 |",
                          out3.read_text(encoding="utf-8"))
            bad = subprocess.run([sys.executable, str(SCRIPT), *paths[:3], "--class", "hold=wide"],
                                 capture_output=True)
            self.assertNotEqual(bad.returncode, 0)


if __name__ == "__main__":
    unittest.main()
