import numpy as np, sys, time
from scipy.spatial import cKDTree

W=90.0; T=1.0; G=90; ORG=-45.0
R=3.0; WS,WA,WC=1.2,0.8,0.6; INTER=4.0; CRUISE=6.0; TURN=4.0
EPS=1e-3; R0=1.0
def init(seed, n=1500):
    rng=np.random.default_rng(seed)
    P=[];H=[];Tm=[]
    for team,(cx,dirx) in enumerate([(-22,1.0),(22,-1.0)]):
        r=12*np.sqrt(rng.random(n)); a=2*np.pi*rng.random(n)
        P.append(np.c_[cx+r*np.cos(a), r*np.sin(a)])
        H.append(np.tile([dirx,0.0],(n,1))); Tm+= [team]*n
    return np.vstack(P),np.vstack(H),np.array(Tm)

def wrap(p): return (p+W/2)%W-W/2

def unit(v,fb):
    l=np.linalg.norm(v,axis=1,keepdims=True)
    ok=l>1e-9
    return np.where(ok, v/np.maximum(l,1e-12), fb)

def step_heading(P,H,F,dt):
    desired=unit(F, H)
    k=min(TURN*dt,1.0)
    h=unit(H+(desired-H)*k, desired)
    return h

# ---- exact traversal ----
def force_exact(P,H,Tm):
    tree=cKDTree(np.mod(P-ORG,W),boxsize=W)
    pairs=tree.query_pairs(R,output_type='ndarray')
    i=np.r_[pairs[:,0],pairs[:,1]]; j=np.r_[pairs[:,1],pairs[:,0]]
    d=P[j]-P[i]; d=d-W*np.round(d/W)
    r2=(d**2).sum(1)
    keep=(r2>=1e-6)&(r2<R*R); i,j,d,r2=i[keep],j[keep],d[keep],r2[keep]
    same=Tm[i]==Tm[j]
    mul=np.where(same,1.0,INTER)
    n=len(P)
    sep=np.zeros((n,2)); 
    contrib=(-d/(r2[:,None]+EPS))*mul[:,None]
    np.add.at(sep,i,contrib)
    sepN=np.bincount(i,minlength=n).astype(float)
    al=np.zeros((n,2)); np.add.at(al,i[same],H[j[same]])
    co=np.zeros((n,2)); np.add.at(co,i[same],d[same]); cohN=np.bincount(i[same],minlength=n).astype(float)
    F=np.zeros((n,2))
    m=sepN>0; F[m]+=sep[m]/sepN[m,None]*WS
    la=(al**2).sum(1)>1e-6; F[la]+=al[la]/np.linalg.norm(al[la],axis=1,keepdims=True)*WA
    m=cohN>0; mean=np.zeros((n,2)); mean[m]=co[m]/cohN[m,None]
    lc=m&((mean**2).sum(1)>1e-6); F[lc]+=mean[lc]/np.linalg.norm(mean[lc],axis=1,keepdims=True)*WC
    return F

# ---- field path (float64 analogue of Scatter->Convolve->Gradient->Force) ----
offs=[(ox,oz) for ox in range(-3,4) for oz in range(-3,4) if ox*ox+oz*oz<9]
def psi(d):
    return 0.5*np.log((np.maximum(d,R0)**2+EPS)/(R*R+EPS))
