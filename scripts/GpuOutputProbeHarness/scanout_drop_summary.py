"""present.scanout から表示欠落を数える（V8 の主指標）。

2 つの指標を出す。

  gap>25ms : deadlineQpc(= SyncQPCTime) の間隔が 25ms を超えた回数。従来指標。
             統計取得タイミングの揺れを含むため、単独では物理的な欠落の証拠にならない。
  missRef  : detail の "PresentCount:PresentRefreshCount" から取った
             PresentRefreshCount の差が 2 以上（= 表示機会を実際に飛ばした）回数。
             DXGI が報告する走査済みリフレッシュ番号の差なので、こちらが実際の欠落に近い。

使い方:
    python scanout_drop_summary.py <run ルート> [開始秒] [終了秒]

<run ルート> の直下にある run ディレクトリ（app/events.jsonl を持つもの）を全て集計する。
条件は各 run の runner-result.json の outputThreadPriority から取る。
"""
import json
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

GAP_MS = 25.0
CONSECUTIVE_SECONDS = 0.05


def analyse(run_dir: Path, start: float, end: float):
    manifest = json.loads((run_dir / "app" / "manifest.json").read_text(encoding="utf-8-sig"))
    frequency = manifest["qpcFrequency"]
    origin = manifest["originQpc"]

    condition = "?"
    result = run_dir / "runner-result.json"
    if result.exists():
        condition = json.loads(result.read_text(encoding="utf-8-sig")).get("outputThreadPriority") or "default"

    rows = []
    with (run_dir / "app" / "events.jsonl").open(encoding="utf-8") as handle:
        for line in handle:
            if '"present.scanout"' not in line:
                continue
            event = json.loads(line)
            seconds = (event["deadlineQpc"] - origin) / frequency
            if not start <= seconds <= end:
                continue
            parts = event.get("detail", "").split(":")
            refresh = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else None
            rows.append((seconds, event["deadlineQpc"], refresh))

    rows.sort(key=lambda row: row[1])
    if len(rows) < 2:
        return None

    gaps = [
        (round(a[0], 3), round((b[1] - a[1]) * 1000.0 / frequency, 2))
        for a, b in zip(rows, rows[1:])
        if (b[1] - a[1]) * 1000.0 / frequency > GAP_MS
    ]
    misses = [
        (round(a[0], 3), b[2] - a[2])
        for a, b in zip(rows, rows[1:])
        if a[2] is not None and b[2] is not None and b[2] - a[2] >= 2
    ]

    span = rows[-1][0] - rows[0][0]
    return {
        "condition": condition,
        "rate": (len(rows) - 1) / span if span > 0 else 0.0,
        "gaps": gaps,
        "misses": misses,
        "consecutive": any(b[0] - a[0] < CONSECUTIVE_SECONDS for a, b in zip(misses, misses[1:])),
    }


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    root = Path(argv[1])
    start = float(argv[2]) if len(argv) > 2 else 5.0
    end = float(argv[3]) if len(argv) > 3 else 45.0

    runs = sorted(p for p in root.iterdir() if p.is_dir() and (p / "app" / "events.jsonl").exists())
    if not runs:
        print(f"run が見つかりません: {root}")
        return 1

    print(f"窓 {start}〜{end} 秒")
    print(f"{'run':<28}{'条件':>10}{'scanout/s':>12}{'gap>25ms':>10}{'missRef':>9}  連続")
    print("-" * 78)
    for run in runs:
        info = analyse(run, start, end)
        if info is None:
            print(f"{run.name:<28}  窓内に present.scanout が無い")
            continue
        print(f"{run.name:<28}{info['condition']:>10}{info['rate']:>12.3f}"
              f"{len(info['gaps']):>10}{len(info['misses']):>9}  {'あり' if info['consecutive'] else 'なし'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
