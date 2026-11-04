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
        double texelX = layout.CellSize.x;
        double texelY = layout.CellSize.y;
        for (int i = 0; i < count; i++)
        {
            int team = teams[i];
            if (team != 0 && team != 1)
            {
                continue;
            }

            Vector2 u = (positions[i] - layout.Origin) / layout.CellSize;
            int i0x = (int)Math.Floor(u.x);
            int i0y = (int)Math.Floor(u.y);
            double fx = u.x - i0x;
            double fy = u.y - i0y;
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
                    double wx = dx == 0 ? 1.0 - fx : fx;
                    double wz = dz == 0 ? 1.0 - fy : fy;
                    double weight = wx * wz;
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
                    double texelFromParticleX = (dx - fx) * texelX;
                    double texelFromParticleY = (dz - fy) * texelY;
                    ksx += weight * (field[b + 6] + ngk * texelFromParticleX);
                    ksy += weight * (field[b + 7] + ngk * texelFromParticleY);
                }
            }

            ns -= 1.0;
            asx -= headings[i].x;
            asy -= headings[i].y;
            ng -= 1.0;
            Vector3 teamWeights = weights[team];
            samples[i] = Compose(gsx, gsy, ns, asx, asy, ksx, ksy, ng, teamWeights.x, teamWeights.y, teamWeights.z);
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