def force_field(P,H,Tm):
    n=len(P)
    u=(P-ORG)/T; i0=np.floor(u).astype(int); f=u-i0
    cw=[(1-f[:,0])*(1-f[:,1]), f[:,0]*(1-f[:,1]), (1-f[:,0])*f[:,1], f[:,0]*f[:,1]]
    cd=[(0,0),(1,0),(0,1),(1,1)]
    N=np.zeros((2,G,G)); Hx=np.zeros((2,G,G)); Hz=np.zeros((2,G,G))
    for w,(dx,dz) in zip(cw,cd):
        ix=(i0[:,0]+dx)%G; iz=(i0[:,1]+dz)%G
        for a in range(2):
            m=Tm==a
            np.add.at(N[a],(ix[m],iz[m]),w[m])
            np.add.at(Hx[a],(ix[m],iz[m]),w[m]*H[m,0])
            np.add.at(Hz[a],(ix[m],iz[m]),w[m]*H[m,1])
    Ng=np.zeros((2,G,G)); A=np.zeros((2,2,G,G)); K=np.zeros((2,2,G,G)); Wp=np.zeros((2,G,G))
    for (ox,oz) in offs:
        sh=lambda X: np.roll(np.roll(X,-ox,axis=-2),-oz,axis=-1)  # value at c+o
        dd=np.hypot(ox,oz)*T; ps=psi(dd)
        for a in range(2):
            Na=sh(N[a]); Ng[a]+=Na
            A[a,0]+=sh(Hx[a]); A[a,1]+=sh(Hz[a])
            K[a,0]+=Na*ox*T; K[a,1]+=Na*oz*T
            Wp[a]+=Na*ps
    Ntot=N.sum(0)*0; Ntot=Ng[0]+Ng[1]
    Phi=np.zeros((2,G,G))
    Phi[0]=Wp[0]+INTER*Wp[1]; Phi[1]=Wp[1]+INTER*Wp[0]
    Gx=(np.roll(Phi,-1,axis=1)-np.roll(Phi,1,axis=1))/(2*T)
    Gz=(np.roll(Phi,-1,axis=2)-np.roll(Phi,1,axis=2))/(2*T)
    gs=np.zeros((n,2)); ns=np.zeros(n); al=np.zeros((n,2)); ng=np.zeros(n); ks=np.zeros((n,2))
    for w,(dx,dz) in zip(cw,cd):
        ix=(i0[:,0]+dx)%G; iz=(i0[:,1]+dz)%G
        a=Tm
        gs[:,0]+=w*Gx[a,ix,iz]; gs[:,1]+=w*Gz[a,ix,iz]
        ns+=w*Ntot[ix,iz]; ng+=w*Ng[a,ix,iz]
        al[:,0]+=w*A[a,0,ix,iz]; al[:,1]+=w*A[a,1,ix,iz]
        tp=np.c_[(dx-f[:,0])*T,(dz-f[:,1])*T]
        ks[:,0]+=w*(K[a,0,ix,iz]+Ng[a,ix,iz]*tp[:,0]); ks[:,1]+=w*(K[a,1,ix,iz]+Ng[a,ix,iz]*tp[:,1])
    ns-=1; al-=H; ng-=1
    F=np.zeros((n,2))
    m=ns>0.001; F[m]+=gs[m]/ns[m,None]*WS
    l=(al**2).sum(1)>1e-6; F[l]+=al[l]/np.linalg.norm(al[l],axis=1,keepdims=True)*WA
    m=ng>0.001; mean=np.zeros((n,2)); mean[m]=ks[m]/ng[m,None]
    l=m&((mean**2).sum(1)>1e-6); F[l]+=mean[l]/np.linalg.norm(mean[l],axis=1,keepdims=True)*WC
    return F

def metrics(P,H,Tm):
    tree=cKDTree(np.mod(P-ORG,W),boxsize=W)
    nb=tree.query_ball_point(np.mod(P-ORG,W),R)
    rho=[];ecc=[];
    for i,lst in enumerate(nb):
        lst=[j for j in lst if Tm[j]==Tm[i]]
        rho.append((len(lst)-1)/(np.pi*R*R))
        if len(lst)>=6:
            d=P[lst]-P[i]; d=d-W*np.round(d/W)
            c=np.cov(d.T); ev=np.linalg.eigvalsh(c)
            ecc.append(np.sqrt(max(ev[0],0)/max(ev[1],1e-12)))
    rho=np.array(rho); ecc=np.array(ecc)
    ang=np.degrees(np.arctan2(H[:,1],H[:,0]))%90
    ax=np.mean((ang<5)|(ang>85))
    # occupied area: texel cells occupied (1m) per team
    occ=len(set(map(tuple,np.floor((P-ORG)/T).astype(int))))
    return dict(rho_med=np.median(rho),rho_p90=np.percentile(rho,90),thin_med=np.median(ecc) if len(ecc) else np.nan,
                frac_thin=np.mean(ecc<0.25) if len(ecc) else np.nan, axis=ax, occ_cells=occ)

def run(kind,seed=1,T_end=10.0,fps=60):
    P,H,Tm=init(seed); dt=1/fps
    ff=force_exact if kind=='exact' else force_field
    out={}
    steps=int(T_end*fps)
    snaps={int(t*fps) for t in (0.0,2,5,10)}
    for s in range(steps+1):
        if s in snaps: out[s/fps]=metrics(P,H,Tm)
        if s==steps: break
        F=ff(P,H,Tm)
        H=step_heading(P,H,F,dt)
        P=wrap(P+H*CRUISE*dt)
    return out

if __name__=='__main__':
    kind=sys.argv[1]; seed=int(sys.argv[2]); t0=time.time()
    res=run(kind,seed)
    for t,m in res.items():
        print(kind,seed,'t=%.0f'%t,{k:(round(float(v),3)) for k,v in m.items()})
    print('time',round(time.time()-t0,1))
