using System;
using UnityEngine;

/// <summary>
/// Double-precision field force and uncapped pair walk (ADR-062 §2.2, §2.6). Tests only.
/// No GPU and no pass code. Field layout is <see cref="FieldNeighborReference"/>.
/// </summary>
public static class FieldForceReference
{
    public const double CountEpsilon = 0.001;
    public const double UnitEpsilon = 1e-6;
    public const double SelfRadiusEpsilon = 1e-6;

    public struct Sample
    {
        public double Fx;
        public double Fy;
        public double SepX;
        public double SepY;
        public double AlignX;
        public double AlignY;
        public double CohX;
        public double CohY;

        /// <summary>Own neighbours inside R. Filled by <see cref="FullTraversal"/>.</summary>
        public int NgRef;

        /// <summary>Length of the mean own-neighbour displacement. Filled by <see cref="FullTraversal"/>.</summary>
        public double MeanDisplacement;

        /// <summary>Separation count after subtracting self. Filled by <see cref="FieldRead"/>.</summary>
        public double Ns;

        /// <summary>Own-neighbour count after subtracting self. Filled by <see cref="FieldRead"/>.</summary>
        public double Ng;

        /// <summary>Length of the alignment vector after subtracting self. Filled by <see cref="FieldRead"/>.</summary>
        public double AlignLength;

        /// <summary>Length of Ks/Ng after subtracting self. Filled by <see cref="FieldRead"/>.</summary>
        public double MeanLength;
    }

    public static Sample[] FieldRead(
        double[] field,
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        Vector3[] weights,
        SpatialHashLayout layout)
    {
        int count = positions.Length;
        var samples = new Sample[count];
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        float cellX = layout.CellSize.x;
        float cellY = layout.CellSize.y;
        float originX = layout.Origin.x;
        float originY = layout.Origin.y;
        for (int i = 0; i < count; i++)
        {
            int team = teams[i];
            if (team != 0 && team != 1)
            {
                continue;
            }

            float ux = (positions[i].x - originX) / cellX;
            float uy = (positions[i].y - originY) / cellY;
            int i0x = (int)Math.Floor(ux);
            int i0y = (int)Math.Floor(uy);
            float fx = ux - i0x;
            float fy = uy - i0y;
            double gsx = 0;
            double gsy = 0;
            double ns = 0;
            double asx = 0;
            double asy = 0;
            double ng = 0;
            double ksx = 0;
            double ksy = 0;
            for (int dz = 0; dz <= 1; dz++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    float wx = dx == 0 ? 1f - fx : fx;
                    float wy = dz == 0 ? 1f - fy : fy;
                    float weight = wx * wy;
                    int cell = Wrap(i0x + dx, i0y + dz, resX, resY);
                    int b = cell * FieldNeighborReference.FieldFloats + team * 8;
                    double ntot = field[b + 2];
                    double ngk = field[b + 3];
                    gsx += weight * field[b];
                    gsy += weight * field[b + 1];
                    ns += weight * ntot;
                    asx += weight * field[b + 4];
                    asy += weight * field[b + 5];
                    ng += weight * ngk;
                    float offX = ((float)dx - fx) * cellX;
                    float offY = ((float)dz - fy) * cellY;
                    ksx += weight * (field[b + 6] + ngk * offX);
                    ksy += weight * (field[b + 7] + ngk * offY);
                }
            }

            ns -= 1.0;
            asx -= headings[i].x;
            asy -= headings[i].y;
            ng -= 1.0;
            Vector3 teamWeights = weights[team];
            Sample sample = Compose(
                gsx, gsy, ns, asx, asy, ksx, ksy, ng, teamWeights.x, teamWeights.y, teamWeights.z);
            sample.Ns = ns;
            sample.Ng = ng;
            sample.AlignLength = Math.Sqrt(asx * asx + asy * asy);
            if (Math.Abs(ng) > 1e-12)
            {
                double mx = ksx / ng;
                double my = ksy / ng;
                sample.MeanLength = Math.Sqrt(mx * mx + my * my);
            }

            samples[i] = sample;
        }

