#!/usr/bin/env python3
"""M4: 素材 fps と表示周期の食い違いによる重複・欠落の分布。

trace.jsonl の frame イベント（公開ごとに marker から読んだ素材フレーム番号）を使い、素材ごとに
  - 同じ素材フレームが連続して公開された回数（重複。24fps→60Hz なら 2 と 3 の混合）
  - 公開されなかった素材フレーム番号（小欠落。連番の飛び 2〜4 = 数フレームの落ち）
  - 意図的な位置飛び（大飛び >= 5 = シーク/不連続）と逆行
  - 長い保持（> 10 発表 = freeze/停止。分布からは別枠）
を集計する。境界は発表（bitmap-publication）で、物理走査（present.scanout）ではない。

使い方:
  python scripts/analyze-frame-quantization.py --run DIR [--run DIR ...]

--gap-seconds 以上に公開が空いたら区間を分ける（既定 0.5s。フェーズ境界やシークを挟む）。
シークを含む区間は「大飛び」として数え、定常区間（大飛び・逆行なし）の集計と分けて出す。
"""
import argparse
import json
import statistics
from collections import Counter
from pathlib import Path

LONG_HOLD = 10          # これより長い連続発表は freeze/停止として別枠
LARGE_JUMP = 5          # 連番の飛びがこれ以上ならシーク/不連続とみなす


