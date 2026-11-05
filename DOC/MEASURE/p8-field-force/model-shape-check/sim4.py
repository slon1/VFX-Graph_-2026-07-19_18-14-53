import sys, time, numpy as np
import sim
from scipy.spatial import cKDTree
Rw=float(sys.argv[1]); seed=1
P,H,Tm=sim.init(seed); dt=1/60
def near_sep(P,H,Tm):
    n=len(P); tree=cKDTree(np.mod(P-sim.ORG,sim.W),boxsize=sim.W)
    cnt=np.array([len(x)-1 for x in tree.query_ball_point(np.mod(P-sim.ORG,sim.W),sim.R)],float)
    pr=tree.query_pairs(Rw,output_type='ndarray')
    i=np.r_[pr[:,0],pr[:,1]]; j=np.r_[pr[:,1],pr[:,0]]
    d=P[j]-P[i]; d=d-sim.W*np.round(d/sim.W); r2=(d**2).sum(1); k=r2>=1e-6
    i,j,d,r2=i[k],j[k],d[k],r2[k]
    mul=np.where(Tm[i]==Tm[j],1.0,sim.INTER)
    s=np.zeros((n,2)); np.add.at(s,i,(-d/(r2[:,None]+sim.EPS))*mul[:,None])
    return s/np.maximum(cnt,1)[:,None]*sim.WS
for st in range(301):
    if st in (120,300):
        m=sim.metrics(P,H,Tm); print('window Rw=%.1f'%Rw,'t=%.0f'%(st/60),{k:round(float(v),3) for k,v in m.items() if k in('rho_med','thin_med','frac_thin','occ_cells')},flush=True)
    if st==300: break
    F=sim.force_field(P,H,Tm)+(near_sep(P,H,Tm) if Rw>0 else 0)
    H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
