import numpy as np, sim
from scipy.spatial import cKDTree
def stats(P,H,Tm):
    W=sim.W; R=sim.R
    ang=np.degrees(np.arctan2(H[:,1],H[:,0]))
    near=np.minimum.reduce([np.abs(((ang-a+180)%360)-180) for a in (0,90,180,270)]); ah=np.mean(near<=5)
    q=np.mod(P-sim.ORG,W); tree=cKDTree(q,boxsize=W)
    nb=tree.query_ball_point(q,R)
    rho=[];th=[];wid=[];la=[]
    for i,l in enumerate(nb):
        l=[j for j in l if Tm[j]==Tm[i]]
        rho.append((len(l)-1)/(np.pi*R*R))
        if len(l)<6: continue
        d=P[l]-P[i]; d=d-W*np.round(d/W)
        ev,evec=np.linalg.eigh(np.cov(d.T)); th.append(np.sqrt(max(ev[0],0)/max(ev[1],1e-12))); wid.append(np.sqrt(max(ev[0],0)))
        if th[-1]<0.25:
            a=np.degrees(np.arctan2(evec[1,1],evec[0,1]))%90; la.append(min(a,90-a)<=10)
    dd,_=tree.query(q,k=2); nn=dd[:,1]
    cells=len(set(map(tuple,np.floor((P-sim.ORG)/1.0).astype(int))))
    return dict(fracThin=np.mean(np.array(th)<0.25),thinMed=np.median(th),widthMed=np.median(wid),nnMed=np.median(nn),stack=np.mean(nn<0.05),cells=cells,rho=np.median(rho),axH=ah,axL=(np.mean(la) if la else float('nan')))
if __name__=='__main__':
    for n in ('exact','base','t06','t05','t05w1','p2','p3'):
        d=np.load('pos_%s_1.npz'%n); m=stats(d['P'],d['H'],d['Tm'])
        print('%-6s'%n,' '.join('%s=%.3f'%(k,v) for k,v in m.items()))
