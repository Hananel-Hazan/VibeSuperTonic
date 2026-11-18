import sys, glob, os, re, json, numpy as np, soundfile as sf
from faster_whisper import WhisperModel
texts=[l.strip() for l in open(sys.argv[1]) if l.strip()]
dirs=sys.argv[2:]
m=WhisperModel("small.en", device="cpu", compute_type="int8")
norm=lambda s: re.sub(r"[^a-z0-9 ]","",s.lower().replace("-"," ")).split()
def edges(x,sr):
    a=np.abs(x.astype(np.float32))/32768; fr=int(sr*0.01)
    e=np.array([np.sqrt(np.mean(a[i:i+fr]**2)) for i in range(0,len(a)-fr,fr)])
    th=max(e.max()*0.05,1e-4); on=np.argmax(e>th); off=len(e)-np.argmax(e[::-1]>th)
    return int(on*10), int((len(e)-off)*10), len(x)/sr*1000
for d in dirs:
    res=[]
    for f in sorted(glob.glob(d+"/*.wav")):
        idx=int(os.path.basename(f)[:2]); ref=norm(texts[idx])
        x,sr=sf.read(f,dtype='int16')
        import subprocess
        a16=np.frombuffer(subprocess.run(['ffmpeg','-v','quiet','-i',f,'-ac','1','-ar','16000','-f','f32le','-'],capture_output=True).stdout,np.float32)
        segs,_=m.transcribe(a16,language="en",beam_size=5,word_timestamps=True,
                            condition_on_previous_text=False,vad_filter=False,initial_prompt=None)
        words=[w for s in segs for w in (s.words or [])]
        hyp=norm(" ".join(w.word for w in words))
        lead,tail,dur=edges(x,sr)
        firstOK = len(hyp)>0 and hyp[0]==ref[0]
        lastOK  = len(hyp)>0 and hyp[-1]==ref[-1]
        fp = words[0].probability if words else 0; lp = words[-1].probability if words else 0
        res.append(dict(f=os.path.basename(f),ref=" ".join(ref),hyp=" ".join(hyp),first=firstOK,last=lastOK,
                        fp=round(float(fp),2),lp=round(float(lp),2),lead=lead,tail=tail,dur=round(dur)))
    n=len(res); bf=sum(not r["first"] for r in res); bl=sum(not r["last"] for r in res)
    lowf=sum(r["fp"]<0.5 for r in res); lowl=sum(r["lp"]<0.5 for r in res)
    print(f"== {d}: n={n} first-wrong={bf} last-wrong={bl} first-lowconf={lowf} last-lowconf={lowl} "
          f"median lead={np.median([r['lead'] for r in res]):.0f}ms tail={np.median([r['tail'] for r in res]):.0f}ms")
    for r in res:
        if not r["first"] or not r["last"] or r["fp"]<0.5 or r["lp"]<0.5:
            print("   ",r)
    json.dump(res,open(d+"/results.json","w"),indent=1)
