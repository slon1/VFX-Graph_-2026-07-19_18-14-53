import numpy as np
import sim
EPS=sim.EPS
class Field:
    def __init__(self, texel, r0):
        self.t=texel; self.G=int(round(sim.W/texel)); self.r0=r0
        G=self.G; k=int(np.ceil(sim.R/texel))+1
        M=np.zeros((G,G)); Kx=np.zeros((G,G)); Kz=np.zeros((G,G)); Pk=np.zeros((G,G))
        for ox in range(-k,k+1):
            for oz in range(-k,k+1):
                d=np.hypot(ox,oz)*texel
                if d<sim.R-1e-12:
                    M[ox%G,oz%G]=1; Kx[ox%G,oz%G]=ox*texel; Kz[ox%G,oz%G]=oz*texel
                    Pk[ox%G,oz%G]=0.5*np.log((max(d,r0)**2+EPS)/(sim.R**2+EPS))
        f=lambda a: np.conj(np.fft.rfft2(a))
        self.FM,self.FKx,self.FKz,self.FP=f(M),f(Kx),f(Kz),f(Pk)
    def corr(self,x,F): return np.fft.irfft2(np.fft.rfft2(x)*F,s=(self.G,self.G))
    def force(self,P,H,Tm):
        t,G=self.t,self.G; n=len(P)
        u=(P-sim.ORG)/t; i0=np.floor(u).astype(int); f=u-i0
        cw=[(1-f[:,0])*(1-f[:,1]), f[:,0]*(1-f[:,1]), (1-f[:,0])*f[:,1], f[:,0]*f[:,1]]
        cd=[(0,0),(1,0),(0,1),(1,1)]
        N=np.zeros((2,G*G)); Hx=np.zeros((2,G*G)); Hz=np.zeros((2,G*G))
        for w,(dx,dz) in zip(cw,cd):
            idx=((i0[:,0]+dx)%G)*G+((i0[:,1]+dz)%G)
            for a in range(2):
                m=Tm==a
                N[a]+=np.bincount(idx[m],w[m],G*G)
                Hx[a]+=np.bincount(idx[m],w[m]*H[m,0],G*G); Hz[a]+=np.bincount(idx[m],w[m]*H[m,1],G*G)
        N=N.reshape(2,G,G); Hx=Hx.reshape(2,G,G); Hz=Hz.reshape(2,G,G)
        Ng=np.stack([self.corr(N[a],self.FM) for a in range(2)])
        A=np.stack([[self.corr(Hx[a],self.FM),self.corr(Hz[a],self.FM)] for a in range(2)])
        K=np.stack([[self.corr(N[a],self.FKx),self.corr(N[a],self.FKz)] for a in range(2)])
        Wp=np.stack([self.corr(N[a],self.FP) for a in range(2)])
        Ntot=Ng[0]+Ng[1]
        Phi=np.stack([Wp[0]+sim.INTER*Wp[1], Wp[1]+sim.INTER*Wp[0]])
        Gx=(np.roll(Phi,-1,axis=1)-np.roll(Phi,1,axis=1))/(2*t)
        Gz=(np.roll(Phi,-1,axis=2)-np.roll(Phi,1,axis=2))/(2*t)
        gs=np.zeros((n,2)); ns=np.zeros(n); al=np.zeros((n,2)); ng=np.zeros(n); ks=np.zeros((n,2))
        for w,(dx,dz) in zip(cw,cd):
            ix=(i0[:,0]+dx)%G; iz=(i0[:,1]+dz)%G; a=Tm
            gs[:,0]+=w*Gx[a,ix,iz]; gs[:,1]+=w*Gz[a,ix,iz]
            ns+=w*Ntot[ix,iz]; ng+=w*Ng[a,ix,iz]
            al[:,0]+=w*A[a,0,ix,iz]; al[:,1]+=w*A[a,1,ix,iz]
            tp0=(dx-f[:,0])*t; tp1=(dz-f[:,1])*t
            ks[:,0]+=w*(K[a,0,ix,iz]+Ng[a,ix,iz]*tp0); ks[:,1]+=w*(K[a,1,ix,iz]+Ng[a,ix,iz]*tp1)
        ns-=1; al-=H; ng-=1
        F=np.zeros((n,2))
        m=ns>0.001; F[m]+=gs[m]/ns[m,None]*sim.WS
        l=(al**2).sum(1)>1e-6; F[l]+=al[l]/np.linalg.norm(al[l],axis=1,keepdims=True)*sim.WA
        m=ng>0.001; mean=np.zeros((n,2)); mean[m]=ks[m]/ng[m,None]
        l=m&((mean**2).sum(1)>1e-6); F[l]+=mean[l]/np.linalg.norm(mean[l],axis=1,keepdims=True)*sim.WC
        return F
if __name__=='__main__':
    P,H,Tm=sim.init(1)
    # evolve a few steps with the slow field to get a clumped state, then compare
    for _ in range(120):
        F=sim.force_field(P,H,Tm); H=sim.step_heading(P,H,F,1/60); P=sim.wrap(P+H*sim.CRUISE/60)
    F1=sim.force_field(P,H,Tm); F2=Field(1.0,1.0).force(P,H,Tm)
    print('max |F_slow - F_fft| texel 1:',np.abs(F1-F2).max())
    import time; fl=Field(0.5,0.5); t0=time.time(); fl.force(P,H,Tm); print('texel .5 step time',round(time.time()-t0,3))
    sim.T=0.5; sim.G=180; sim.R0=0.5; sim.offs=[(ox,oz) for ox in range(-7,8) for oz in range(-7,8) if (ox*ox+oz*oz)*0.25<9]
    F3=sim.force_field(P,H,Tm); F4=fl.force(P,H,Tm); print('max diff texel .5:',np.abs(F3-F4).max())
