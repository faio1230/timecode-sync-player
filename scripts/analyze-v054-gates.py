#!/usr/bin/env python3
"""v0.5.4 段 0: LTC シナリオのアプリログから、門ごとの発火回数と足した遅延を数える。

使い方:
  python scripts/analyze-v054-gates.py --run label=REPORT_DIR [--run ...] --out DIR

REPORT_DIR は run-ltc-scenarios.ps1 の -ReportDir。REPORT_DIR/scenarios/*/harness.jsonl の
scenario-start と app-exit の時刻（UTC）をローカル（+09:00）へ直し、その窓のアプリログ
（REPORT_DIR/app-logs/timecodesyncplayer-*.log）だけを数える。窓が重なったら警告する
（並行実行は想定していない）。

門の番号は docs/design/v0.5.3-d38-seek-gates.md §3 の 24 門。数え方の定義は
docs/design/v0.5.4-gate-baseline.md に書く。ここでは「どの行をどう対にするか」をコードで固定する。

v0.5.4 段 0 で足した行（すべて Debug、書式 sync.gate <名前> <値>）:
  G1  signal-loss-confirm elapsedMs=… reason=…
  G5  pending-suppress playback=… target=…
  G6  seek-settled target=… elapsedMs=…
  G7  pending-timeout elapsedMs=… target=…
  G8  pending-replace pendingTarget=… requestedTarget=…
  G9  post-settle-suppress elapsedMs=… target=…
  G11 trust-reacquire samples=… required=…
  G12 untrusted-defer ltc=… playback=…
  G14 seek-debounce ltc=… playback=… / seek-skip reason="Debounced" …
  G17 load-suppress elapsedMs=…
  G18 load-release elapsedMs=…
  G22 native-seek-defer ltc=…
"""
import argparse
import csv
import json
import re
import statistics
import sys
from datetime import datetime, timedelta
from pathlib import Path

LINE_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) \+\d{2}:\d{2} \[(\w+)\] (.*)$")
KV_RE = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)=("([^"]*)"|\S+)')

GATES = [
    ("g01", "1 損失の確定(250ms)"),
    ("g02", "2 保持直後 Jump の即時復帰"),
    ("g03", "3 JumpAppliedOnce のラッチ"),
    ("g04", "4 Jump の確認窓"),
    ("g05", "5 シーク保留の抑止"),
    ("g06", "6 着地(Settled)判定"),
    ("g07", "7 保留のタイムアウト(2s)"),
    ("g08", "8 到達不能 pending の置き換え"),
    ("g09", "9 着地後の抑止(500ms)"),
    ("g10", "10 シーク中の位置の不信"),
    ("g11", "11 時間切れ後の位置の再確認"),
    ("g12", "12 未信頼の決定(シーク停止)"),
    ("g13", "13 D37-a のゲート"),
    ("g14", "14 シークのデバウンス(250ms)"),
    ("g15", "15 速度補正優先"),
    ("g16", "16 着地窓(シーク優先)"),
    ("g17", "17 ロード中の抑止"),
    ("g18", "18 ロード解除の成立"),
    ("g19", "19 保持着地の 1 フレーム省略"),
    ("g20", "20 境界ホールド中の保持着地スキップ"),
    ("g21", "21 保持値の変化の 1 回着地"),
    ("g22", "22 手動・ネイティブシーク中の抑止"),
    ("g23", "23 補正の着地窓(1.0s)"),
    ("g24", "24 Jump のシーク連鎖の歯止め"),
]


def parse_ts(text):
    return datetime.strptime(text, "%Y-%m-%d %H:%M:%S.%f")


def parse_iso_utc(text):
    # harness は 7 桁の小数秒を持つ。UTC の aware を作り、ローカルへ直して naive にする。
    m = re.match(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})", text)
    if not m:
        return None
    frac = (m.group(2) or "0")[:6].ljust(6, "0")
    base = f"{m.group(1)}.{frac}"
    tz = m.group(3)
    suffix = "+00:00" if tz == "Z" else tz
    dt = datetime.strptime(base + suffix, "%Y-%m-%dT%H:%M:%S.%f%z")
    return dt.astimezone().replace(tzinfo=None)


