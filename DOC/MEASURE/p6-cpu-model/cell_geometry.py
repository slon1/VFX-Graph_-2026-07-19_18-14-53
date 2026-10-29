#!/usr/bin/env python3
"""Cell-geometry estimate for the neighbor walk (ADR-055 background, F5 follow-up).

Question: if the neighbor search skipped cells that cannot contain neighbors, or used smaller cells,
how many candidates would be read per particle, and what share of them lies inside the radius R?

Model (an ESTIMATE, not a measurement):
  - uniform particle density, 2D torus ignored, one radius R = 1;
  - the particle is uniform inside its own cell of size s;
  - 'cull' = skip a neighbour cell when the minimal distance from the particle to that cell is >= R;
  - candidate area = number of visited cells * s^2 (in R^2 units); accepted share = pi * R^2 / candidate area.
Equivalent cap (same number of ACCEPTED neighbours as cap 192 on the current grid):
  cap_eq = 192 * share_now / share_scheme.   This assumes behaviour is set by the accepted count
  (ADR-052 section 1.1: accepted-48 behaves like visited-128, both ~45 accepted).
Run:  python3 cell_geometry.py   (seed fixed, ~1 minute)
"""
import math, random
random.seed(1)
R = 1.0
CAP = 192
CYC_PER_SLOT = 99.90 / 192.0   # walkCost_cyc at cap 192 (F5-report) per unit of cap (cost is cap-bound: 97-99.8% of walks hit the cap)


def candidate_area(s, span, cull, trials):
    tot = 0.0
    for _ in range(trials):
        x, y = random.random() * s, random.random() * s
        n = 0
        for ix in range(-span, span + 1):
            for iy in range(-span, span + 1):
                if cull:
                    dx = max(ix * s - x, 0, x - (ix + 1) * s)
                    dy = max(iy * s - y, 0, y - (iy + 1) * s)
                    if dx * dx + dy * dy >= R * R:
                        continue
                n += 1
        tot += n * s * s
    return tot / trials


schemes = [("3x3, cell=R (now)", 1.0, 1, False, 150000),
           ("3x3, cell=R, skip cells beyond R", 1.0, 1, True, 150000),
           ("5x5, cell=R/2", 0.5, 2, False, 150000),
           ("5x5, cell=R/2, skip cells beyond R", 0.5, 2, True, 150000)]
circle = math.pi * R * R
base = None
print(f"{'scheme':<38}{'candidate area':>15}{'accepted share':>16}{'candidates':>12}{'cap_eq':>8}{'walk cyc':>10}")
for name, s, span, cull, trials in schemes:
    a = candidate_area(s, span, cull, trials)
    base = base or a
    share, share0 = circle / a, circle / base
    cap_eq = CAP * share0 / share
    print(f"{name:<38}{a:15.2f}{100*share:15.1f}%{100*a/base:11.0f}%{cap_eq:8.0f}{cap_eq*CYC_PER_SLOT:10.1f}")
print("walk cyc = cap_eq * (99.9/192) cycles per particle; excludes the cost of extra cell ranges and branches.")
