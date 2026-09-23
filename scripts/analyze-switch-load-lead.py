"""v0.5.1 項目 4: トラック切替ごとの「読み込みの所要」と「切替直後の残差」を並べる。

出力トレース（events.jsonl）とアプリログから、Continue のトラック切替 1 回ごとに:
  - load_ms:     読み込みの発行（load.issue）→ 読み込み後の最初の配信（gst.delivery）
  - first_delta: 読み込み後の最初の sync.evaluate の delta（秒。正 = 映像が LTC より手前）
  - settle_s:    |delta| が 80ms 以内に入って 1 秒続くまで（読み込みの発行から）
  - nth:         同じトラックの何回目の読み込みか（1 回目は冷えた状態の可能性がある）
を出し、トラックごとに 2 回目以降の load_ms のばらつきと、first_delta との差をまとめる。

使い方:
  python scripts/analyze-switch-load-lead.py <events.jsonl> <app.log> <origin_qpc> <origin_wall>
    origin_qpc / origin_wall は harness.jsonl の l2-audit-origin（qpc と startedAt）。
    例: ... 4586881308693 2026-09-23T13:58:26.22618+09:00

素材名はトラック名のまま出るので、結果を公開文書に貼るときは匿名 ID に置き換えること。
"""
import datetime
import json
import re
import statistics
import sys
from collections import defaultdict

SETTLE_THRESHOLD = 0.080
SETTLE_HOLD_SECONDS = 1.0


def parse_wall(text):
    return datetime.datetime.fromisoformat(text)


def main(argv):
    if len(argv) != 5:
        print(__doc__)
        return 2
    events_path, log_path, origin_qpc, origin_wall = argv[1], argv[2], int(argv[3]), parse_wall(argv[4])
    freq = 1e7

    def wall_to_qpc(wall):
        return origin_qpc + int((wall - origin_wall).total_seconds() * freq)

    # アプリログ: 切替の時刻とトラック名
    switches = []
    pattern = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d) .*Continue mode: switching to track (.+?) at media position")
    with open(log_path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = pattern.match(line)
            if m:
                wall = datetime.datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f %z")
                switches.append((wall_to_qpc(wall), m.group(2)))

    wanted = ("load.issue", "load.return", "gst.delivery", "sync.evaluate")
    events = []
    with open(events_path, encoding="utf-8") as f:
        for line in f:
            if not any(f'"{w}"' in line for w in wanted):
                continue
            e = json.loads(line)
            events.append(e)
    events.sort(key=lambda e: e["qpc"])

    rows = []
    loads_seen = defaultdict(int)
    for idx, e in enumerate(events):
        if e["stage"] != "load.issue":
            continue
        # 切替のログ行（読み込みの直前、1 秒以内）に対応づける。無ければ切替ではない読み込み。
        near = [s for s in switches if 0 <= e["qpc"] - s[0] <= freq]
        if not near:
            continue
        track = near[-1][1]
        loads_seen[track] += 1
        ret = next((x for x in events[idx + 1:] if x["stage"] == "load.return"), None)
        if ret is None:
            continue
        first = next((x for x in events[idx + 1:] if x["stage"] == "gst.delivery" and x["qpc"] > ret["qpc"]), None)
        evals = [x for x in events[idx + 1:] if x["stage"] == "sync.evaluate" and x["qpc"] > ret["qpc"]]
        evals = [x for x in evals if x["qpc"] - e["qpc"] < 30 * freq]
        deltas = []
        for x in evals:
            m = re.search(r"delta=(-?[\d.]+|nan)", x.get("detail") or "")
            if m and m.group(1) != "nan":
                deltas.append((x["qpc"], float(m.group(1))))
        settle = None
        for i, (q, d) in enumerate(deltas):
            if abs(d) > SETTLE_THRESHOLD:
                continue
            window = [dd for qq, dd in deltas[i:] if qq - q <= SETTLE_HOLD_SECONDS * freq]
            if window and all(abs(dd) <= SETTLE_THRESHOLD for dd in window):
                settle = (q - e["qpc"]) / freq
                break
        rows.append({
            "track": track,
            "nth": loads_seen[track],
            "at_s": (e["qpc"] - origin_qpc) / freq,
            "load_ms": (first["qpc"] - e["qpc"]) / 1e4 if first else None,
            "first_delta": deltas[0][1] if deltas else None,
            "settle_s": settle,
        })

    print(f"{'at_s':>9} {'track':<24} {'nth':>3} {'load_ms':>8} {'first_delta_ms':>14} {'settle_s':>8}")
    for r in rows:
        print(f"{r['at_s']:9.1f} {r['track'][:24]:<24} {r['nth']:>3} "
              f"{r['load_ms'] if r['load_ms'] is not None else float('nan'):8.1f} "
              f"{(r['first_delta'] or float('nan')) * 1000:14.1f} "
              f"{r['settle_s'] if r['settle_s'] is not None else float('nan'):8.2f}")

    print("\nトラックごと（2 回目以降の読み込み）:")
    by_track = defaultdict(list)
    for r in rows:
        if r["nth"] >= 2 and r["load_ms"] is not None:
            by_track[r["track"]].append(r)
    for track, rs in by_track.items():
        loads = [r["load_ms"] for r in rs]
        gaps = [r["first_delta"] * 1000 - r["load_ms"] for r in rs if r["first_delta"] is not None]
        spread = (max(loads) - min(loads)) if len(loads) > 1 else 0.0
        print(f"  {track[:24]:<24} n={len(loads)} load_ms median={statistics.median(loads):.1f} "
              f"spread={spread:.1f} first_delta-load median={statistics.median(gaps) if gaps else float('nan'):.1f}ms")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
