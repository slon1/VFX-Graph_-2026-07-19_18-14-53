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
#include <string>
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
static float pcq(vector<float> v, float q) { sort(v.begin(), v.end()); return v[(size_t)(q * (v.size() - 1))]; }
static int wrapi(int a, int G) { return ((a % G) + G) % G; }

int main(int argc, char **argv) {
    int mode = atoi(argv[1]);
    int G = atoi(argv[2]);
    unsigned seed = atoi(argv[3]);
    float T = atof(argv[4]); float r0 = argc > 5 ? atof(argv[5]) : 0.75f; int Gn = 180, kst = 3, ownEx = 1;
    if (mode == 5 || mode == 9 || mode == 10) { Gn = atoi(argv[5]); kst = atoi(argv[6]); ownEx = atoi(argv[7]); r0 = kst * (S / Gn); if (mode == 10 && kst == 0) r0 = 1.f; }
    auto envf = [&](const char *k, float d) { const char *v = getenv(k); return v ? (float)atof(v) : d; };
    const float Ph = envf("P_H", 1.5f), Prho0 = envf("P_RHO0", 8.f), Pgam = envf("P_GAMMA", 2.f), Pgain = envf("P_GAIN", 1.f), Pmu = envf("P_MU", 4.f), Prs = envf("P_RS", 0.f), Pema = envf("P_EMA", 1.f), Pclamp = envf("P_CLAMP", 1.f);
    if (mode == 6 && Prs > 0) r0 = Prs;
    const float Pkap = envf("P_KAPPA", 0.3f); const int Pit = (int)envf("P_ITERS", 16.f), Pwarm = (int)envf("P_WARM", 1.f);
    const float M_FPS = getenv("M_FPS") ? (float)atof(getenv("M_FPS")) : 60.f; const int Pupd = getenv("M_UPD") ? atoi(getenv("M_UPD")) : 1;
    const float dt = 1.f / M_FPS, mult = 4.f, discR = 12.f;
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
    if (mode == 5 || mode == 9 || (mode == 10 && kst > 0)) { for (int g = 0; g < 2; g++) { nN[g].assign((size_t)Gn * Gn, 0.f); sxN[g].assign((size_t)Gn * Gn, 0.f); szN[g].assign((size_t)Gn * Gn, 0.f); } listN.resize((size_t)Gn * Gn); }
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
    struct CO { int ox, oz; float w; }; vector<CO> cone;
    { double sum = 0; int rc = (int)ceilf(Ph / tx) + 1;
      for (int oz = -rc; oz <= rc; oz++) for (int ox = -rc; ox <= rc; ox++) { double d = sqrt((double)(ox * tx) * (ox * tx) + (double)(oz * tx) * (oz * tx)); if (d >= Ph) continue; double w = 1.0 - d / Ph; cone.push_back({ox, oz, (float)w}); sum += w; }
      for (auto &c : cone) c.w = (float)(c.w / (sum * tx * tx)); }
    vector<float> rho[2], Pf[2], gradx[2], gradz[2], rhoPrev[2], PhiP[2], PhiQ[2];
    struct PO { int ox, oz; float w; }; vector<PO> psi;
    if (mode == 7 || mode == 8 || mode == 9) {
        vector<pair<float, float>> gs;
        if (mode == 8) { const char *e = getenv("P_GS"); string str = e ? e : ""; size_t pos = 0;
            while (pos < str.size()) { size_t cm = str.find(',', pos); if (cm == string::npos) cm = str.size(); string it = str.substr(pos, cm - pos); size_t co = it.find(':'); gs.push_back({(float)atof(it.substr(0, co).c_str()), (float)atof(it.substr(co + 1).c_str())}); pos = cm + 1; } }
        float Rw = (mode == 7 || mode == 9) ? R : envf("P_WIN", 9.f);
        int rc = (int)ceilf(Rw / tx) + 1;
        for (int oz = -rc; oz <= rc; oz++) for (int ox = -rc; ox <= rc; ox++) {
            float d = sqrtf((float)(ox * tx) * (ox * tx) + (float)(oz * tx) * (oz * tx)); if (d >= Rw) continue;
            float w; if (mode == 7 || mode == 9) { float de = (mode == 9) ? fmaxf(d, r0) : d; w = 0.5f * logf((de * de + 1e-3f) / (R * R + 1e-3f)); } else { w = 0; for (auto &g : gs) w -= g.first * expf(-d * d / (2.f * g.second * g.second)); }
            psi.push_back({ox, oz, w}); }
    }
    for (int g = 0; g < 2; g++) { rho[g].assign(GG, 0.f); rhoPrev[g].assign(GG, -1.f); PhiP[g].assign(GG, 0.f); PhiQ[g].assign(GG, 0.f); Pf[g].assign(GG, 0.f); gradx[g].assign(GG, 0.f); gradz[g].assign(GG, 0.f); }

    for (int s = 0; s <= steps; s++) {
        for (auto &c : cells) c.clear();
        for (int i = 0; i < N; i++) cells[cellc(p[i].z) * RES + cellc(p[i].x)].push_back(i);
        if (mode == 5 || mode == 9 || (mode == 10 && kst > 0)) {
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
        if (mode == 4 || (mode == 6 && Prs > 0)) { for (auto &c : cells2) c.clear(); for (int i = 0; i < N; i++) cells2[cellc2(p[i].z) * RES2 + cellc2(p[i].x)].push_back(i); }

        if (s == snaps[0] || s == snaps[1] || s == snaps[2]) {
            int cross = 0; long enemy = 0, tot = 0; double polSum = 0; int polN = 0; vector<float> rO(N, 0.f), rA(N, 0.f);
            vector<int> uf(N); iota(uf.begin(), uf.end(), 0);
            auto find = [&](int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; };
            for (int i = 0; i < N; i++) {
                int cx = cellc(p[i].x), cz = cellc(p[i].z); bool hasEnemy = false; float lhx = 0, lhz = 0; int lc = 0; float rOi = 0, rAi = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                    auto &c = cells[((cz + dz + RES) % RES) * RES + (cx + dx + RES) % RES];
                    for (int j : c) {
                        if (j == i) continue;
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z);
                        if (ddx * ddx + ddz * ddz >= R * R) continue;
                        { float d = sqrtf(ddx * ddx + ddz * ddz); if (d < Ph) { float w = 3.f / (3.14159265f * Ph * Ph) * (1.f - d / Ph); rAi += w; if (p[j].team == p[i].team) rOi += w; } }
                        tot++;
                        if (p[j].team != p[i].team) { enemy++; hasEnemy = true; }
                        else { uf[find(i)] = find(j); lhx += p[j].hx; lhz += p[j].hz; lc++; }
                    }
                }
                rO[i] = rOi; rA[i] = rAi;
                if (lc > 0) { polSum += sqrtf(lhx * lhx + lhz * lhz) / lc; polN++; }
                if (hasEnemy) cross++;
            }
            vector<int> sz(N, 0); for (int i = 0; i < N; i++) sz[find(i)]++;
            int fl[2] = {0, 0};
            for (int i = 0; i < N; i++) if (find(i) == i && sz[i] >= 30) fl[p[i].team]++;
            double tm = 0, tq = 0; for (int i = 0; i < N; i++) { double cr = phx[i] * p[i].hz - phz[i] * p[i].hx, dt_ = phx[i] * p[i].hx + phz[i] * p[i].hz; double ang = fabs(atan2(cr, dt_)); tm += ang; tq += ang * ang; } tm /= N; double tsd = sqrt(fmax(0.0, tq / N - tm * tm));
            printf("t=%.0f contact=%.4f enemy=%.4f flocks=%d,%d pol=%.4f turn=%.5f turnSd=%.5f vis=%.1f vis3=%.1f visMax=%d rhoOwn50=%.2f rhoOwn90=%.2f rhoAll50=%.2f\n", s * dt, 100.f * cross / N, tot ? 100.f * enemy / tot : 0.f, fl[0], fl[1], polN ? polSum / polN : 0.0, tm, tsd, lastVis, lastVis3, lastVisMax, pcq(rO, 0.5f), pcq(rO, 0.9f), pcq(rA, 0.5f));
        }
        if (s == steps) break;

        bool updF = (Pupd <= 1) || (s % Pupd == 0);
        bool useField = mode != 0;
        bool leaveOneOut = mode != 3;
        if (useField && updF) {
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

        if (mode == 10) {
            float den = 4.f + Pkap * Pkap * tx * tx;
            if (!Pwarm) for (int g = 0; g < 2; g++) fill(PhiP[g].begin(), PhiP[g].end(), 0.f);
            for (int it = 0; it < Pit; it++) for (int g = 0; g < 2; g++) {
                for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                    int c = cz * G + cx;
                    PhiQ[g][c] = (PhiP[g][cz * G + wrapi(cx + 1, G)] + PhiP[g][cz * G + wrapi(cx - 1, G)] + PhiP[g][wrapi(cz + 1, G) * G + cx] + PhiP[g][wrapi(cz - 1, G) * G + cx] + n[g][c]) / den; }
                swap(PhiP[g], PhiQ[g]);
            }
            for (int a_ = 0; a_ < 2; a_++) for (int c = 0; c < GG; c++) Pf[a_][c] = PhiP[a_][c] + mult * PhiP[1 - a_][c];
            for (int a_ = 0; a_ < 2; a_++) for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                int c = cz * G + cx;
                gradx[a_][c] = (Pf[a_][cz * G + wrapi(cx + 1, G)] - Pf[a_][cz * G + wrapi(cx - 1, G)]) / (2.f * tx);
                gradz[a_][c] = (Pf[a_][wrapi(cz + 1, G) * G + cx] - Pf[a_][wrapi(cz - 1, G) * G + cx]) / (2.f * tx); }
        }
        if ((mode == 7 || mode == 8 || mode == 9) && updF) {
            for (int g = 0; g < 2; g++) for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                float r = 0; for (auto &o : psi) r += n[g][wrapi(cz + o.oz, G) * G + wrapi(cx + o.ox, G)] * o.w; rho[g][cz * G + cx] = r; }
            for (int a_ = 0; a_ < 2; a_++) for (int c = 0; c < GG; c++) Pf[a_][c] = rho[a_][c] + mult * rho[1 - a_][c];
            for (int a_ = 0; a_ < 2; a_++) for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                int c = cz * G + cx;
                gradx[a_][c] = (Pf[a_][cz * G + wrapi(cx + 1, G)] - Pf[a_][cz * G + wrapi(cx - 1, G)]) / (2.f * tx);
                gradz[a_][c] = (Pf[a_][wrapi(cz + 1, G) * G + cx] - Pf[a_][wrapi(cz - 1, G) * G + cx]) / (2.f * tx); }
        }
        if (mode == 6) {
            for (int g = 0; g < 2; g++) for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                float r = 0; for (auto &o : cone) r += n[g][wrapi(cz + o.oz, G) * G + wrapi(cx + o.ox, G)] * o.w; { int id_ = cz * G + cx; if (Pema < 1.f && rhoPrev[g][id_] >= 0.f) r = Pema * r + (1.f - Pema) * rhoPrev[g][id_]; rhoPrev[g][id_] = r; rho[g][id_] = r; } }
            for (int a_ = 0; a_ < 2; a_++) for (int c = 0; c < GG; c++) {
                float reff = rho[a_][c] + Pmu * rho[1 - a_][c]; float ex = fmaxf(reff - Prho0, 0.f) / Prho0; Pf[a_][c] = powf(ex, Pgam); }
            for (int a_ = 0; a_ < 2; a_++) for (int cz = 0; cz < G; cz++) for (int cx = 0; cx < G; cx++) {
                int c = cz * G + cx;
                gradx[a_][c] = (Pf[a_][cz * G + wrapi(cx + 1, G)] - Pf[a_][cz * G + wrapi(cx - 1, G)]) / (2.f * tx);
                gradz[a_][c] = (Pf[a_][wrapi(cz + 1, G) * G + cx] - Pf[a_][wrapi(cz - 1, G) * G + cx]) / (2.f * tx); }
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
                float Vsx = 0, Vsz = 0, Ns = 0, Asx = 0, Asz = 0, Ksx = 0, Ksz = 0, Ngs = 0; float gsx = 0, gsz = 0;
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
                    if (mode >= 6) { gsx += wk * gradx[a][c]; gsz += wk * gradz[a][c]; }
                    Vsx += wk * vx; Vsz += wk * vz; Ns += wk * nt; Asx += wk * ac_x; Asz += wk * ac_z;
                    // cohesion correction: displacement from particle to this corner texel centre
                    float ddx = (kx_ - fxr) * tx, ddz = (kz_ - fzr) * tx;
                    Ksx += wk * (kcx + ngc * ddx); Ksz += wk * (kcz + ngc * ddz); Ngs += wk * ngc;
                }
                if (mode != 2 && Ns > 1e-3f) { sx = Vsx; sz = Vsz; sN = Ns; }
                ax = Asx; az = Asz; kx = Ksx; kz = Ksz; cN = Ngs;
                if (mode == 10) { if (Ns > 1e-3f) { sx = -6.2831853f * Pgain * gsx; sz = -6.2831853f * Pgain * gsz; sN = Ns; } else { sx = 0; sz = 0; sN = 0; } }
                if (mode == 7 || mode == 8 || mode == 9) { if (Ns > 1e-3f) { sx = gsx; sz = gsz; sN = Ns; } else { sx = 0; sz = 0; sN = 0; } }
                if (mode == 6) {
                    if (Ns > 1e-3f) { float gxv = -Pgain * Ph * gsx, gzv = -Pgain * Ph * gsz; float l = sqrtf(gxv * gxv + gzv * gzv); if (l > Pclamp) { gxv *= Pclamp / l; gzv *= Pclamp / l; } sx = gxv * Ns; sz = gzv * Ns; sN = Ns; }
                    else { sx = 0; sz = 0; sN = 0; }
                }
            }
            if (mode == 5 || mode == 9 || (mode == 10 && kst > 0)) {
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
            if (mode == 4 || (mode == 6 && Prs > 0)) {
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
