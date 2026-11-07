import sys, numpy as np
import sim
from scipy.spatial import cKDTree
rot=float(sys.argv[1]); seed=int(sys.argv[2])
P,H,Tm=sim.init(seed)
c,s=np.cos(np.radians(rot)),np.sin(np.radians(rot)); Rm=np.array([[c,-s],[s,c]])
P=sim.wrap(P@Rm.T); H=H@Rm.T; dt=1/60
def stats(P,H,Tm):
    ang=np.degrees(np.arctan2(H[:,1],H[:,0]))
    near=np.minimum.reduce([np.abs(((ang-a+180)%360)-180) for a in (0,90,180,270)])
    ah=np.mean(near<=5)
    tree=cKDTree(np.mod(P-sim.ORG,sim.W),boxsize=sim.W)
    nb=tree.query_ball_point(np.mod(P-sim.ORG,sim.W),sim.R)
    la=[];th=[]
    for i,l in enumerate(nb):
        l=[j for j in l if Tm[j]==Tm[i]]
        if len(l)<6: continue
        d=P[l]-P[i]; d=d-sim.W*np.round(d/sim.W)
        ev,evec=np.linalg.eigh(np.cov(d.T)); t=np.sqrt(max(ev[0],0)/max(ev[1],1e-12))
        th.append(t)
        if t<0.25:
            v=evec[:,1]; a=np.degrees(np.arctan2(v[1],v[0]))%90; la.append(min(a,90-a)<=10)
    return ah,(np.mean(la) if la else float('nan')),np.mean(np.array(th)<0.25)
for st in range(901):
    if st in (600,900):
        ah,al,ft=stats(P,H,Tm); print('rot %2.0f seed %d t=%2.0f axisHead %.2f axisLine %.2f fracThin %.2f'%(rot,seed,st/60,ah,al,ft),flush=True)
    if st==900: break
    F=sim.force_field(P,H,Tm); H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
