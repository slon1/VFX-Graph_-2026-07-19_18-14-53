// CPU model of Boids_hash_2teams: BoidNeighborForce (cell-order cap, id%9 rotation) + TeamHeadingSteer + Integrate + Wrap.
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

int main(int argc, char **argv) {
    float dt = argc > 1 ? atof(argv[1]) : 1.f / 60.f;
    float mult = argc > 2 ? atof(argv[2]) : 4.f;
    int cap = argc > 3 ? atoi(argv[3]) : 48;   // 0 = no cap
    unsigned seed = argc > 4 ? atoi(argv[4]) : 1;
    float T = argc > 5 ? atof(argv[5]) : 20.f;
    int perTeam = argc > 6 ? atoi(argv[6]) : 1500;
    float discR = argc > 7 ? atof(argv[7]) : 12.f;
    int mode = argc > 8 ? atoi(argv[8]) : 0; // 0 visited-cap, 1 accepted-cap, 2 centre-first order, 3 symmetric stable key

    const float R = 3.f, wS = 1.2f, wA = 0.8f, wC = 0.6f, cruise = 6.f, turn = 4.f;
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
    vector<float> fx(N), fz(N);
    vector<char> capped(N);
    int steps = (int)(T / dt), report = (int)(2.5f / dt);
    printf("# dt=%.4f mult=%.1f cap=%d seed=%u N=%d\n", dt, mult, cap, seed, N);
    printf("# t  centroidDist  fireCx iceCx  crossContact%%  enemyShare%%  cap%%  fireFlocks iceFlocks\n");

    for (int s = 0; s <= steps; s++) {
        for (auto &c : cells) c.clear();
        for (int i = 0; i < N; i++) cells[cellc(p[i].z) * RES + cellc(p[i].x)].push_back(i);
        for (auto &c : cells) shuffle(c.begin(), c.end(), rng); // atomic scatter order varies per frame

        // metrics (uncapped, brute force within 3x3 cells)
        if (s % report == 0) {
            int cross = 0; long enemy = 0, tot = 0; int capN = 0;
            vector<int> uf(N); iota(uf.begin(), uf.end(), 0);
            auto find = [&](int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; };
            for (int i = 0; i < N; i++) {
                int cx = cellc(p[i].x), cz = cellc(p[i].z); bool hasEnemy = false;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                    auto &c = cells[((cz + dz + RES) % RES) * RES + (cx + dx + RES) % RES];
                    for (int j : c) {
                        if (j == i) continue;
                        float ddx = minimg(p[j].x - p[i].x), ddz = minimg(p[j].z - p[i].z);
                        if (ddx * ddx + ddz * ddz >= R * R) continue;
                        tot++;
                        if (p[j].team != p[i].team) { enemy++; hasEnemy = true; }
                        else uf[find(i)] = find(j);
                    }
                }
                if (hasEnemy) cross++;
                capN += capped[i];
            }
            vector<int> sz(N, 0); for (int i = 0; i < N; i++) sz[find(i)]++;
            int fl[2] = {0, 0};
            for (int i = 0; i < N; i++) if (find(i) == i && sz[i] >= 30) fl[p[i].team]++;
            double ang[2][2] = {{0,0},{0,0}}, angc[2][2] = {{0,0},{0,0}}; int cnt[2] = {0,0};
            for (auto &q : p) { double ax = (q.x + HALF) / S * 6.283185307, az = (q.z + HALF) / S * 6.283185307;
                ang[q.team][0] += sin(ax); angc[q.team][0] += cos(ax); ang[q.team][1] += sin(az); angc[q.team][1] += cos(az); cnt[q.team]++; }
            double cxm[2], czm[2];
            for (int t = 0; t < 2; t++) { cxm[t] = atan2(ang[t][0], angc[t][0]) / 6.283185307 * S; czm[t] = atan2(ang[t][1], angc[t][1]) / 6.283185307 * S; }
            float dx = minimg((float)(cxm[0] - cxm[1])), dz = minimg((float)(czm[0] - czm[1]));
            printf("%5.2f  %6.1f  %6.1f %6.1f  %6.1f  %6.1f  %6.1f   %d %d\n", s * dt, sqrtf(dx * dx + dz * dz), cxm[0], cxm[1],
                   100.f * cross / N, tot ? 100.f * enemy / tot : 0.f, 100.f * capN / N, fl[0], fl[1]);
        }
        if (s == steps) break;

        // force from snapshot (positions + headings at frame start)
        vector<P> snap = p;
        for (int i = 0; i < N; i++) {
            int cx = cellc(snap[i].x), cz = cellc(snap[i].z);
            float sx = 0, sz = 0, ax = 0, az = 0, kx = 0, kz = 0; int sN = 0, aN = 0, cN = 0; int visited = 0; bool cp = false;
            int start = i % 9;
            int accepted = 0;
            int n9 = 0;
            if (mode == 3) for (int q = 0; q < 9; q++) { int dy = q / 3 - 1, dxx = q % 3 - 1; n9 += cells[(((cz + dy) % RES + RES) % RES) * RES + ((cx + dxx) % RES + RES) % RES].size(); }
            static const int centreFirst[9] = {4, 1, 3, 5, 7, 0, 2, 6, 8};
            for (int n = 0; n < 9 && !cp; n++) {
                int k = (mode == 2) ? centreFirst[n] : (start + n) % 9; int dy = k / 3 - 1, dxx = k % 3 - 1;
                auto &c = cells[(((cz + dy) % RES + RES) % RES) * RES + ((cx + dxx) % RES + RES) % RES];
                for (int j : c) {
                    float dx = minimg(snap[j].x - snap[i].x), dz = minimg(snap[j].z - snap[i].z);
                    float r2 = dx * dx + dz * dz;
                    if (r2 < 1e-6f) continue;
                    if (mode == 3) {
                        if (cap > 0 && n9 > cap) {
                            unsigned a = min(i, j), b = max(i, j);
                            unsigned h = (a * 73856093u) ^ (b * 19349663u); h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                            if ((h & 0xFFFFFF) / 16777216.f >= (float)cap / n9) continue;
                        }
                        visited++;
                    } else if (mode == 1) {
                        if (r2 < R * R) { if (cap > 0 && accepted >= cap) { cp = true; break; } }
                    } else {
                        if (cap > 0 && visited >= cap) { cp = true; break; }
                        visited++;
                    }
                    if (r2 >= R * R) continue;
                    accepted++;
                    bool same = snap[j].team == snap[i].team;
                    float m = same ? 1.f : mult; float inv = 1.f / (r2 + 1e-3f);
                    sx += -dx * inv * m; sz += -dz * inv * m; sN++;
                    if (same) { ax += snap[j].hx; az += snap[j].hz; aN++; kx += dx; kz += dz; cN++; }
                }
            }
            capped[i] = cp;
            float Fx = 0, Fz = 0;
            if (sN) { Fx += sx / sN * wS; Fz += sz / sN * wS; }
            if (ax * ax + az * az > 1e-6f) { float l = sqrtf(ax * ax + az * az); Fx += ax / l * wA; Fz += az / l * wA; }
            if (cN) { float mx = kx / cN, mz = kz / cN; if (mx * mx + mz * mz > 1e-6f) { float l = sqrtf(mx * mx + mz * mz); Fx += mx / l * wC; Fz += mz / l * wC; } }
            fx[i] = Fx; fz[i] = Fz;
        }
        // steer + integrate + wrap
        for (int i = 0; i < N; i++) {
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
