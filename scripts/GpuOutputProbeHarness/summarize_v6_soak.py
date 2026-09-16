# V6 soak summary (trace-free): Playback perf deltas, playbackRate, error/ring/device counts,
# memory-samples.csv trend and receiver-samples.json liveness from one TestResults run dir.
import io, json, os, re, sys
from datetime import datetime, timedelta

run = sys.argv[1]
log_path = sys.argv[2] if len(sys.argv) > 2 else None
MIB = 1024.0 * 1024.0

def read_json(name, default=None):
    path = os.path.join(run, name)
    if not os.path.exists(path):
        return default
    return json.load(io.open(path, encoding="utf-8-sig"))

result = read_json("runner-result.json", {})
steps = result.get("steps") or []

def step_time(prefix):
    for s in steps:
        m = re.search(re.escape(prefix) + r".*at (\d{2}:\d{2}:\d{2}\.\d{3})", s)
        if m:
            return datetime.strptime(m.group(1), "%H:%M:%S.%f")
    return None

btnplay = step_time("BtnPlay invoked")
seconds = result.get("seconds") or 0
marks = sorted(s for s in steps if s.startswith("VolumeSlider set to "))
if marks:
    last = re.search(r"at (\d{2}:\d{2}:\d{2}\.\d{3})", marks[-1])
    if last:
        btnplay = datetime.strptime(last.group(1), "%H:%M:%S.%f") - timedelta(seconds=300 * len(marks))
if btnplay is None:
    close = step_time("WM_CLOSE posted")
    if close is not None:
        btnplay = close - timedelta(seconds=seconds)

if not log_path:
    for cand in ("app-log-full.txt", "app-log-tail.txt"):
        p = os.path.join(run, cand)
        if os.path.exists(p):
            log_path = p
            break
log_name = os.path.basename(log_path) if log_path else None