def load_events(path):
    events = []
    with Path(path).open("r", encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if line:
                events.append(json.loads(line))
    return events


def load_fixture(run):
    path = run / "fixture.json"
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8-sig"))


def split_segments(frames, gap_ticks):
    segments = []
    current = []
    for frame in frames:
        if current and frame["ticks"] - current[-1]["ticks"] > gap_ticks:
            segments.append(current)
            current = []
        current.append(frame)
    if current:
        segments.append(current)
    return segments


def analyze_segment(frames, frequency):
    holds = []
    run_length = 0
    previous = None
    for frame in frames:
        if previous is not None and frame["frameIndex"] == previous:
            run_length += 1
        else:
            if previous is not None:
                holds.append(run_length)
            run_length = 1
            previous = frame["frameIndex"]
    if previous is not None:
        holds.append(run_length)

    indices = [frame["frameIndex"] for frame in frames]
    unique = sorted(set(indices))
    small_missing = 0
    large_jumps = 0
    for earlier, later in zip(unique, unique[1:]):
        gap = later - earlier
        if gap <= 1:
            continue
        if gap < LARGE_JUMP:
            small_missing += gap - 1
        else:
            large_jumps += 1
    backwards = sum(1 for earlier, later in zip(indices, indices[1:]) if later < earlier)
    small_backwards = sum(1 for earlier, later in zip(indices, indices[1:])
                          if 0 < earlier - later <= 5)
    large_backwards = sum(1 for earlier, later in zip(indices, indices[1:])
                          if earlier - later > 5)
    duration = (frames[-1]["ticks"] - frames[0]["ticks"]) / frequency
    typical = Counter({value: count for value, count in Counter(holds).items() if value <= LONG_HOLD})
    long_holds = sum(count for value, count in Counter(holds).items() if value > LONG_HOLD)
    return {
        "frames": len(frames),
        "distinct": len(unique),
        "duplicatePublications": len(frames) - len(unique),
        "smallMissing": small_missing,
        "largeJumps": large_jumps,
        "backwards": backwards,
        "smallBackwards": small_backwards,
        "largeBackwards": large_backwards,
        "longHolds": long_holds,
        "durationSeconds": duration,
        "holds": typical,
    }


def format_histogram(counter, total):
    if not counter:
        return "-"
    return ", ".join(f"{value}:{count}（{100 * count / total:.0f}%）"
                     for value, count in sorted(counter.items()))


def merge_segments(segments):
    merged = Counter()
    counts = Counter()
    for item in segments:
        counts["frames"] += item["frames"]
        counts["distinct"] += item["distinct"]
        counts["duplicatePublications"] += item["duplicatePublications"]
        counts["smallMissing"] += item["smallMissing"]
        counts["largeJumps"] += item["largeJumps"]
        counts["backwards"] += item["backwards"]
        counts["smallBackwards"] += item["smallBackwards"]
        counts["largeBackwards"] += item["largeBackwards"]
        counts["longHolds"] += item["longHolds"]
        counts["durationSeconds"] += item["durationSeconds"]
        merged.update(item["holds"])
    return counts, merged


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=Path)
    parser.add_argument("--gap-seconds", type=float, default=0.5,
                        help="同一素材の公開がこの秒数以上空いたら区間を分ける（既定 0.5）")
    args = parser.parse_args()

    for run in args.run:
        trace = run / "trace.jsonl"
        if not trace.exists():
            print(f"## {run.name}: trace.jsonl が無い")
            continue
        events = load_events(trace)
        meta = next((event for event in events if event.get("type") == "meta"), None)
        if meta is None:
            print(f"## {run.name}: meta が無い")
            continue
        frequency = meta["frequency"]
        fixture = load_fixture(run)
        clip_info = {clip["id"]: clip for clip in fixture.get("clips", [])}
        frames = [event for event in events
                  if event.get("type") == "frame" and event.get("markerValid") is True]
        by_clip = {}
        for frame in frames:
            by_clip.setdefault(frame.get("clipId"), []).append(frame)

        print(f"## {run.name}")
        print(f"- frame イベント（marker 有効）: {len(frames)} 件 / "
              f"fixture ltcFps={fixture.get('ltcFps')} / qpcFrequency={frequency}")
        print(f"- 小欠落 = 連番の飛び 2〜{LARGE_JUMP - 1}（数フレームの落ち）、"
              f"大飛び = 飛び >= {LARGE_JUMP}（シーク/不連続）、長保持 = 連続発表 > {LONG_HOLD}（freeze/停止）、"
              f"逆行は 小（<= 5 フレーム）/ 大（ループ・シーク復帰）に分ける")
        print()
        print("| clip | 素材 fps | 発表数 | 異なり | 重複発表 | 小欠落 | 大飛び | 小逆行 | 大逆行 | 長保持 | 保持2 | 保持3 | 新規フレーム率 |")
        print("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
        details = []
        for clip_id in sorted(by_clip, key=lambda value: (value is None, value)):
            clip_frames = sorted(by_clip[clip_id], key=lambda event: event["ticks"])
            segments = split_segments(clip_frames, int(args.gap_seconds * frequency))
            stats = [analyze_segment(segment, frequency) for segment in segments]
            counts, holds = merge_segments(stats)
            total_holds = sum(holds.values())
            info = clip_info.get(clip_id, {})
            if info.get("fpsNumerator") and info.get("fpsDenominator"):
                content_fps = info["fpsNumerator"] / info["fpsDenominator"]
            else:
                content_fps = counts["distinct"] / counts["durationSeconds"] if counts["durationSeconds"] else 0.0
            new_rate = counts["distinct"] / counts["durationSeconds"] if counts["durationSeconds"] else 0.0
            print(f"| {clip_id} | {content_fps:.2f} | {counts['frames']} | {counts['distinct']} | "
                  f"{counts['duplicatePublications']} | {counts['smallMissing']} | {counts['largeJumps']} | "
                  f"{counts['smallBackwards']} | {counts['largeBackwards']} | {counts['longHolds']} | "
                  f"{holds.get(2, 0)} | {holds.get(3, 0)} | {new_rate:.2f}/s |")
            details.append({
                "clip": clip_id, "contentFps": content_fps, "newRate": new_rate,
                "counts": counts, "holds": holds, "totalHolds": total_holds, "stats": stats,
            })
        print()
        for item in details:
            counts = item["counts"]
            print(f"- clip {item['clip']}（{item['contentFps']:.2f}fps）: 新規フレーム {item['newRate']:.2f}/s、"
                  f"保持数の分布（<= {LONG_HOLD}）: {format_histogram(item['holds'], item['totalHolds'])}")
            clean = [segment for segment in item["stats"]
                     if segment["largeJumps"] == 0 and segment["smallBackwards"] == 0
                     and segment["durationSeconds"] >= 5.0 and segment["frames"] >= 10]
            clean_counts, clean_holds = merge_segments(clean)
            clean_total = sum(clean_holds.values())
            clean_rate = (clean_counts["distinct"] / clean_counts["durationSeconds"]
                          if clean_counts["durationSeconds"] else 0.0)
            print(f"  定常区間（大飛び・逆行なし、>= 5s）: {len(clean)} 区間 "
                  f"{clean_counts['durationSeconds']:.1f}s、発表{clean_counts['frames']} "
                  f"異なり{clean_counts['distinct']} 小欠落{clean_counts['smallMissing']} "
                  f"長保持{clean_counts['longHolds']}、新規 {clean_rate:.2f}/s、保持数 "
                  f"{format_histogram(clean_holds, clean_total)}")
            for index, segment in enumerate(item["stats"], 1):
                if segment["largeJumps"] == 0 and segment["smallBackwards"] == 0:
                    continue
                print(f"    非定常区間{index}: {segment['durationSeconds']:.1f}s 発表{segment['frames']} "
                      f"異なり{segment['distinct']} 小欠落{segment['smallMissing']} "
                      f"大飛び{segment['largeJumps']} 小逆行{segment['smallBackwards']} "
                      f"大逆行{segment['largeBackwards']}")
        print()


if __name__ == "__main__":
    main()
