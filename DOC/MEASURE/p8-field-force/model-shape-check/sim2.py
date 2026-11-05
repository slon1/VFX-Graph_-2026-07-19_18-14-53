import sys, time, numpy as np
import sim
variant=sys.argv[1]; seed=int(sys.argv[2]); T_end=float(sys.argv[3])
W0=(sim.WS,sim.WA,sim.WC)
def with_w(fn,ws,wa,wc):
    sim.WS,sim.WA,sim.WC=ws,wa,wc
    try: return fn()
    finally: sim.WS,sim.WA,sim.WC=W0
def set_fine(t):
    sim.T=t; sim.G=int(round(90/t)); sim.R0=t
    k=int(np.ceil(3/t))+1
    sim.offs=[(ox,oz) for ox in range(-k,k+1) for oz in range(-k,k+1) if (ox*ox+oz*oz)*t*t<9]
if variant=='field_fine': set_fine(0.5)
if variant=='field_vfine': set_fine(0.25)
if variant=='field_coreR0_small': sim.R0=0.25   # same texel, smaller flat core
def ff(P,H,Tm):
    if variant=='hyb_sepexact':
        return with_w(lambda: sim.force_exact(P,H,Tm),W0[0],0,0)+with_w(lambda: sim.force_field(P,H,Tm),0,W0[1],W0[2])
    if variant=='hyb_acexact':
        return with_w(lambda: sim.force_field(P,H,Tm),W0[0],0,0)+with_w(lambda: sim.force_exact(P,H,Tm),0,W0[1],W0[2])
    if variant=='exact': return sim.force_exact(P,H,Tm)
    return sim.force_field(P,H,Tm)
P,H,Tm=sim.init(seed); dt=1/60; t0=time.time()
snaps={int(t*60) for t in (2,5,10) if t<=T_end}
steps=int(T_end*60)
for s in range(steps+1):
    if s in snaps:
        m=sim.metrics(P,H,Tm)
        print(variant,seed,'t=%.0f'%(s/60),{k:round(float(v),3) for k,v in m.items() if k in('rho_med','thin_med','frac_thin','occ_cells')},flush=True)
    if s==steps: break
    F=ff(P,H,Tm); H=sim.step_heading(P,H,F,dt); P=sim.wrap(P+H*sim.CRUISE*dt)
print(variant,seed,'time',round(time.time()-t0,1))
