import sys, numpy as np
import sim, fastfield, stats7
from scipy.spatial import cKDTree
name=sys.argv[1]; seed=int(sys.argv[2]); T_end=float(sys.argv[3]); TC=float(sys.argv[4]); Rw=float(sys.argv[5]); TF=float(sys.argv[6]); WP=float(sys.argv[7]); G0=float(sys.argv[8])
fld=fastfield.Field(TC,TC) if TC>0 else None
def near_sep(P,H,Tm):
    n=len(P); q=np.mod(P-sim.ORG,sim.W); tree=cKDTree(q,boxsize=sim.W)
    cnt=np.array([len(x)-1 for x in tree.query_ball_point(q,sim.R)],float) if fld is not None else None
    pr=tree.query_pairs(Rw,output_type='ndarray')
    if len(pr)==0: return np.zeros((n,2))
    i=np.r_[pr[:,0],pr[:,1]]; j=np.r_[pr[:,1],pr[:,0]]
    d=P[j]-P[i]; d=d-sim.W*np.round(d/sim.W); r2=(d**2).sum(1); k=r2>=1e-6
    i,j,d,r2=i[k],j[k],d[k],r2[k]
    mul=np.where(Tm[i]==Tm[j],1.0,sim.INTER)
    s=np.zeros((n,2)); np.add.at(s,i,(-d/(r2[:,None]+sim.EPS))*mul[:,None])
    return s/np.maximum(cnt,1)[:,None]*sim.WS
if TF>0: GF=int(round(90/TF))
def pressure(P,H,Tm):
    n=len(P); u=(P-sim.ORG)/TF; i0=np.floor(u).astype(int); f=u-i0
    cw=[(1-f[:,0])*(1-f[:,1]), f[:,0]*(1-f[:,1]), (1-f[:,0])*f[:,1], f[:,0]*f[:,1]]; cd=[(0,0),(1,0),(0,1),(1,1)]
    rho=np.zeros((2,GF*GF))
    for w,(dx,dz) in zip(cw,cd):
        idx=((i0[:,0]+dx)%GF)*GF+((i0[:,1]+dz)%GF)
        for a in range(2):
            m=Tm==a; rho[a]+=np.bincount(idx[m],w[m],GF*GF)
    rho=rho.reshape(2,GF,GF)/(TF*TF)
    rw=np.stack([rho[0]+sim.INTER*rho[1], rho[1]+sim.INTER*rho[0]])
    gx=(np.roll(rw,-1,axis=1)-np.roll(rw,1,axis=1))/(2*TF); gz=(np.roll(rw,-1,axis=2)-np.roll(rw,1,axis=2))/(2*TF)
    g=np.zeros((n,2))
    for w,(dx,dz) in zip(cw,cd):
        ix=(i0[:,0]+dx)%GF; iz=(i0[:,1]+dz)%GF
        g[:,0]+=w*gx[Tm,ix,iz]; g[:,1]+=w*gz[Tm,ix,iz]
    Wd={(0,0):cw[0],(1,0):cw[1],(0,1):cw[2],(1,1):cw[3]}
    def rs(i,j): return Wd.get((i,j),np.zeros(n))/(TF*TF)
    s=np.zeros((n,2))
    for w,(a,b) in zip(cw,cd):
        s[:,0]+=w*(rs(a+1,b)-rs(a-1,b))/(2*TF); s[:,1]+=w*(rs(a,b+1)-rs(a,b-1))/(2*TF)
    g-=s
    mag=np.linalg.norm(g,axis=1,keepdims=True)
    return -WP*g/(mag+G0)
P,H,Tm=sim.init(seed); dt=1/60
for st in range(int(T_end*60)):
    F=sim.force_exact(P,H,Tm) if fld is None else fld.force(P,H,Tm)
    if Rw>0: F=F+near_sep(P,H,Tm)
    if TF>0: F=F+pressure(P,H,Tm)
    H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
np.savez('pos_%s_%d.npz'%(name,seed),P=P,H=H,Tm=Tm)
m=stats7.stats(P,H,Tm); print(name,seed,' '.join('%s=%.3f'%(k,v) for k,v in m.items()),flush=True)
