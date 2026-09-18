#!/usr/bin/env python3
"""M6/M6-b: アプリログの peak/rms からレベル別の crest factor（peak/rms）を出す。

レベルは dirty-plan.json の並び。各レベルは `seconds + settlingSeconds + gapSeconds` 秒ずつ
順に再生されるので、run 開始（レポートディレクトリ名）以降の `LTC audio stats` 行を
先頭サンプルからの経過時間でレベルに割り当てる。
レベル内の中央値から 10% を超えて外れる窓（切替端・欠落を含む窓）は中央値の計算から除き、
そのレベルの最大 crest（外れ窓を含む）を併記する。

使い方: python scripts/extract-capture-crest.py <report-dir> [<report-dir> ...]
"""
import json
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

LINE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+.*peak=([\d.]+) rms=([\d.]+)")
THRESHOLDS = (1.3, 1.5, 1.7)


def run_start(report):
    parts = report.name.split("-")
    return datetime.strptime(parts[-2] + parts[-1], "%Y%m%d%H%M%S")


def read_samples(report, start):
    log = next((report / "app-logs").glob("timecodesyncplayer-*.log"), None)
    if log is None:
        return []
    samples = []
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        if "LTC audio stats" not in line:
            continue
        match = LINE.match(line)
        if not match:
            continue
        timestamp = datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S")
        if timestamp < start:
            continue
        peak, rms = float(match.group(2)), float(match.group(3))
        if peak > 0 and rms > 0:
            samples.append((timestamp, peak, rms))
    return samples


def median(values):
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def main():
    for argument in sys.argv[1:]:
        report = Path(argument)
        plan = json.loads((report / "dirty-plan.json").read_text(encoding="utf-8-sig"))
        settling = float(plan.get("settlingSeconds", 2.0))
        gap = float(plan.get("gapSeconds", 0.5))
        samples = read_samples(report, run_start(report))
        if not samples:
            print(f"## {report.name}: ログに LTC audio stats がありません")
            continue

        print(f"## {report.name}")
        print("| レベル | 秒 | n | peak 中央値 | rms 中央値 | crest 中央値 | crest 範囲（外れ窓含む） |")
        print("| --- | ---: | ---: | ---: | ---: | ---: | --- |")
        t0 = samples[0][0]
        for index, level in enumerate(plan["levels"]):
            play = float(level["seconds"]) + settling + gap
            begin = t0 + timedelta(seconds=index * play)
            end = begin + timedelta(seconds=play)
            in_level = [item for item in samples if begin <= item[0] < end]
            if not in_level:
                print(f"| {level['name']} | {level['seconds']:.0f} | 0 | - | - | - | - |")
                continue
            rmses = [item[2] for item in in_level]
            center = median(rmses)
            core = [item for item in in_level if abs(item[2] - center) / center <= 0.10]
            if not core:
                core = in_level
            peaks = [item[1] for item in core]
            cores = [item[1] / item[2] for item in core]
            all_crests = [item[1] / item[2] for item in in_level]
            print(f"| {level['name']} | {level['seconds']:.0f} | {len(in_level)} | {median(peaks):.4f} | "
                  f"{median([item[2] for item in core]):.4f} | {median(cores):.3f} | "
                  f"{min(all_crests):.3f}–{max(all_crests):.3f} |")

        all_crests = [peak / rms for _, peak, rms in samples]
        over = " / ".join(f"crest>{t} {sum(1 for c in all_crests if c > t)}件" for t in THRESHOLDS)
        print(f"全 {len(all_crests)} 窓: crest 最大 {max(all_crests):.3f}、{over}（切替端の窓を含む）")
        print("")


if __name__ == "__main__":
    main()
