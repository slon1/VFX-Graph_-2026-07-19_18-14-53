// Shared CIC corners, quantized weights, and integer texel wrap.
// The including shader defines FIXED_SCALE before this file.
// Old field kernels pass HashOrigin, HashCellSize, and HashRes.

// Integer modulo. c - r*floor(c/r) returns 150 on a 150-grid when the reciprocal is short.
int2 WrapCell(int2 c, int2 res)
{
    return ((c % res) + res) % res;
}

void CicCorners(
    float2 p,
    float2 origin,
    float2 cellSize,
    out int2 i0,
    out float2 f,
    out float w00,
    out float w10,
    out float w01,
    out float w11)
{
    float2 u = (p - origin) / cellSize;
    i0 = (int2)floor(u);
    f = u - float2(i0);
    w00 = (1.0 - f.x) * (1.0 - f.y);
    w10 = f.x * (1.0 - f.y);
    w01 = (1.0 - f.x) * f.y;
    w11 = f.x * f.y;
}

void QuantizeWeights(float w00, float w10, float w01, float w11, out int q00, out int q10, out int q01, out int q11)
{
    float s = FIXED_SCALE;
    float t0 = w00 * s;
    float t1 = w10 * s;
    float t2 = w01 * s;
    float t3 = w11 * s;
    q00 = (int)floor(t0 + 0.5);
    q10 = (int)floor(t1 + 0.5);
    q01 = (int)floor(t2 + 0.5);
    q11 = (int)floor(t3 + 0.5);
    int diff = (int)s - (q00 + q10 + q01 + q11);
    [loop]
    for (int step = 0; step < 4 && diff != 0; step++)
    {
        float e0 = (float)q00 - t0;
        float e1 = (float)q10 - t1;
        float e2 = (float)q01 - t2;
        float e3 = (float)q11 - t3;
        int pick = 0;
        float best = diff > 0 ? e0 : -e0;
        float b1 = diff > 0 ? e1 : -e1;
        float b2 = diff > 0 ? e2 : -e2;
        float b3 = diff > 0 ? e3 : -e3;
        if (b1 < best) { best = b1; pick = 1; }
        if (b2 < best) { best = b2; pick = 2; }
        if (b3 < best) { pick = 3; }
        int stepQ = diff > 0 ? 1 : -1;
        if (pick == 0) q00 += stepQ;
        else if (pick == 1) q10 += stepQ;
        else if (pick == 2) q01 += stepQ;
        else q11 += stepQ;
        diff -= stepQ;
    }
}
