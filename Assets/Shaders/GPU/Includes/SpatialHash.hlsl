#ifndef M3D_SPATIAL_HASH_INCLUDED
#define M3D_SPATIAL_HASH_INCLUDED

// XZ grid of the spatial hash (ADR-034 §2). Pushed by SpatialHashSet.PushParams.
float2 HashOrigin;
float2 HashSize;
float2 HashCellSize;
int2 HashRes;
int HashWrap;
uint HashCellCount;

int2 HashCellCoord(float2 xz)
{
    int2 c = (int2)floor((xz - HashOrigin) / HashCellSize);
    if (HashWrap != 0)
    {
        c = ((c % HashRes) + HashRes) % HashRes;
    }
    else
    {
        c = clamp(c, int2(0, 0), HashRes - 1);
    }

    return c;
}

// min() keeps a NaN/huge position inside the buffers.
uint HashCellIndex(int2 c)
{
    uint index = (uint)(c.y * HashRes.x + c.x);
    return min(index, HashCellCount - 1u);
}

#endif
