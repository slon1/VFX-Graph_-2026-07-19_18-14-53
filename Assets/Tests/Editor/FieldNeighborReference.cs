using System;
using UnityEngine;

/// <summary>
/// Double-precision neighbor field (ADR-061 §2.2–2.4). Tests only. No GPU and no pass code.
/// Raw layout per texel: N0, Hx0, Hz0, N1, Hx1, Hz1.
/// Field layout per texel: four float4, team then {Gx, Gz, Ntot, Ng}, {Ax, Az, Kx, Kz}.
/// </summary>
public static class FieldNeighborReference
{
    public const double Epsilon = 0.001;
    public const int Channels = 6;
    public const int FieldFloats = 16;

    public static int DiskOffsetCount(double radiusTexels)
    {
        int count = 0;
        int lim = (int)Math.Floor(radiusTexels - 1e-4);
        double rt2 = radiusTexels * radiusTexels;
        for (int oz = -lim; oz <= lim; oz++)
        {
            for (int ox = -lim; ox <= lim; ox++)
            {
                if (ox * ox + oz * oz < rt2)
                {
                    count++;
                }
            }
        }

        return count;
    }

    public static double Psi(double distance, double radius, double texel)
    {
        double num = Math.Max(distance, texel);
        return 0.5 * Math.Log((num * num + Epsilon) / (radius * radius + Epsilon));
    }

    public static double[] Collect(Vector2[] positions, Vector2[] headings, int[] teams, SpatialHashLayout layout)
    {
        int cells = layout.CellCount;
        double[] raw = new double[cells * Channels];
        for (int i = 0; i < positions.Length; i++)
        {
            if (teams[i] != 0 && teams[i] != 1)
            {
                continue;
            }

            int channel = teams[i] * 3;
            Vector2 u = (positions[i] - layout.Origin) / layout.CellSize;
            int ix = (int)Math.Floor(u.x);
            int iy = (int)Math.Floor(u.y);
            double fx = u.x - ix;
            double fy = u.y - iy;
            Deposit(raw, layout, ix, iy, (1.0 - fx) * (1.0 - fy), headings[i], channel);
            Deposit(raw, layout, ix + 1, iy, fx * (1.0 - fy), headings[i], channel);
            Deposit(raw, layout, ix, iy + 1, (1.0 - fx) * fy, headings[i], channel);
            Deposit(raw, layout, ix + 1, iy + 1, fx * fy, headings[i], channel);
        }

        return raw;
    }

    public static void Convolve(
        double[] raw,
        SpatialHashLayout layout,
        double radius,
        double[] interMultipliers,
        out double[] phi,
        out double[] field)
    {
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        int cells = layout.CellCount;
        phi = new double[cells * 2];
        field = new double[cells * FieldFloats];
        double texel = layout.CellSize.x;
        double rt = radius / texel;
        int lim = (int)Math.Floor(rt - 1e-4);
        double rt2 = rt * rt;
        for (int y = 0; y < resY; y++)
        {
            for (int x = 0; x < resX; x++)
            {
                double ntot = 0;
                double ng0 = 0;
                double ng1 = 0;
                double ax0 = 0;
                double az0 = 0;
                double ax1 = 0;
                double az1 = 0;
                double kx0 = 0;
                double kz0 = 0;
                double kx1 = 0;
                double kz1 = 0;
                double w0 = 0;
                double w1 = 0;
                for (int oz = -lim; oz <= lim; oz++)
                {
                    for (int ox = -lim; ox <= lim; ox++)
                    {
                        if (ox * ox + oz * oz >= rt2)
                        {
                            continue;
                        }

                        int src = Index(layout, x + ox, y + oz);
                        int b = src * Channels;
                        double n0 = raw[b];
                        double hx0 = raw[b + 1];
                        double hz0 = raw[b + 2];
                        double n1 = raw[b + 3];
                        double hx1 = raw[b + 4];
                        double hz1 = raw[b + 5];
                        double offX = ox * layout.CellSize.x;
                        double offZ = oz * layout.CellSize.y;
                        ntot += n0 + n1;
                        ng0 += n0;
                        ng1 += n1;
                        ax0 += hx0;
                        az0 += hz0;
                        ax1 += hx1;
                        az1 += hz1;
                        kx0 += n0 * offX;
                        kz0 += n0 * offZ;
                        kx1 += n1 * offX;
                        kz1 += n1 * offZ;
                        double dist = Math.Sqrt(offX * offX + offZ * offZ);
                        double psi = Psi(dist, radius, texel);
                        w0 += n0 * psi;
                        w1 += n1 * psi;
                    }
                }

                int cell = y * resX + x;
                double phi0 = w0 + interMultipliers[0] * w1;
                double phi1 = interMultipliers[1] * w0 + w1;
                phi[cell * 2] = phi0;
                phi[cell * 2 + 1] = phi1;
                int f = cell * FieldFloats;
                field[f + 2] = ntot;
                field[f + 3] = ng0;
                field[f + 4] = ax0;
                field[f + 5] = az0;
                field[f + 6] = kx0;
                field[f + 7] = kz0;
                field[f + 8 + 2] = ntot;
                field[f + 8 + 3] = ng1;
                field[f + 8 + 4] = ax1;
                field[f + 8 + 5] = az1;
                field[f + 8 + 6] = kx1;
                field[f + 8 + 7] = kz1;
            }
        }
    }

    public static void Gradient(double[] phi, SpatialHashLayout layout, double[] field)
    {
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        for (int y = 0; y < resY; y++)
        {
            for (int x = 0; x < resX; x++)
            {
                int cell = y * resX + x;
                double px1 = phi[Index(layout, x + 1, y) * 2];
                double px0 = phi[Index(layout, x - 1, y) * 2];
                double pz1 = phi[Index(layout, x, y + 1) * 2];
                double pz0 = phi[Index(layout, x, y - 1) * 2];
                double qx1 = phi[Index(layout, x + 1, y) * 2 + 1];
                double qx0 = phi[Index(layout, x - 1, y) * 2 + 1];
                double qz1 = phi[Index(layout, x, y + 1) * 2 + 1];
                double qz0 = phi[Index(layout, x, y - 1) * 2 + 1];
                int f = cell * FieldFloats;
                field[f] = (px1 - px0) / (2.0 * layout.CellSize.x);
                field[f + 1] = (pz1 - pz0) / (2.0 * layout.CellSize.y);
                field[f + 8] = (qx1 - qx0) / (2.0 * layout.CellSize.x);
                field[f + 9] = (qz1 - qz0) / (2.0 * layout.CellSize.y);
            }
        }
    }

    public static Vector2 UnwrappedCenter(SpatialHashLayout layout, int unwrappedX, int unwrappedY)
    {
        return layout.Origin + new Vector2(
            unwrappedX * layout.CellSize.x,
            unwrappedY * layout.CellSize.y);
    }

    private static void Deposit(
        double[] raw,
        SpatialHashLayout layout,
        int unwrappedX,
        int unwrappedY,
        double weight,
        Vector2 heading,
        int channel)
    {
        int cell = Index(layout, unwrappedX, unwrappedY);
        int b = cell * Channels + channel;
        raw[b] += weight;
        raw[b + 1] += weight * heading.x;
        raw[b + 2] += weight * heading.y;
    }

    private static int Index(SpatialHashLayout layout, int x, int y)
    {
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
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