        return samples;
    }

    /// <summary>
    /// Force in float32, same adds and thresholds as FieldNeighborForce.
    /// Sa, Sk, Sg are the sums of absolute scalar terms added into align, ks and gs.
    /// </summary>
    public struct FloatSample
    {
        public float Fx;
        public float Fy;
        public float Sa;
        public float Sk;
        public float Sg;
        public float AbsAs;
        public float AbsKs;
        public float AbsNs;
    }

    public static FloatSample[] FloatRead(
        Vector4[] slots,
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        Vector3[] weights,
        SpatialHashLayout layout,
        Vector2[] cic = null)
    {
        int count = positions.Length;
        var samples = new FloatSample[count];
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        float cellX = layout.CellSize.x;
        float cellY = layout.CellSize.y;
        float originX = layout.Origin.x;
        float originY = layout.Origin.y;
        for (int i = 0; i < count; i++)
        {
            int team = teams[i];
            if (team != 0 && team != 1)
            {
                continue;
            }

            float fx;
            float fy;
            var cornerW = new float[4];
            var cornerCell = new int[4];
            if (cic == null)
            {
                float ux = (positions[i].x - originX) / cellX;
                float uy = (positions[i].y - originY) / cellY;
                int i0x = (int)MathF.Floor(ux);
                int i0y = (int)MathF.Floor(uy);
                fx = ux - i0x;
                fy = uy - i0y;
                cornerW[0] = (1f - fx) * (1f - fy);
                cornerW[1] = fx * (1f - fy);
                cornerW[2] = (1f - fx) * fy;
                cornerW[3] = fx * fy;
                for (int k = 0; k < 4; k++)
                {
                    int dx = k == 1 || k == 3 ? 1 : 0;
                    int dz = k >= 2 ? 1 : 0;
                    cornerCell[k] = Wrap(i0x + dx, i0y + dz, resX, resY);
                }
            }
            else
            {
                int b = i * 4;
                cornerW[0] = cic[b].y;
                cornerW[1] = cic[b + 1].y;
                cornerW[2] = cic[b + 2].y;
                cornerW[3] = cic[b + 3].y;
                fx = cornerW[1] + cornerW[3];
                fy = cornerW[2] + cornerW[3];
                for (int k = 0; k < 4; k++)
                {
                    cornerCell[k] = (int)cic[b + k].x;
                }
            }

            float gsx = 0f;
            float gsy = 0f;
            float ns = 0f;
            float ax = 0f;
            float ay = 0f;
            float ng = 0f;
            float kx = 0f;
            float ky = 0f;
            float sg = 0f;
            float sa = 0f;
            float sk = 0f;
            for (int k = 0; k < 4; k++)
            {
                int dx = k == 1 || k == 3 ? 1 : 0;
                int dz = k >= 2 ? 1 : 0;
                float w = cornerW[k];
                int cell = cornerCell[k];
                int slot = cell * 4 + team * 2;
                Vector4 packedG = slots[slot];
                Vector4 packedA = slots[slot + 1];
                float gx = w * packedG.x;
                float gz = w * packedG.y;
                gsx += gx;
                gsy += gz;
                sg += Math.Abs(gx) + Math.Abs(gz);
                ns += w * packedG.z;
                ng += w * packedG.w;
                float lax = w * packedA.x;
                float lay = w * packedA.y;
                ax += lax;
                ay += lay;
                sa += Math.Abs(lax) + Math.Abs(lay);
                float offX = ((float)dx - fx) * cellX;
                float offY = ((float)dz - fy) * cellY;
                float lkx = w * (packedA.z + packedG.w * offX);
                float lky = w * (packedA.w + packedG.w * offY);
                kx += lkx;
                ky += lky;
                sk += Math.Abs(lkx) + Math.Abs(lky);
            }

            float hx = headings[i].x;
            float hy = headings[i].y;
            ns -= 1f;
            ax -= hx;
            ay -= hy;
            ng -= 1f;
            sa += Math.Abs(hx) + Math.Abs(hy);
            Vector3 teamWeights = weights[team];
            float forceX = 0f;
            float forceY = 0f;
            if (ns > 0.001f)
            {
                forceX += (gsx / ns) * teamWeights.x;
                forceY += (gsy / ns) * teamWeights.x;
            }

            float align2 = ax * ax + ay * ay;
            if (align2 > 1e-6f)
            {
                float inv = 1f / MathF.Sqrt(align2);
                forceX += ax * inv * teamWeights.y;
                forceY += ay * inv * teamWeights.y;
            }

            if (ng > 0.001f)
            {
                float mx = kx / ng;
                float my = ky / ng;
                float mean2 = mx * mx + my * my;
                if (mean2 > 1e-6f)
                {
                    float inv = 1f / MathF.Sqrt(mean2);
                    forceX += mx * inv * teamWeights.z;
                    forceY += my * inv * teamWeights.z;
                }
            }

            samples[i] = new FloatSample
            {
                Fx = forceX,
                Fy = forceY,
                Sa = sa,
                Sk = sk,
                Sg = sg,
                AbsAs = MathF.Sqrt(align2),
                AbsKs = MathF.Sqrt(kx * kx + ky * ky),
                AbsNs = Math.Abs(ns),
            };
        }

        return samples;
    }

    public static Sample[] FullTraversal(
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        Vector3[] weights,
        double[] inter,
        double radius,
        Vector2 world)
    {
        int count = positions.Length;
        var samples = new Sample[count];
        double radius2 = radius * radius;
        for (int i = 0; i < count; i++)
        {
            int team = teams[i];
            if (team != 0 && team != 1)
            {
                continue;
            }

            double sepX = 0;
            double sepY = 0;
            int sepN = 0;
            double alignX = 0;
            double alignY = 0;
            double cohX = 0;
            double cohY = 0;
            int own = 0;
            for (int j = 0; j < count; j++)
            {
                if (j == i || (teams[j] != 0 && teams[j] != 1))
                {
                    continue;
                }

                Vector2 delta = MinImage(positions[j] - positions[i], world);
                double r2 = delta.x * (double)delta.x + delta.y * (double)delta.y;
                if (r2 < SelfRadiusEpsilon || r2 >= radius2)
                {
                    continue;
                }

                bool same = teams[j] == team;
                double mul = same ? 1.0 : inter[team];
                double inv = 1.0 / (r2 + CountEpsilon);
                sepX += -delta.x * inv * mul;
                sepY += -delta.y * inv * mul;
                sepN++;
                if (!same)
                {
                    continue;
                }

                alignX += headings[j].x;
                alignY += headings[j].y;
                cohX += delta.x;
                cohY += delta.y;
                own++;
            }

            Vector3 teamWeights = weights[team];
            double ns = sepN;
            Sample sample = Compose(
                sepX,
                sepY,
                ns,
                alignX,
                alignY,
                cohX,
                cohY,
                own,
                teamWeights.x,
                teamWeights.y,
                teamWeights.z);
            sample.NgRef = own;
            if (own > 0)
            {
                double meanX = cohX / own;
                double meanY = cohY / own;
                sample.MeanDisplacement = Math.Sqrt(meanX * meanX + meanY * meanY);
            }

            samples[i] = sample;
        }

        return samples;
    }

    private static Sample Compose(
        double sepX,
        double sepY,
        double sepN,
        double alignX,
        double alignY,
        double cohX,
        double cohY,
        double own,
        double wSep,
        double wAlign,
        double wCoh)
    {
        var sample = new Sample();
        if (sepN > CountEpsilon)
        {
            sample.SepX = sepX / sepN * wSep;
            sample.SepY = sepY / sepN * wSep;
        }

        double align2 = alignX * alignX + alignY * alignY;
        if (align2 > UnitEpsilon)
        {
            double inv = 1.0 / Math.Sqrt(align2);
            sample.AlignX = alignX * inv * wAlign;
            sample.AlignY = alignY * inv * wAlign;
        }

        if (own > CountEpsilon)
        {
            double meanX = cohX / own;
            double meanY = cohY / own;
            double mean2 = meanX * meanX + meanY * meanY;
            if (mean2 > UnitEpsilon)
            {
                double inv = 1.0 / Math.Sqrt(mean2);
                sample.CohX = meanX * inv * wCoh;
                sample.CohY = meanY * inv * wCoh;
            }
        }

        sample.Fx = sample.SepX + sample.AlignX + sample.CohX;
        sample.Fy = sample.SepY + sample.AlignY + sample.CohY;
        return sample;
    }

    private static Vector2 MinImage(Vector2 delta, Vector2 world)
    {
        return new Vector2(
            (float)(delta.x - world.x * Math.Round(delta.x / world.x)),
            (float)(delta.y - world.y * Math.Round(delta.y / world.y)));
    }

    private static int Wrap(int x, int y, int resX, int resY)
    {
        int wx = x % resX;
        if (wx < 0)
        {
            wx += resX;
        }

        int wy = y % resY;
        if (wy < 0)
        {
            wy += resY;
        }

        return wy * resX + wx;
    }
}
