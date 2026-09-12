# Summarize V1 matrix runs: per clip, source fps (from stream), distinct frames/sec vs expected, display/spout rates, compose p99, errors.
import io,json,sys,glob,os,collections,statistics,subprocess,re
root=sys.argv[1]; lo=float(sys.argv[2]) if len(sys.argv)>2 else 8; hi=float(sys.argv[3]) if len(sys.argv)>3 else 48
def probe_fps(path):
    try:
        out=subprocess.run(["ffprobe","-v","error","-select_streams","v:0","-show_entries","stream=r_frame_rate,codec_name,pix_fmt","-of","csv=p=0",path],capture_output=True,text=True,timeout=20).stdout.strip().split(",")
        codec,pix,fr=out[0],out[1],out[2]; n,d=fr.split("/"); return codec,pix,float(n)/float(d)
    except Exception as e: return "?","?",0.0
rows=[]
for run in sorted(glob.glob(os.path.join(root,"*-v1-*"))):
    try:
        rr=json.load(io.open(os.path.join(run,"runner-result.json"),encoding="utf-8-sig"))
        man=json.load(io.open(os.path.join(run,"app","manifest.json"),encoding="utf-8-sig")); f=man["qpcFrequency"]; o=man["originQpc"]
        ev=[json.loads(l) for l in io.open(os.path.join(run,"app","events.jsonl"),encoding="utf-8")]
    except Exception as e:
        rows.append((os.path.basename(run),"(no trace: %s)"%e)); continue
    t=lambda e:(e["qpc"]-o)/f
    codec,pix,fps=probe_fps(rr["media"])
    src=[e for e in ev if e.get("stage")=="source.acquire" and lo<=t(e)<hi]
    ids=collections.defaultdict(set); nr=collections.Counter()
    for e in src:
        if e.get("detail")=="Ready": ids[int(t(e))].add(e["imageId"])
        else: nr[int(t(e))]+=1
    secs=sorted(ids); dist=[len(ids[s]) for s in secs]
    pres=collections.Counter(int(t(e)) for e in ev if e.get("stage")=="present.scanout" and lo<=t(e)<hi)
    pub=collections.Counter(int(t(e)) for e in ev if e.get("stage")=="send.publish" and lo<=t(e)<hi)
    d={}
    for e in ev:
        if e.get("stage") in ("compose.start","compose.complete") and lo<=t(e)<hi: d.setdefault(e.get("scheduledQpc"),{})[e["stage"]]=e["qpc"]
    cm=sorted((v["compose.complete"]-v["compose.start"])*1000/f for v in d.values() if len(v)==2)
    errs=[e for e in ev if "error" in e.get("stage","")]
    diag=None
    try: diag=json.load(io.open(os.path.join(run,"app","summary.json"),encoding="utf-8-sig")).get("sourceDiagnostics") or json.load(io.open(os.path.join(run,"app","summary.json"),encoding="utf-8-sig")).get("source",{}).get("sourceDiagnostics")
    except Exception: pass
    dec=(diag or {}).get("decoder","?") if isinstance(diag,dict) else "?"
    exp=round(fps) if fps else None
    ok_src = exp is not None and secs and min(dist)>=exp-1 and max(dist)<=exp+1
    rows.append((os.path.basename(rr["media"]),codec,pix,round(fps,3),dec,"%d..%d"%(min(dist),max(dist)) if dist else "-",exp,round(min(pres.values()) if pres else 0),round(sum(pub.values())/max(1,len(pub)),1),"%.2f"%(cm[int(len(cm)*.99)] if cm else 0),len(errs),rr.get("appExit"),"OK" if ok_src and errs==[] and rr.get("appExit")==0 and (min(pres.values()) if pres else 0)>=59 else "CHECK"))
print("%-28s %-6s %-10s %-7s %-14s %-9s %-4s %-6s %-6s %-6s %-4s %-4s %s"%("clip","codec","pix","fps","decoder","dist/s","exp","minPr","spout","cp99","err","exit","verdict"))
for r in rows: print(" ".join(str(x).ljust(w) for x,w in zip(r,(28,6,10,7,14,9,4,6,6,6,4,4,6))))
