// Field-vs-exact prototype for Boids_hash_2teams. Same constants, init, steer and metrics as boids_model.cpp.
// mode 0: exact, uncapped, all rules from particle pairs (reference)
// mode 1: field for separation + alignment + cohesion (CIC splat, disc convolution, bilinear read, leave-one-out)
// mode 2: field for alignment + cohesion, exact uncapped separation
// mode 3: like 1, but without leave-one-out (to see what self-exclusion buys)
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <vector>
#include <random>
#include <algorithm>
#include <numeric>
using namespace std;

static const float S = 90.f, HALF = 45.f, CELL = 3.f;
static const int RES = 30;
struct P { float x, z, hx, hz; int team; };
static float minimg(float d) { return d - S * roundf(d / S); }
static int cellc(float v) { int c = (int)floorf((v + HALF) / CELL); return ((c % RES) + RES) % RES; }
static void unitOr(float &x, float &z, float fx, float fz) {
    float l = x * x + z * z;
    if (l > 1e-12f) { float inv = 1.f / sqrtf(l); x *= inv; z *= inv; } else { x = fx; z = fz; }
}
static int wrapi(int a, int G) { return ((a % G) + G) % G; }

int main(int argc, char **argv) {
    int mode = atoi(argv[1]);
    int G = atoi(argv[2]);
    unsigned seed = atoi(argv[3]);
    float T = atof(argv[4]); float r0 = argc > 5 ? atof(argv[5]) : 0.75f; int Gn = 180, kst = 3, ownEx = 1;
    if (mode == 5) { Gn = atoi(argv[5]); kst = atoi(argv[6]); ownEx = atoi(argv[7]); r0 = kst * (S / Gn); }
    const float dt = 1.f / 60.f, mult = 4.f, discR = 12.f;
    const int perTeam = 1500;
    const float R = 3.f, wS = 1.2f, wA = 0.8f, wC = 0.6f, cruise = 6.f, turn = 4.f;
    const float tx = S / G; // texel size
    mt19937 rng(seed);
    uniform_real_distribution<double> U(0, 1);
    vector<P> p;
    for (int t = 0; t < 2; t++) {
        float cx = t == 0 ? -22.f : 22.f, hx = t == 0 ? 1.f : -1.f;
        for (int i = 0; i < perTeam; i++) {
            double r = discR * sqrt(U(rng)), a = U(rng) * 6.283185307;
            p.push_back({cx + (float)(r * cos(a)), (float)(r * sin(a)), hx, 0.f, t});
        }
    }
    int N = p.size();
    vector<vector<int>> cells(RES * RES);
    int RES2 = (int)floorf(S / r0); float cell2 = S / RES2; vector<vector<int>> cells2((size_t)RES2 * RES2);
    auto cellc2 = [&](float v) { int c = (int)floorf((v + HALF) / cell2); return ((c % RES2) + RES2) % RES2; };
    double lastVis = 0, lastVis3 = 0; int lastVisMax = 0;
    vector<float> nN[2], sxN[2], szN[2]; vector<vector<int>> listN;
    if (mode == 5) { for (int g = 0; g < 2; g++) { nN[g].assign((size_t)Gn * Gn, 0.f); sxN[g].assign((size_t)Gn * Gn, 0.f); szN[g].assign((size_t)Gn * Gn, 0.f); } listN.resize((size_t)Gn * Gn); }
    vector<float> fx(N), fz(N), phx(N, 0.f), phz(N, 0.f);
    int steps = (int)(T / dt);
    auto stepOf = [&](float t) { return (int)floorf(t / dt + 0.5f); };
    int snaps[3] = {stepOf(1.f), stepOf(5.f), stepOf(15.f)};

    // disc offsets (texel units)
    struct Off { int ox, oz; float kx, kz; bool far; };
    vector<Off> offs;
    int rr = (int)ceilf(R / tx) + 1;
    for (int oz = -rr; oz <= rr; oz++) for (int ox = -rr; ox <= rr; ox++) {
        float dx = ox * tx, dz = oz * tx, d2 = dx * dx + dz * dz;
        if (d2 >= R * R) continue;
        float inv = 1.f / (d2 + 1e-3f);
        offs.push_back({ox, oz, -dx * inv, -dz * inv, !((mode == 4 || mode == 5) && d2 < r0 * r0)});
    }
    auto kvAt = [&](int ox, int oz, float &kx, float &kz) -> bool {
        float dx = ox * tx, dz = oz * tx, d2 = dx * dx + dz * dz;
        if (d2 >= R * R || ((mode == 4 || mode == 5) && d2 < r0 * r0)) return false;
        float inv = 1.f / (d2 + 1e-3f);
        kx = -dx * inv; kz = -dz * inv; return true;
    };
    int GG = G * G;
    vector<float> n[2], hx_[2], hz_[2];
    vector<float> Vx[2], Vz[2], Ntot, A_x[2], A_z[2], Kx[2], Kz[2], Ng[2];
    for (int g = 0; g < 2; g++) {
        n[g].resize(GG); hx_[g].resize(GG); hz_[g].resize(GG);
        Vx[g].resize(GG); Vz[g].resize(GG); A_x[g].resize(GG); A_z[g].resize(GG);
        Kx[g].resize(GG); Kz[g].resize(GG); Ng[g].resize(GG);
    }
    Ntot.resize(GG);

    for (int s = 0; s <= steps; s++) {
        for (auto &c : cells) c.clear();
        for (int i = 0; i < N; i++) cells[cellc(p[i].z) * RES + cellc(p[i].x)].push_back(i);
        if (mode == 5) {
            float tn_ = S / Gn;
            for (int g = 0; g < 2; g++) { fill(nN[g].begin(), nN[g].end(), 0.f); fill(sxN[g].begin(), sxN[g].end(), 0.f); fill(szN[g].begin(), szN[g].end(), 0.f); }
            for (auto &l : listN) l.clear();
            for (int i = 0; i < N; i++) {
                float gx = p[i].x + HALF, gz = p[i].z + HALF;
                int ixn = (int)floorf(gx / tn_), izn = (int)floorf(gz / tn_); if (ixn >= Gn) ixn = Gn - 1; if (izn >= Gn) izn = Gn - 1;
                size_t idx = (size_t)izn * Gn + ixn; int g = p[i].team;
                nN[g][idx] += 1.f; sxN[g][idx] += gx - (ixn + 0.5f) * tn_; szN[g][idx] += gz - (izn + 0.5f) * tn_; listN[idx].push_back(i);
            }
        }
        if (mode == 4) { for (auto &c : cells2) c.clear(); for (int i = 0; i < N; i++) cells2[cellc2(p[i].z) * RES2 + cellc2(p[i].x)].push_back(i); }

        if (s == snaps[0] || s == snaps[1] || s == snaps[2]) {
            int cross = 0; long enemy = 0, tot = 0; double polSum = 0; int polN = 0;
            vector<int> uf(N); iota(uf.begin(), uf.end(), 0);
            auto find = [&](int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; };
            for (int i = 0; i < N; i++) {
                int cx = cellc(p[i].x), cz = cellc(p[i].z); bool hasEnemy = false; float lhx = 0, lhz = 0; int lc = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                    auto &c = cells[((cz + dz + RES) % RES) * RES + (cx + dx + RES) % RES];
                    for (int j : c) {
                        if (j == i) continue;
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z);
                        if (ddx * ddx + ddz * ddz >= R * R) continue;
                        tot++;
                        if (p[j].team != p[i].team) { enemy++; hasEnemy = true; }
                        else { uf[find(i)] = find(j); lhx += p[j].hx; lhz += p[j].hz; lc++; }
                    }
                }
                if (lc > 0) { polSum += sqrtf(lhx * lhx + lhz * lhz) / lc; polN++; }
                if (hasEnemy) cross++;
            }
            vector<int> sz(N, 0); for (int i = 0; i < N; i++) sz[find(i)]++;
            int fl[2] = {0, 0};
            for (int i = 0; i < N; i++) if (find(i) == i && sz[i] >= 30) fl[p[i].team]++;
            double tm = 0, tq = 0; for (int i = 0; i < N; i++) { double cr = phx[i] * p[i].hz - phz[i] * p[i].hx, dt_ = phx[i] * p[i].hx + phz[i] * p[i].hz; double ang = fabs(atan2(cr, dt_)); tm += ang; tq += ang * ang; } tm /= N; double tsd = sqrt(fmax(0.0, tq / N - tm * tm));
            printf("t=%.0f contact=%.4f enemy=%.4f flocks=%d,%d pol=%.4f turn=%.5f turnSd=%.5f vis=%.1f vis3=%.1f visMax=%d\n", s * dt, 100.f * cross / N, tot ? 100.f * enemy / tot : 0.f, fl[0], fl[1], polN ? polSum / polN : 0.0, tm, tsd, lastVis, lastVis3, lastVisMax);
        }
        if (s == steps) break;

        bool useField = mode != 0;
        bool leaveOneOut = mode != 3;
        if (useField) {
            for (int g = 0; g < 2; g++) { fill(n[g].begin(), n[g].end(), 0.f); fill(hx_[g].begin(), hx_[g].end(), 0.f); fill(hz_[g].begin(), hz_[g].end(), 0.f); }
            for (int i = 0; i < N; i++) {
                float u = (p[i].x + HALF) / tx - 0.5f, v = (p[i].z + HALF) / tx - 0.5f;
                int ix = (int)floorf(u), iz = (int)floorf(v); float fxr = u - ix, fzr = v - iz;
                for (int dz = 0; dz < 2; dz++) for (int dx = 0; dx < 2; dx++) {
                    float w = (dx ? fxr : 1.f - fxr) * (dz ? fzr : 1.f - fzr);
                    int idx = wrapi(iz + dz, G) * G + wrapi(ix + dx, G);
                    n[p[i].team][idx] += w; hx_[p[i].team][idx] += w * p[i].hx; hz_[p[i].team][idx] += w * p[i].hz;
                }
            }
            for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                int c = cz * G + cx;
                float v0x = 0, v0z = 0, v1x = 0, v1z = 0, nt = 0;
                float ax[2] = {0, 0}, az[2] = {0, 0}, kx[2] = {0, 0}, kz[2] = {0, 0}, ng[2] = {0, 0};
                for (auto &o : offs) {
                    int idx = wrapi(cz + o.oz, G) * G + wrapi(cx + o.ox, G);
                    float n0 = n[0][idx], n1 = n[1][idx];
                    // target team 0: own weight 1, foreign mult; target team 1 mirrored
                    if (o.far) { v0x += (n0 + mult * n1) * o.kx; v0z += (n0 + mult * n1) * o.kz;
                    v1x += (mult * n0 + n1) * o.kx; v1z += (mult * n0 + n1) * o.kz; }
                    nt += n0 + n1;
                    for (int g = 0; g < 2; g++) {
                        ax[g] += hx_[g][idx]; az[g] += hz_[g][idx];
                        float ng_ = n[g][idx];
                        kx[g] += ng_ * o.ox * tx; kz[g] += ng_ * o.oz * tx; ng[g] += ng_;
                    }
                }
                Vx[0][c] = v0x; Vz[0][c] = v0z; Vx[1][c] = v1x; Vz[1][c] = v1z; Ntot[c] = nt;
                for (int g = 0; g < 2; g++) { A_x[g][c] = ax[g]; A_z[g][c] = az[g]; Kx[g][c] = kx[g]; Kz[g][c] = kz[g]; Ng[g][c] = ng[g]; }
            }
        }

        long long visSum = 0, vis3Sum = 0; int visMx = 0;
        for (int i = 0; i < N; i++) {
            float sx = 0, sz = 0, ax = 0, az = 0, kx = 0, kz = 0; float sN = 0, cN = 0;
            int a = p[i].team;
            bool exactSep = (mode == 0 || mode == 2), exactAC = (mode == 0);
            if (exactSep || exactAC) {
                int cx = cellc(p[i].x), cz = cellc(p[i].z);
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                    auto &c = cells[((cz + dz + RES) % RES) * RES + (cx + dx + RES) % RES];
                    for (int j : c) {
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z);
                        float r2 = ddx * ddx + ddz * ddz;
                        if (r2 < 1e-6f || r2 >= R * R) continue;
                        bool same = p[j].team == a; float m = same ? 1.f : mult; float inv = 1.f / (r2 + 1e-3f);
                        if (exactSep) { sx += -ddx * inv * m; sz += -ddz * inv * m; sN += 1; }
                        if (exactAC && same) { ax += p[j].hx; az += p[j].hz; kx += ddx; kz += ddz; cN += 1; }
                    }
                }
            }
            if (useField) {
                float u = (p[i].x + HALF) / tx - 0.5f, v = (p[i].z + HALF) / tx - 0.5f;
                int ix = (int)floorf(u), iz = (int)floorf(v); float fxr = u - ix, fzr = v - iz;
                float w4[2][2]; for (int dz = 0; dz < 2; dz++) for (int dx = 0; dx < 2; dx++) w4[dz][dx] = (dx ? fxr : 1.f - fxr) * (dz ? fzr : 1.f - fzr);
                float Vsx = 0, Vsz = 0, Ns = 0, Asx = 0, Asz = 0, Ksx = 0, Ksz = 0, Ngs = 0;
                for (int kz_ = 0; kz_ < 2; kz_++) for (int kx_ = 0; kx_ < 2; kx_++) {
                    float wk = w4[kz_][kx_]; int c = wrapi(iz + kz_, G) * G + wrapi(ix + kx_, G);
                    float vx = Vx[a][c], vz = Vz[a][c], nt = Ntot[c], ac_x = A_x[a][c], ac_z = A_z[a][c], kcx = Kx[a][c], kcz = Kz[a][c], ngc = Ng[a][c];
                    if (leaveOneOut) {
                        // remove own splat (4 texels j) as seen from corner texel k
                        for (int jz = 0; jz < 2; jz++) for (int jx = 0; jx < 2; jx++) {
                            float wj = w4[jz][jx]; int ox = jx - kx_, oz = jz - kz_;
                            float kxx = 0, kzz = 0;
                            { float dd2 = (ox * tx) * (ox * tx) + (oz * tx) * (oz * tx); if (dd2 >= R * R) continue; }
                            bool farv = kvAt(ox, oz, kxx, kzz);
                            if (farv) { vx -= wj * kxx; vz -= wj * kzz; } // own weight 1 for own team target
                            nt -= wj; ac_x -= wj * p[i].hx; ac_z -= wj * p[i].hz;
                            kcx -= wj * ox * tx; kcz -= wj * oz * tx; ngc -= wj;
                        }
                    }
                    Vsx += wk * vx; Vsz += wk * vz; Ns += wk * nt; Asx += wk * ac_x; Asz += wk * ac_z;
                    // cohesion correction: displacement from particle to this corner texel centre
                    float ddx = (kx_ - fxr) * tx, ddz = (kz_ - fzr) * tx;
                    Ksx += wk * (kcx + ngc * ddx); Ksz += wk * (kcz + ngc * ddz); Ngs += wk * ngc;
                }
                if (mode != 2 && Ns > 1e-3f) { sx = Vsx; sz = Vsz; sN = Ns; }
                ax = Asx; az = Asz; kx = Ksx; kz = Ksz; cN = Ngs;
            }
            if (mode == 5) {
                float tn_ = S / Gn; float gx = p[i].x + HALF, gz = p[i].z + HALF;
                int ixn = (int)floorf(gx / tn_), izn = (int)floorf(gz / tn_); if (ixn >= Gn) ixn = Gn - 1; if (izn >= Gn) izn = Gn - 1;
                float pox = gx - (ixn + 0.5f) * tn_, poz = gz - (izn + 0.5f) * tn_; int v2 = 0;
                for (int dz = -kst; dz <= kst; dz++) for (int dx = -kst; dx <= kst; dx++) {
                    size_t idx = (size_t)wrapi(izn + dz, Gn) * Gn + wrapi(ixn + dx, Gn);
                    bool own = (dx == 0 && dz == 0);
                    if (own && ownEx) continue;
                    for (int g = 0; g < 2; g++) {
                        float cnt = nN[g][idx], sox = sxN[g][idx], soz = szN[g][idx];
                        if (own && g == a) { cnt -= 1.f; sox -= pox; soz -= poz; }
                        if (cnt < 0.5f) continue;
                        float cxr = dx * tn_ + sox / cnt - pox, czr = dz * tn_ + soz / cnt - poz, r2 = cxr * cxr + czr * czr;
                        if (r2 < 1e-6f || r2 >= r0 * r0) continue;
                        float m = (g == a) ? 1.f : mult, inv = 1.f / (r2 + 1e-3f);
                        sx += -cxr * inv * m * cnt; sz += -czr * inv * m * cnt;
                    }
                }
                if (ownEx) {
                    for (int j : listN[(size_t)izn * Gn + ixn]) {
                        if (j == i) continue; v2++;
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z), r2 = ddx * ddx + ddz * ddz;
                        if (r2 < 1e-6f || r2 >= r0 * r0) continue;
                        float m = p[j].team == a ? 1.f : mult, inv = 1.f / (r2 + 1e-3f);
                        sx += -ddx * inv * m; sz += -ddz * inv * m;
                    }
                }
                visSum += v2; if (v2 > visMx) visMx = v2;
                int cx3 = cellc(p[i].x), cz3 = cellc(p[i].z), c3 = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) c3 += (int)cells[((cz3 + dz + RES) % RES) * RES + (cx3 + dx + RES) % RES].size();
                vis3Sum += c3 - 1;
            }
            if (mode == 4) {
                int cx2 = cellc2(p[i].x), cz2 = cellc2(p[i].z), v2 = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                    auto &c = cells2[(size_t)((cz2 + dz + RES2) % RES2) * RES2 + (cx2 + dx + RES2) % RES2];
                    for (int j : c) {
                        if (j == i) continue;
                        v2++;
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z), r2 = ddx * ddx + ddz * ddz;
                        if (r2 < 1e-6f || r2 >= r0 * r0) continue;
                        float m = p[j].team == a ? 1.f : mult, inv = 1.f / (r2 + 1e-3f);
                        sx += -ddx * inv * m; sz += -ddz * inv * m;
                    }
                }
                visSum += v2; if (v2 > visMx) visMx = v2;
                int cx3 = cellc(p[i].x), cz3 = cellc(p[i].z), c3 = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) c3 += (int)cells[((cz3 + dz + RES) % RES) * RES + (cx3 + dx + RES) % RES].size();
                vis3Sum += c3 - 1;
            }
            float Fx = 0, Fz = 0;
            if (sN > 1e-3f) { Fx += sx / sN * wS; Fz += sz / sN * wS; }
            if (ax * ax + az * az > 1e-6f) { float l = sqrtf(ax * ax + az * az); Fx += ax / l * wA; Fz += az / l * wA; }
            if (cN > 1e-3f) { float mx = kx / cN, mz = kz / cN; if (mx * mx + mz * mz > 1e-6f) { float l = sqrtf(mx * mx + mz * mz); Fx += mx / l * wC; Fz += mz / l * wC; } }
            fx[i] = Fx; fz[i] = Fz;
        }
        lastVis = (double)visSum / N; lastVis3 = (double)vis3Sum / N; lastVisMax = visMx;
        for (int i = 0; i < N; i++) {
            phx[i] = p[i].hx; phz[i] = p[i].hz;
            float hx = p[i].hx, hz = p[i].hz;
            float dxs = fx[i], dzs = fz[i];
            float fbx = hx, fbz = hz; unitOr(fbx, fbz, 1.f, 0.f);
            unitOr(dxs, dzs, fbx, fbz);
            unitOr(hx, hz, dxs, dzs);
            float k = fminf(fmaxf(turn * dt, 0.f), 1.f);
            float bx = hx + (dxs - hx) * k, bz = hz + (dzs - hz) * k;
            unitOr(bx, bz, dxs, dzs); unitOr(bx, bz, 1.f, 0.f);
            p[i].hx = bx; p[i].hz = bz;
            float nx = p[i].x + bx * cruise * dt, nz = p[i].z + bz * cruise * dt;
            if (nx >= HALF) nx -= S; if (nx < -HALF) nx += S; if (nz >= HALF) nz -= S; if (nz < -HALF) nz += S;
            p[i].x = nx; p[i].z = nz;
        }
    }
}
