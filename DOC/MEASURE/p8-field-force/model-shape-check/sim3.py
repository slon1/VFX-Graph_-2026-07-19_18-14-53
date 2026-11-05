import sys, numpy as np
import sim
from scipy.spatial import cKDTree
rot=float(sys.argv[1]); seed=1
P,H,Tm=sim.init(seed)
c,s=np.cos(np.radians(rot)),np.sin(np.radians(rot)); Rm=np.array([[c,-s],[s,c]])
P=P@Rm.T; H=H@Rm.T
dt=1/60
def orient(P,H,Tm):
    tree=cKDTree(np.mod(P-sim.ORG,sim.W),boxsize=sim.W)
    nb=tree.query_ball_point(np.mod(P-sim.ORG,sim.W),sim.R)
    ang=[];hd=[]
    for i,l in enumerate(nb):
        l=[j for j in l if Tm[j]==Tm[i]]
        if len(l)<6: continue
        d=P[l]-P[i]; d=d-sim.W*np.round(d/sim.W)
        ev,evec=np.linalg.eigh(np.cov(d.T))
        if np.sqrt(max(ev[0],0)/max(ev[1],1e-12))<0.25:
            v=evec[:,1]; ang.append(np.degrees(np.arctan2(v[1],v[0]))%90)
            hd.append(np.degrees(np.arctan2(H[i,1],H[i,0]))%180)
    ang=np.array(ang); 
    near_axis=np.mean((ang<10)|(ang>80)); 
    # line direction vs own heading
    return len(ang), near_axis
for st in range(301):
    if st in (120,300):
        n,fa=orient(P,H,Tm); print('rot',rot,'t=%.0f'%(st/60),'thin particles',n,'line within 10deg of grid axes: %.2f (random=0.22)'%fa,flush=True)
    if st==300: break
    F=sim.force_field(P,H,Tm); H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
