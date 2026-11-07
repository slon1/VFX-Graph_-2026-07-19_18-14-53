import sys, time, numpy as np
import sim, fastfield
from scipy.spatial import cKDTree
VAR={'base':(1.0,1.0,0.0),'t05':(0.5,0.5,0.0),'t05c':(0.5,0.25,0.0),'t05w075':(0.5,0.5,0.75),
     't05w1':(0.5,0.5,1.0),'t1w15':(1.0,1.0,1.5),'t025':(0.25,0.25,0.0),'t075':(0.75,0.75,0.0),'t067':(2/3,2/3,0.0),'t06':(0.6,0.6,0.0)}
name=sys.argv[1]; seed=int(sys.argv[2]); texel,r0,Rw=VAR[name]
fld=fastfield.Field(texel,r0)
def near_sep(P,H,Tm):
    n=len(P); tree=cKDTree(np.mod(P-sim.ORG,sim.W),boxsize=sim.W)
    cnt=np.array([len(x)-1 for x in tree.query_ball_point(np.mod(P-sim.ORG,sim.W),sim.R)],float)
    pr=tree.query_pairs(Rw,output_type='ndarray')
    if len(pr)==0: return np.zeros((n,2))
    i=np.r_[pr[:,0],pr[:,1]]; j=np.r_[pr[:,1],pr[:,0]]
    d=P[j]-P[i]; d=d-sim.W*np.round(d/sim.W); r2=(d**2).sum(1); k=r2>=1e-6
    i,j,d,r2=i[k],j[k],d[k],r2[k]
    mul=np.where(Tm[i]==Tm[j],1.0,sim.INTER)
    s=np.zeros((n,2)); np.add.at(s,i,(-d/(r2[:,None]+sim.EPS))*mul[:,None])
    return s/np.maximum(cnt,1)[:,None]*sim.WS
def stats(P,H,Tm):
    ang=np.degrees(np.arctan2(H[:,1],H[:,0]))
    near=np.minimum.reduce([np.abs(((ang-a+180)%360)-180) for a in (0,90,180,270)]); ah=np.mean(near<=5)
    tree=cKDTree(np.mod(P-sim.ORG,sim.W),boxsize=sim.W)
    nb=tree.query_ball_point(np.mod(P-sim.ORG,sim.W),sim.R)
    rho=[];th=[];la=[]
    for i,l in enumerate(nb):
        l=[j for j in l if Tm[j]==Tm[i]]
        rho.append((len(l)-1)/(np.pi*sim.R**2))
        if len(l)<6: continue
        d=P[l]-P[i]; d=d-sim.W*np.round(d/sim.W)
        ev,evec=np.linalg.eigh(np.cov(d.T)); t=np.sqrt(max(ev[0],0)/max(ev[1],1e-12)); th.append(t)
        if t<0.25:
            a=np.degrees(np.arctan2(evec[1,1],evec[0,1]))%90; la.append(min(a,90-a)<=10)
    cells=len(set(map(tuple,np.floor((P-sim.ORG)/1.0).astype(int))))
    return dict(thinMed=np.median(th),fracThin=np.mean(np.array(th)<0.25),cells=cells,rhoMed=np.median(rho),axisHead=ah,axisLine=(np.mean(la) if la else float('nan')))
P,H,Tm=sim.init(seed); dt=1/60; t0=time.time()
for st in range(901):
    if st in (300,600,900):
        m=stats(P,H,Tm); print(name,seed,'t=%2.0f'%(st/60),' '.join('%s=%.3f'%(k,v) for k,v in m.items()),flush=True)
    if st==900: break
    F=fld.force(P,H,Tm)
    if Rw>0: F=F+near_sep(P,H,Tm)
    H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
print(name,seed,'time',round(time.time()-t0,1),flush=True)
