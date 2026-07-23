// Shared noise library: PCG hash, 3D value noise, curl noise, vector fbm.
// Pure functions only — no buffers, no uniforms.

#ifndef M3D_NOISE_INCLUDED
#define M3D_NOISE_INCLUDED

uint3 Pcg3d(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

// Hash of an integer lattice point -> float3 in [0, 1].
float3 Hash33(float3 latticePoint)
{
    uint3 h = Pcg3d(uint3(int3(latticePoint) + 32768));
    return float3(h) * (1.0 / 4294967295.0);
}

// 3D value noise, three independent channels in [0, 1].
float3 ValueNoise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);

    float3 n000 = Hash33(i + float3(0, 0, 0));
    float3 n100 = Hash33(i + float3(1, 0, 0));
    float3 n010 = Hash33(i + float3(0, 1, 0));
    float3 n110 = Hash33(i + float3(1, 1, 0));
    float3 n001 = Hash33(i + float3(0, 0, 1));
    float3 n101 = Hash33(i + float3(1, 0, 1));
    float3 n011 = Hash33(i + float3(0, 1, 1));
    float3 n111 = Hash33(i + float3(1, 1, 1));

    float3 x00 = lerp(n000, n100, u.x);
    float3 x10 = lerp(n010, n110, u.x);
    float3 x01 = lerp(n001, n101, u.x);
    float3 x11 = lerp(n011, n111, u.x);

    float3 y0 = lerp(x00, x10, u.y);
    float3 y1 = lerp(x01, x11, u.y);

    return lerp(y0, y1, u.z);
}

// Signed noise vector in [-1, 1].
float3 SignedNoise3(float3 p)
{
    return ValueNoise3(p) * 2.0 - 1.0;
}

// Divergence-free curl of the ValueNoise3 vector field (central differences).
// Note: 6 noise evaluations — the most expensive primitive here.
float3 CurlNoise(float3 p)
{
    const float e = 0.1;
    const float inv2e = 1.0 / (2.0 * e);

    float3 fx0 = ValueNoise3(p - float3(e, 0, 0));
    float3 fx1 = ValueNoise3(p + float3(e, 0, 0));
    float3 fy0 = ValueNoise3(p - float3(0, e, 0));
    float3 fy1 = ValueNoise3(p + float3(0, e, 0));
    float3 fz0 = ValueNoise3(p - float3(0, 0, e));
    float3 fz1 = ValueNoise3(p + float3(0, 0, e));

    float dFzdy = (fy1.z - fy0.z) * inv2e;
    float dFydz = (fz1.y - fz0.y) * inv2e;
    float dFxdz = (fz1.x - fz0.x) * inv2e;
    float dFzdx = (fx1.z - fx0.z) * inv2e;
    float dFydx = (fx1.y - fx0.y) * inv2e;
    float dFxdy = (fy1.x - fy0.x) * inv2e;

    return float3(dFzdy - dFydz, dFxdz - dFzdx, dFydx - dFxdy);
}

// Vector fbm: octaves of signed value noise, [-1, 1] range approx.
float3 Fbm3(float3 p, int octaves)
{
    float3 sum = 0;
    float amplitude = 0.5;
    float frequency = 1.0;

    for (int i = 0; i < octaves; i++)
    {
        sum += SignedNoise3(p * frequency) * amplitude;
        frequency *= 2.0;
        amplitude *= 0.5;
    }

    return sum;
}

#endif // M3D_NOISE_INCLUDED