def parse_kv(message):
    out = {}
    for m in KV_RE.finditer(message):
        out[m.group(1)] = m.group(3) if m.group(3) is not None else m.group(2)
    return out


def fnum(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return None


class Event:
    __slots__ = ("ts", "level", "msg", "kv")

    def __init__(self, ts, level, msg):
        self.ts = ts
        self.level = level
        self.msg = msg
        self.kv = parse_kv(msg)


class GateStat:
    def __init__(self, gate_id, label):
        self.gate_id = gate_id
        self.label = label
        self.count = 0
        self.delays = []       # ms
        self.detail = {}       # 内訳（理由・種類のカウント）
        self.extra = {}        # 注記用の値

    def add(self, delay_ms=None, detail_key=None, n=1):
        self.count += n
        if delay_ms is not None:
            self.delays.append(delay_ms)
        if detail_key is not None:
            self.detail[detail_key] = self.detail.get(detail_key, 0) + n

    def median(self):
        return round(statistics.median(self.delays), 1) if self.delays else None

    def maximum(self):
        return round(max(self.delays), 1) if self.delays else None


def find_next(events, start_index, predicate, within_seconds):
    limit = events[start_index].ts + timedelta(seconds=within_seconds)
    for j in range(start_index + 1, len(events)):
        e = events[j]
        if e.ts > limit:
            break
        if predicate(e):
            return e
    return None


def ms_between(a, b):
    return (b - a).total_seconds() * 1000.0


def episodes(events_subset, gap_seconds=0.5):
    if not events_subset:
        return []
    spans = []
    start = events_subset[0]
    last = events_subset[0]
    for e in events_subset[1:]:
        if (e.ts - last.ts).total_seconds() <= gap_seconds:
            last = e
        else:
            spans.append((start, last))
            start = last = e
    spans.append((start, last))
    return spans


def analyze_scenario(harness_events, log_events):
    start = None
    end = None
    for h in harness_events:
        event = h.get("event")
        if event == "scenario-start" and start is None:
            start = parse_iso_utc(h["timestampUtc"])
        if event == "app-exit" and end is None:
            end = parse_iso_utc(h["timestampUtc"])
    if start is None:
        return None
    if end is None:
        end = start
    window = [e for e in log_events if start <= e.ts <= end + timedelta(seconds=2)]
    stats = {gid: GateStat(gid, label) for gid, label in GATES}
    metrics = {}

    # ---- 直列化したイベント ----
    def msg_has(e, text):
        return text in e.msg

    # シーク発行（同期 2 種・着地・Jump 補正）
    seeks = []  # dict(ts, kind, target)
    for e in window:
        if "LTC timecode held: landing seek issued" in e.msg:
            target = fnum(e.kv.get("target"))
            seeks.append({"ts": e.ts, "kind": "landing", "target": target, "event": e})
        elif re.search(r"Timecode sync seek .*success=true", e.msg, re.IGNORECASE):
            target = fnum(e.kv.get("target"))
            seeks.append({"ts": e.ts, "kind": "sync-single", "target": target, "event": e})
        elif re.search(r"Continue mode: sync seek .*success=true", e.msg, re.IGNORECASE):
            target = fnum(e.kv.get("target"))
            seeks.append({"ts": e.ts, "kind": "sync-continue", "target": target, "event": e})
        elif "Jump correction seek" in e.msg:
            target = fnum(e.kv.get("target"))
            seeks.append({"ts": e.ts, "kind": "jump-correction", "target": target, "event": e})
    seeks.sort(key=lambda s: s["ts"])

    # 保留の決着行（最初の 1 回だけ数える用の位置）
    settle_idx = []
    for i, e in enumerate(window):
        if (e.msg.startswith("Timecode sync pending") or
                "sync.gate seek-settled" in e.msg or
                "sync.gate pending-timeout" in e.msg or
                "sync.gate trust-reacquire" in e.msg):
            settle_idx.append(i)

    def next_resolution(ts, within_seconds=3.0):
        limit = ts + timedelta(seconds=within_seconds)
        for e in window:
            if e.ts <= ts or e.ts > limit:
                continue
            if (e.msg.startswith("Timecode sync pending") or
                    "sync.gate seek-settled" in e.msg or
                    "sync.gate pending-timeout" in e.msg or
                    "sync.gate trust-reacquire" in e.msg):
                return e
        return None

    # ---- G1 損失の確定 ----
    g = stats["g01"]
    for e in window:
        if "sync.gate signal-loss-confirm" in e.msg:
            elapsed = fnum(e.kv.get("elapsedMs"))
            g.add(elapsed, detail_key="reason=" + str(e.kv.get("reason")))
    for e in window:
        if "LTC signal lost: playback paused" in e.msg:
            g.detail["pause-line"] = g.detail.get("pause-line", 0) + 1
            g.detail["pause-reason=" + str(e.kv.get("reason"))] = \
                g.detail.get("pause-reason=" + str(e.kv.get("reason")), 0) + 1

    # ---- G2 保持直後 Jump の即時復帰 ----
    g = stats["g02"]
    for i, e in enumerate(window):
        if 'Sync lifecycle: "SignalRecovered" source=held-jump' in e.msg:
            delay = None
            for j in range(i - 1, -1, -1):
                if "LTC frame diagnostic status=\"Jump\"" in window[j].msg:
                    delta = ms_between(window[j].ts, e.ts)
                    if delta <= 2000:
                        delay = delta
                    break
            g.add(delay)
    for e in window:
        if 'Sync lifecycle: "SignalRecovered" source=valid-frames' in e.msg:
            g.detail["valid-frames"] = g.detail.get("valid-frames", 0) + 1

    # ---- G3 JumpAppliedOnce ----
    g = stats["g03"]
    dropped = [(i, e) for i, e in enumerate(window) if "sync: Jump dropped (JumpAppliedOnce)" in e.msg]
    for i, e in dropped:
        nxt = find_next(window, i, lambda x: "applying the" in x.msg and "frame once" in x.msg, 5.0)
        g.add(ms_between(e.ts, nxt.ts) if nxt else None,
              detail_key="reason=" + str(e.kv.get("reason")))

    # ---- G4 Jump の確認窓 ----
    g = stats["g04"]
    for i, e in enumerate(window):
        if "holding unconfirmed Jump frame" in e.msg:
            nxt = find_next(window, i, lambda x: "applying the confirmed Jump frame once" in x.msg, 2.0)
            g.add(ms_between(e.ts, nxt.ts) if nxt else None,
                  detail_key="reason=" + str(e.kv.get("reason")))
        elif "applying the confirmed Jump frame once" in e.msg:
            g.detail["confirmed"] = g.detail.get("confirmed", 0) + 1
        elif "dropping out-of-window pending Jump frame" in e.msg:
            g.detail["out-of-window"] = g.detail.get("out-of-window", 0) + 1

    # ---- G5 シーク保留の抑止 ----
    g = stats["g05"]
    suppress = [e for e in window if "sync.gate pending-suppress" in e.msg]
    for e in suppress:
        g.add()
    for span in episodes(suppress, gap_seconds=1.0):
        g.delays.append(ms_between(span[0].ts, span[1].ts))
    # 保留エピソード（シーク発行→決着）も内訳として残す
    resolves = []
    for e in window:
        if (e.msg.startswith('Timecode sync pending "Settled"') or
                'Timecode sync pending "TimedOut"' in e.msg or
                'Timecode sync pending "Superseded"' in e.msg):
            resolves.append(e)
    for s in seeks:
        nxt = next((r for r in resolves if r.ts >= s["ts"]), None)
        if nxt:
            g.extra.setdefault("episode_ms", []).append(ms_between(s["ts"], nxt.ts))

    # ---- G6 着地 ----
    g = stats["g06"]
    for e in window:
        if "sync.gate seek-settled" in e.msg:
            g.add(fnum(e.kv.get("elapsedMs")), detail_key="target=" + str(e.kv.get("target")))

    # ---- G7 タイムアウト ----
    g = stats["g07"]
    for e in window:
        if "sync.gate pending-timeout" in e.msg:
            g.add(fnum(e.kv.get("elapsedMs")))
        elif 'Timecode sync pending "TimedOut"' in e.msg:
            g.detail["pending-line"] = g.detail.get("pending-line", 0) + 1

    # ---- G8 置き換え ----
    g = stats["g08"]
    for e in window:
        if "sync.gate pending-replace" in e.msg:
            g.add()
        elif 'Timecode sync pending "Superseded"' in e.msg:
            g.detail["discard"] = g.detail.get("discard", 0) + 1

    # ---- G9 着地後の抑止 ----
    g = stats["g09"]
    for e in window:
        if "sync.gate post-settle-suppress" in e.msg:
            g.add(fnum(e.kv.get("elapsedMs")))

    # ---- G10 位置の不信（シーク発行が発火） ----
    g = stats["g10"]
    for s in seeks:
        nxt = next_resolution(s["ts"])
        g.add(ms_between(s["ts"], nxt.ts) if nxt else None, detail_key=s["kind"])

    # ---- G11 再確認 ----
    g = stats["g11"]
    for i, e in enumerate(window):
        if "sync.gate trust-reacquire" in e.msg:
            delay = None
            for j in range(i - 1, -1, -1):
                prev = window[j]
                if 'pending "TimedOut"' in prev.msg or 'pending "Superseded"' in prev.msg:
                    delta = ms_between(prev.ts, e.ts)
                    if delta <= 3000:
                        delay = delta
                    break
            g.add(delay)

    # ---- G12 未信頼の決定 ----
    g = stats["g12"]
    untrusted = [e for e in window if "sync.gate untrusted-defer" in e.msg]
    for _ in untrusted:
        g.add()
    for span in episodes(untrusted, gap_seconds=0.5):
        g.delays.append(ms_between(span[0].ts, span[1].ts))

    # ---- G13 D37-a ゲート ----
    g = stats["g13"]
    for i, e in enumerate(window):
        if "Timecode sync seek gated" in e.msg:
            nxt_seek = next((s for s in seeks if s["ts"] > e.ts), None)
            g.add(ms_between(e.ts, nxt_seek["ts"]) if nxt_seek and
                  ms_between(e.ts, nxt_seek["ts"]) <= 2000 else None)
        elif "Seek decision gate: rejected unstable sample" in e.msg:
            g.detail["rejected"] = g.detail.get("rejected", 0) + 1
        elif "Correction gate: rejected unstable sample" in e.msg:
            g.detail["correction-rejected"] = g.detail.get("correction-rejected", 0) + 1

    # ---- G14 デバウンス ----
    g = stats["g14"]
    debounce = []
    for e in window:
        if "sync.gate seek-debounce" in e.msg:
            debounce.append(e)
            g.detail["single"] = g.detail.get("single", 0) + 1
        elif "sync.gate seek-skip" in e.msg and str(e.kv.get("reason")) == "Debounced":
            debounce.append(e)
            g.detail["continue"] = g.detail.get("continue", 0) + 1
    for e in debounce:
        g.add()
    for span in episodes(debounce, gap_seconds=0.5):
        g.delays.append(ms_between(span[0].ts, span[1].ts))
    for i, e in enumerate(window):
        if "sync.gate seek-skip" in e.msg:
            detail_key = "skip=" + str(e.kv.get("reason"))
            stats["g05"].detail[detail_key] = stats["g05"].detail.get(detail_key, 0) + 1

    # ---- G15 速度補正優先 ----
    g = stats["g15"]
    for e in window:
        if "rate catch-up preferred" in e.msg:
            g.add()
        elif "rate catch-up settled" in e.msg:
            seconds = fnum(e.kv.get("elapsedSeconds"))
            g.add(seconds * 1000.0 if seconds is not None else None,
                  detail_key="settled")
        elif "rate catch-up escalated" in e.msg:
            g.detail["escalated"] = g.detail.get("escalated", 0) + 1

    # ---- G16 着地窓 ----
    g = stats["g16"]
    for e in window:
        if "landing window closed" in e.msg or "Seek landing: follow-start episode ended" in e.msg:
            reason = "other"
            if "on arrival" in e.msg:
                reason = "arrival"
            elif "without progress" in e.msg:
                reason = "without-progress"
            elif "at the seek cap" in e.msg:
                reason = "seek-cap"
            elif "at the age cap" in e.msg:
                reason = "age-cap"
            g.add(fnum(e.kv.get("ageMs")), detail_key=reason)
        elif "follow start landing window opened" in e.msg:
            g.detail["opened"] = g.detail.get("opened", 0) + 1

    # ---- G17 ロード中の抑止 ----
    g = stats["g17"]
    for e in window:
        if "sync.gate load-suppress" in e.msg:
            g.add(fnum(e.kv.get("elapsedMs")))

    # ---- G18 ロード解除 ----
    g = stats["g18"]
    for e in window:
        if "sync.gate load-release" in e.msg:
            g.add(fnum(e.kv.get("elapsedMs")), detail_key="progress")
        elif "Timecode sync: file load released" in e.msg:
            g.add(detail_key=str(e.kv.get("Reason")))
        elif "reapplying the last accepted timecode once after file load" in e.msg:
            g.detail["reapply"] = g.detail.get("reapply", 0) + 1

    # ---- G19 保持着地の 1 フレーム省略 ----
    g = stats["g19"]
    for e in window:
        if "landing skipped (within one frame)" in e.msg:
            g.add(0.0)

    # ---- G20 境界ホールド ----
    g = stats["g20"]
    holds = []
    for i, e in enumerate(window):
        if "single mode: clip boundary hold ltc=" in e.msg.lower() or \
                e.msg.startswith("Single mode: clip boundary hold ltc="):
            holds.append(e)
    skips = [e for e in window if "landing skipped (clip boundary hold active)" in e.msg]
    for _ in skips:
        g.add()
    # 遅延はスキップが起きたときだけ、そのホールドの解除まで（発火 0 の周は遅延も 0 件）。
    for skip in skips:
        nxt_hold = next((h for h in holds if h.ts <= skip.ts), None)
        nxt_release = next((e for e in window if e.ts > skip.ts and
                            "clip boundary hold released" in e.msg), None)
        if nxt_hold is not None and nxt_release is not None:
            g.delays.append(ms_between(nxt_hold.ts, nxt_release.ts))
    g.detail["holds"] = len(holds)

    # ---- G21 保持値の変化 ----
    g = stats["g21"]
    for e in window:
        if "applying the held value change frame once" in e.msg:
            g.add(0.0)

    # ---- G22 手動・ネイティブシーク ----
    g = stats["g22"]
    native = [e for e in window if "sync.gate native-seek-defer" in e.msg]
    for _ in native:
        g.add()
    for span in episodes(native, gap_seconds=0.5):
        g.delays.append(ms_between(span[0].ts, span[1].ts))
    for e in window:
        if 'Sync lifecycle: "ManualSeek"' in e.msg or 'Sync lifecycle: "TimelineSeek"' in e.msg:
            g.detail["manual"] = g.detail.get("manual", 0) + 1

    # ---- G23 補正の着地窓 ----
    g = stats["g23"]
    switch = [e for e in window if "Smooth rate limit switched to" in e.msg]
    active = None
    for e in switch:
        if 'landingWindow="active"' in e.msg or "landingWindow=active" in e.msg:
            active = e
            g.detail["active"] = g.detail.get("active", 0) + 1
        elif active is not None:
            span = ms_between(active.ts, e.ts)
            g.detail["ended"] = g.detail.get("ended", 0) + 1
            if span <= 1500:
                g.add(span)
            else:
                g.detail["ended-late"] = g.detail.get("ended-late", 0) + 1
            active = None

    # ---- G24 Jump 連鎖の歯止め ----
    g = stats["g24"]
    for e in window:
        if "Jump correction limit reached" in e.msg:
            g.add()
        elif "Jump correction seek" in e.msg:
            g.detail["jump-seek"] = g.detail.get("jump-seek", 0) + 1

    # ---- §9 の指標 ----
    sync_seeks = [s for s in seeks if s["kind"].startswith("sync")]
    landing_seeks = [s for s in seeks if s["kind"] == "landing"]
    metrics["syncSeeks"] = len(sync_seeks)
    metrics["landingSeeks"] = len(landing_seeks)
    metrics["syncAndLandingSeeks"] = len(sync_seeks) + len(landing_seeks)
    metrics["jumpCorrectionSeeks"] = sum(1 for s in seeks if s["kind"] == "jump-correction")
    metrics["timeouts"] = sum(1 for e in window if 'Timecode sync pending "TimedOut"' in e.msg)
    metrics["superseded"] = sum(1 for e in window if 'Timecode sync pending "Superseded"' in e.msg)
    # 通り過ぎの戻し: 着地シークが先行の同期シークから 500ms 以内・目標差 0.25s 以内
    passthrough = 0
    for landing in landing_seeks:
        prior = [s for s in sync_seeks if s["ts"] < landing["ts"] and
                 (landing["ts"] - s["ts"]).total_seconds() <= 0.5]
        if prior and landing["target"] is not None:
            last = prior[-1]
            if last["target"] is not None and abs(last["target"] - landing["target"]) <= 0.25:
                passthrough += 1
    metrics["passThroughReturns"] = passthrough
    # 発振: 直前 2 本と同じ目標（1 フレーム = 0.04s 以内）へ、間隔 1 秒以内の 3 本目以降
    oscillation = 0
    ordered = seeks
    for i in range(2, len(ordered)):
        a, b, c = ordered[i - 2], ordered[i - 1], ordered[i]
        if None in (a["target"], b["target"], c["target"]):
            continue
        near = (abs(c["target"] - a["target"]) <= 0.04 and abs(c["target"] - b["target"]) <= 0.04)
        fast = ((c["ts"] - a["ts"]).total_seconds() <= 1.0)
        if near and fast:
            oscillation += 1
    metrics["oscillation"] = oscillation

    # HUD の hold-landing / jump-black-summary / hold-pause（アプリログと対にする）
    latencies = []
    over_budget = 0
    black_frames = 0
    black_samples = 0
    pause_test = []
    for h in harness_events:
        if h.get("event") == "hold-landing":
            value = h.get("details", {}).get("landingLatencySeconds")
            if value is not None:
                latencies.append(value)
            if h.get("details", {}).get("latencyOverBudget"):
                over_budget += 1
        elif h.get("event") == "jump-black-summary":
            black_frames += int(h.get("details", {}).get("blackFrames") or 0)
            black_samples += int(h.get("details", {}).get("samples") or 0)
        elif h.get("event") == "hold-pause":
            value = h.get("details", {}).get("pauseLatencySeconds")
            if value is not None:
                pause_test.append(value)
    metrics["landingLatencyMedian"] = round(statistics.median(latencies), 3) if latencies else None
    metrics["landingLatencyMax"] = round(max(latencies), 3) if latencies else None
    metrics["landingLatencyN"] = len(latencies)
    metrics["landingOverBudget"] = over_budget
    metrics["jumpBlackFrames"] = black_frames
    metrics["jumpBlackSamples"] = black_samples
    metrics["pauseTestMedian"] = round(statistics.median(pause_test), 3) if pause_test else None
    metrics["pauseTestN"] = len(pause_test)

    # R-1/R-2: 連続する Duplicate の先頭から playback paused まで（アプリログ）。
    # 無音（SignalLoss）で止まった回は先行 Duplicate が無いので数えない（N/A）。
    pause_log = []
    for i, e in enumerate(window):
        if "LTC signal lost: playback paused" in e.msg:
            first_dup = None
            j = i - 1
            while j >= 0 and (e.ts - window[j].ts).total_seconds() <= 2.0:
                prev = window[j]
                if 'status="Duplicate"' in prev.msg:
                    first_dup = prev
                elif 'status="' in prev.msg:
                    break
                j -= 1
            if first_dup is not None:
                pause_log.append(ms_between(first_dup.ts, e.ts))
    metrics["pauseLogMs"] = [round(v, 1) for v in pause_log]

    return {
        "scenario": scenario_id_of(harness_events),
        "start": start,
        "end": end,
        "stats": stats,
        "metrics": metrics,
        "window_count": len(window),
    }


def scenario_id_of(harness_events):
    for h in harness_events:
        if h.get("event") == "scenario-start":
            return h.get("details", {}).get("testId") or "?"
    return "?"


def load_harness(path):
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def load_log(path):
    events = []
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            m = LINE_RE.match(line.rstrip("\n"))
            if not m:
                continue
            events.append(Event(parse_ts(m.group(1)), m.group(2), m.group(3)))
    events.sort(key=lambda e: e.ts)
    return events


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", action="append", required=True,
                        help="label=REPORT_DIR（複数可）")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    all_gate_rows = []
    all_metric_rows = []
    all_delay_rows = []

    for spec in args.run:
        label, _, report_text = spec.partition("=")
        report = Path(report_text)
        log_dir = report / "app-logs"
        log_files = sorted(log_dir.glob("*.log"))
        if not log_files:
            log_files = sorted((log_dir / "test-bin").glob("*.log"))
        if not log_files:
            print(f"WARN {label}: no app log under {log_dir}", file=sys.stderr)
            continue
        log_events = []
        for log_file in log_files:
            log_events.extend(load_log(log_file))
        log_events.sort(key=lambda e: e.ts)

        scenario_dirs = sorted((report / "scenarios").glob("*/harness.jsonl"))
        results = []
        windows = []
        for harness_path in scenario_dirs:
            harness_events = load_harness(harness_path)
            result = analyze_scenario(harness_events, log_events)
            if result is None:
                continue
            results.append(result)
            windows.append((result["start"], result["end"], result["scenario"]))
        # 窓の重なり検査
        windows.sort()
        for i in range(1, len(windows)):
            if windows[i][0] < windows[i - 1][1]:
                print(f"WARN {label}: scenario windows overlap "
                      f"{windows[i-1][2]} {windows[i-1][1]} / {windows[i][2]} {windows[i][0]}",
                      file=sys.stderr)

        for result in results:
            scenario = result["scenario"]
            for gid, label_gate in GATES:
                stat = result["stats"][gid]
                all_gate_rows.append({
                    "run": label,
                    "scenario": scenario,
                    "gate": gid,
                    "gateName": label_gate,
                    "fires": stat.count,
                    "delayMedianMs": stat.median(),
                    "delayMaxMs": stat.maximum(),
                    "detail": json.dumps(stat.detail, ensure_ascii=False),
                })
                for delay in stat.delays:
                    all_delay_rows.append({
                        "run": label, "scenario": scenario, "gate": gid, "delayMs": round(delay, 1)})
            metrics = result["metrics"]
            all_metric_rows.append({
                "run": label,
                "scenario": scenario,
                "syncSeeks": metrics["syncSeeks"],
                "landingSeeks": metrics["landingSeeks"],
                "syncAndLandingSeeks": metrics["syncAndLandingSeeks"],
                "jumpCorrectionSeeks": metrics["jumpCorrectionSeeks"],
                "timeouts": metrics["timeouts"],
                "passThroughReturns": metrics["passThroughReturns"],
                "oscillation": metrics["oscillation"],
                "landingLatencyMedian": metrics["landingLatencyMedian"],
                "landingLatencyMax": metrics["landingLatencyMax"],
                "landingLatencyN": metrics["landingLatencyN"],
                "landingOverBudget": metrics["landingOverBudget"],
                "jumpBlackFrames": metrics["jumpBlackFrames"],
                "jumpBlackSamples": metrics["jumpBlackSamples"],
                "pauseTestMedian": metrics["pauseTestMedian"],
                "pauseTestN": metrics["pauseTestN"],
                "pauseLogMs": json.dumps(metrics["pauseLogMs"]),
            })
            print(f"{label} {scenario}: window={result['start']}..{result['end']} "
                  f"lines={result['window_count']} sync={metrics['syncSeeks']} "
                  f"landing={metrics['landingSeeks']} timeouts={metrics['timeouts']}")

    gate_csv = out_dir / "gate-counts.csv"
    with gate_csv.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=[
            "run", "scenario", "gate", "gateName", "fires", "delayMedianMs", "delayMaxMs", "detail"])
        writer.writeheader()
        writer.writerows(all_gate_rows)

    delay_csv = out_dir / "gate-delays.csv"
    with delay_csv.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=["run", "scenario", "gate", "delayMs"])
        writer.writeheader()
        writer.writerows(all_delay_rows)

    metric_csv = out_dir / "scenario-metrics.csv"
    with metric_csv.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(all_metric_rows[0].keys()) if all_metric_rows else
                                ["run", "scenario"])
        writer.writeheader()
        writer.writerows(all_metric_rows)

    print(f"out={out_dir} gates={len(all_gate_rows)} metrics={len(all_metric_rows)}")


if __name__ == "__main__":
    main()
