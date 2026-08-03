// Shared field plane sampling — used by FieldPasses, P2GPasses, GradientPasses.

#ifndef M3D_FIELD_SAMPLING_HLSL
#define M3D_FIELD_SAMPLING_HLSL

int2 FieldResolution;
float2 FieldTexelSize;
float3 FieldOrigin;
float3 FieldAxisU;
float3 FieldAxisV;
float2 FieldSize;

float2 WorldToFieldUV(float3 worldPos)
{
    float3 local = worldPos - FieldOrigin;
    float u = dot(local, FieldAxisU) / max(FieldSize.x, 1e-5) + 0.5;
    float v = dot(local, FieldAxisV) / max(FieldSize.y, 1e-5) + 0.5;
    return float2(u, v);
}

float3 FieldUVToWorldVelocity(float2 fieldVel)
{
    // Field stores velocity in plane axes (RG = U/V components).
    return FieldAxisU * fieldVel.x + FieldAxisV * fieldVel.y;
}

// UV-space gradient → world direction along field plane axes (no /FieldSize).
float3 FieldUvGradientToWorld(float2 gradUv)
{
    return FieldAxisU * gradUv.x + FieldAxisV * gradUv.y;
}

#endif