line_re = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}) \[(\w+)\] (.*)$")
perf_re = re.compile(r"elapsed=([\d.]+)s .*?playbackRate=([\d.]+).*?gpuPublishedFrames=(\d+)")
entries = []
if log_path:
    for line in io.open(log_path, encoding="utf-8", errors="replace"):
        m = line_re.match(line)
        if m:
            entries.append((datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f"), m.group(3), m.group(4)))

def tod_distance(a, b):
    ta = a.hour * 3600 + a.minute * 60 + a.second + a.microsecond / 1e6
    tb = b.hour * 3600 + b.minute * 60 + b.second + b.microsecond / 1e6
    d = abs(ta - tb)
    return min(d, 86400.0 - d)

end = None
if btnplay and entries:
    ref = min(entries, key=lambda e: tod_distance(e[0].time(), btnplay.time()))[0].date()
    btnplay = datetime.combine(ref, btnplay.time())
if btnplay:
    end = btnplay + timedelta(seconds=seconds)

print("RUN label=%s backend=%s seconds=%s appExit=%s completedNormally=%s receiverExit=%s receiverForced=%s error=%s" % (
    result.get("label"), result.get("playerBackend"), seconds, result.get("appExit"),
    result.get("completedNormally"), result.get("receiverExit"), result.get("receiverForced"), result.get("error")))
print("WINDOW BtnPlay=%s end=%s log=%s" % (btnplay, end, log_name))

perfs = []
errs = []
for ts, lvl, msg in entries:
    if btnplay and not (btnplay <= ts <= end):
        continue
    if msg.startswith("Playback perf ") and "elapsed=" in msg:
        pm = perf_re.search(msg)
        if pm:
            perfs.append((ts, float(pm.group(1)), float(pm.group(2)), int(pm.group(3))))
    if lvl in ("ERR", "FTL"):
        errs.append((ts, msg))

print("PERF lines=%d first=%s last=%s" % (len(perfs), perfs[0][0].strftime("%H:%M:%S") if perfs else "-",
                                          perfs[-1][0].strftime("%H:%M:%S") if perfs else "-"))
if perfs:
    ivs, gaps = [], []
    for (t0, e0, r0, g0), (t1, e1, r1, g1) in zip(perfs, perfs[1:]):
        dt, df = (t1 - t0).total_seconds(), g1 - g0
        if dt <= 0 or dt > 5:
            gaps.append((t1, dt))
            continue
        ivs.append((t1, dt, df, df / dt))
    bad = [iv for iv in ivs if abs(iv[2] - 60.0 * iv[1]) > iv[1]]
    total = perfs[-1][3] - perfs[0][3]
    span = (perfs[-1][0] - perfs[0][0]).total_seconds()
    print("DELTAS intervals=%d gaps=%d dropped=%d totalPublished=%d span=%.2fs expected=%.0f diff=%+.0f fpsMin=%.3f fpsMax=%.3f" % (
        len(ivs), len(gaps), len(bad), total, span, 60.0 * span, total - 60.0 * span,
        min(iv[3] for iv in ivs) if ivs else 0, max(iv[3] for iv in ivs) if ivs else 0))
    for t, dt, df, fps in sorted(bad, key=lambda iv: iv[3])[:10]:
        print("  dropped at=%s dt=%.2f frames=%d fps=%.3f" % (t.strftime("%H:%M:%S"), dt, df, fps))
    for t, dt in gaps[:10]:
        print("  gap at=%s dt=%.2f" % (t.strftime("%H:%M:%S"), dt))
    rates = [p[2] for p in perfs]
    out1 = [p for p in perfs if abs(p[2] - 1.0) > 0.005]
    out0 = [p for p in perfs if p[2] != 1.0]
    print("RATE min=%.3f max=%.3f not1.000=%d gt0.5pct=%d" % (min(rates), max(rates), len(out0), len(out1)))
    for p in out1[:10]:
        print("  rate at=%s rate=%.3f elapsed=%.2f" % (p[0].strftime("%H:%M:%S"), p[2], p[1]))

print("ERRLINES count=%d" % len(errs))
for ts, msg in errs[:10]:
    print("  err at=%s %s" % (ts.strftime("%H:%M:%S"), msg[:160]))

def count_pattern(pat):
    rx = re.compile(pat, re.I)
    total, per = 0, {}
    names = ["app-stderr.txt", "app-stdout.txt"] + ([log_name] if log_name else [])
    for n in names:
        p = os.path.join(run, n)
        if not os.path.exists(p):
            continue
        c = sum(1 for line in io.open(p, encoding="utf-8", errors="replace") if rx.search(line))
        if c:
            per[n] = c
        total += c
    return total, per

for label, pat in (("RING-RECREATED", r"ring: recreated"), ("DEVICE-LOST", r"device removed|device lost|gpuDeviceLost")):
    total, per = count_pattern(pat)
    print("%s total=%d files=%s" % (label, total, per))

mem = []
p = os.path.join(run, "memory-samples.csv")
if os.path.exists(p):
    for i, line in enumerate(io.open(p, encoding="utf-8")):
        if i == 0:
            continue
        parts = line.strip().split(",")
        if len(parts) != 4:
            continue
        mem.append((datetime.strptime(parts[0][:19], "%Y-%m-%dT%H:%M:%S"), int(parts[2]), int(parts[3])))
print("MEMORY samples=%d" % len(mem))
if mem:
    five = min(mem, key=lambda r: abs((r[0] - (mem[0][0] + timedelta(seconds=300))).total_seconds()))
    ws = [r[1] for r in mem]
    pb = [r[2] for r in mem]
    ws_up = max((b - a for a, b in zip(ws, ws[1:])), default=0)
    pb_up = max((b - a for a, b in zip(pb, pb[1:])), default=0)
    print("  span=%s..%s (%.0fs)" % (mem[0][0].strftime("%H:%M:%S"), mem[-1][0].strftime("%H:%M:%S"),
                                     (mem[-1][0] - mem[0][0]).total_seconds()))
    print("  workingSet first=%.1fMiB fiveMin=%.1fMiB last=%.1fMiB delta5m..end=%+.1fMiB min=%.1fMiB max=%.1fMiB maxStepUp=%+.2fMiB" % (
        ws[0] / MIB, five[1] / MIB, ws[-1] / MIB, (ws[-1] - five[1]) / MIB, min(ws) / MIB, max(ws) / MIB, ws_up / MIB))
    print("  private    first=%.1fMiB fiveMin=%.1fMiB last=%.1fMiB delta5m..end=%+.1fMiB min=%.1fMiB max=%.1fMiB maxStepUp=%+.2fMiB" % (
        pb[0] / MIB, five[2] / MIB, pb[-1] / MIB, (pb[-1] - five[2]) / MIB, min(pb) / MIB, max(pb) / MIB, pb_up / MIB))

rs = read_json("receiver-samples.json", [])
alive = sum(1 for s in rs if s.get("alive"))
q = [s["qpc"] for s in rs if "qpc" in s]
maxgap = max((b - a for a, b in zip(q, q[1:])), default=0)
print("RECEIVER samples=%d alive=%d dead=%d maxQpcGap=%d (%.1fs at 10MHz)" % (
    len(rs), alive, len(rs) - alive, maxgap, maxgap / 1e7))
