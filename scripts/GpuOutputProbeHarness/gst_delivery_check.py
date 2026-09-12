# Per-run check for problem H (GStreamer delivery): distinct frames/s, NotReady, shim replaced, arrival intervals, arrival->acquire age trend.
import io,json,collections,statistics,sys
run=sys.argv[1]; lo=float(sys.argv[2]) if len(sys.argv)>2 else 0; hi=float(sys.argv[3]) if len(sys.argv)>3 else 1e9
man=json.load(io.open(run+"/app/manifest.json",encoding="utf-8-sig")); f=man["qpcFrequency"]; o=man["originQpc"]
ev=[json.loads(l) for l in io.open(run+"/app/events.jsonl",encoding="utf-8")]
t=lambda e:(e["qpc"]-o)/f
src=[e for e in ev if e.get("stage")=="source.acquire" and lo<=t(e)<hi]
ready=[e for e in src if e.get("detail")=="Ready"]
ids=collections.defaultdict(set); nr=collections.Counter()
for e in src:
    if e.get("detail")=="Ready": ids[int(t(e))].add(e["imageId"])
    else: nr[int(t(e))]+=1
secs=sorted(ids)
print("distinct/sec:",[len(ids[s]) for s in secs])
print("NotReady/sec:",[nr.get(s,0) for s in secs])
d=collections.Counter(b["imageId"]-a["imageId"] for a,b in zip(ready,ready[1:])); print("id deltas:",dict(d))
dl=[e for e in ev if e.get("stage")=="gst.delivery"]
print("gst.delivery events:",len(dl))
if dl:
    iv=[(b["qpc"]-a["qpc"])*1000/f for a,b in zip(dl,dl[1:]) if lo<=t(b)<hi]; iv.sort()
    if iv: print("arrival interval ms: mean %.2f p1 %.2f p99 %.2f max %.2f"%(statistics.mean(iv),iv[int(len(iv)*0.01)],iv[int(len(iv)*0.99)],iv[-1]))
    flags=collections.Counter(int(e["detail"].split(":")[2]) & 1 for e in dl); print("replaced flag counts:",dict(flags))
    cb=[e["value"] for e in dl]; cb.sort(); print("callback us: p99 %d max %d"%(cb[int(len(cb)*0.99)],cb[-1]))
    arr={e["imageId"]:e["qpc"] for e in dl}
    age=collections.defaultdict(list)
    for e in ready:
        q=arr.get(e["imageId"])
        if q: age[int(t(e))].append((e["qpc"]-q)*1000/f)
    print("arrival->acquire age mean ms/sec:",[round(statistics.mean(age[s]),1) for s in sorted(age)])
    # queue depth proxy: arrivals before acquire minus acquired
    print("age max ms/sec:",[round(max(age[s]),1) for s in sorted(age)])
diag=[e for e in ev if e.get("stage")=="lifecycle"]; print("lifecycle:",[(e.get("detail"),round(t(e),1)) for e in diag if not e.get("detail","").startswith("composeLead")])
pres=collections.Counter(int(t(e)) for e in ev if e.get("stage")=="present.scanout"); print("present/sec:",[pres.get(s,0) for s in secs])
print("errors:",len([e for e in ev if "error" in e.get("stage","")]))
