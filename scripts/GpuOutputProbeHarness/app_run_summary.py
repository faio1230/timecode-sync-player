# One-shot summary for a main-app GPU run: delivery (gst), display, Spout, compose, lead, late presents.
import io,json,sys,collections,statistics,subprocess,os
run=sys.argv[1]; lo=float(sys.argv[2]); hi=float(sys.argv[3])
man=json.load(io.open(run+"/app/manifest.json",encoding="utf-8-sig")); f=man["qpcFrequency"]; o=man["originQpc"]
ev=[json.loads(l) for l in io.open(run+"/app/events.jsonl",encoding="utf-8")]; ev.sort(key=lambda e:e["qpc"])
t=lambda e:(e["qpc"]-o)/f
W=lambda st:[e for e in ev if e.get("stage")==st and lo<=t(e)<hi]
def stats(v): v=sorted(v); return "n=%d mean=%.2f p99=%.2f max=%.2f"%(len(v),statistics.mean(v),v[int(len(v)*.99)],v[-1]) if v else "n=0"
def pairs(a,b,worker=None):
    d={}
    for e in ev:
        if e.get("stage") in (a,b) and (worker is None or e.get("worker")==worker) and lo<=t(e)<hi: d.setdefault((e.get("worker"),e.get("scheduledQpc")),{})[e["stage"]]=e["qpc"]
    return [(v[b]-v[a])*1000/f for v in d.values() if a in v and b in v and v[b]>=v[a]]
secs=range(int(lo),int(hi))
src=W("source.acquire"); ready=[e for e in src if e.get("detail")=="Ready"]
ids=collections.defaultdict(set); nr=collections.Counter()
for e in src:
    (ids[int(t(e))].add(e["imageId"]) if e.get("detail")=="Ready" else nr.__setitem__(int(t(e)),nr[int(t(e))]+1))
print("distinct/sec:",[len(ids[s]) for s in secs]); print("NotReady/sec:",[nr.get(s,0) for s in secs])
print("id deltas:",dict(collections.Counter(b["imageId"]-a["imageId"] for a,b in zip(ready,ready[1:]))))
dl=[e for e in ev if e.get("stage")=="gst.delivery"]
if dl:
    arr={e["imageId"]:e["qpc"] for e in dl}; age=collections.defaultdict(list)
    for e in ready:
        q=arr.get(e["imageId"]); 
        if q: age[int(t(e))].append((e["qpc"]-q)*1000/f)
    print("arrival->acquire age mean/sec:",[round(statistics.mean(age[s]),1) if age[s] else None for s in secs])
    print("replaced flags:",sum(int(e["detail"].split(":")[2])&1 for e in dl))
pres=collections.Counter(int(t(e)) for e in W("present.scanout")); print("present/sec:",[pres.get(s,0) for s in secs])
pub=collections.Counter(int(t(e)) for e in W("send.publish")); print("send.publish/sec:",[pub.get(s,0) for s in secs])
print("compose start->complete:",stats(pairs("compose.start","compose.complete")))
c=collections.defaultdict(list)
for e in ev:
    pass
d={}
for e in ev:
    if e.get("stage") in ("compose.start","compose.complete"): d.setdefault(e.get("scheduledQpc"),{})[e["stage"]]=e["qpc"]
cm=collections.defaultdict(list)
for v in d.values():
    if len(v)==2: cm[int((v["compose.start"]-o)/f)].append((v["compose.complete"]-v["compose.start"])*1000/f)
print("compose max/sec:",[round(max(cm[s]),1) if cm[s] else None for s in secs])
ps=W("present.start"); late=[b for a,b in zip(ps,ps[1:]) if (b["qpc"]-a["qpc"])*1000/f>25]
print("late presents (>25ms):",len(late))
print("lead:",[(e["detail"].split(":")[1],round(t(e),1)) for e in ev if e.get("stage")=="lifecycle" and e.get("detail","").startswith("composeLead")])
print("skips:",dict(collections.Counter(e.get("detail") for e in W("skip"))))
print("errors:",len([e for e in ev if "error" in e.get("stage","")]),"abandoned:",len([e for e in ev if "abandon" in json.dumps(e).lower()]))
