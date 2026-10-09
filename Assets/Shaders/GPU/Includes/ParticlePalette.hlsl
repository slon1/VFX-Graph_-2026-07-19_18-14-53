#ifndef M3D_PARTICLE_PALETTE_INCLUDED
#define M3D_PARTICLE_PALETTE_INCLUDED

// Palette sample from ParticleBillboard frag (ADR-043 A1). No URP. Callers pass already-fetched scalars.
float4 ParticlePaletteColor(
    float value,
    uint teamId,
    float useLut,
    float useTeams,
    float teamCount,
    float scale,
    float4 color,
    Texture2D<float4> lutTex,
    SamplerState lutSampler)
{
    float4 result = color;
    bool useValue = useLut > 0.5;
    bool useTeam = useTeams > 0.5;
    if (useValue || useTeam)
    {
        float d = useValue ? saturate(value * scale) : 0.0;
        float row = 0.5;
        if (useTeam)
        {
            uint t = min(teamId, (uint)max(teamCount - 1.0, 0.0));
            row = (t + 0.5) / max(teamCount, 1.0);
        }

        float4 lut = lutTex.SampleLevel(lutSampler, float2(d, row), 0);
        result = float4(lut.rgb, lut.a * color.a);
    }

    return result;
}

#endif
