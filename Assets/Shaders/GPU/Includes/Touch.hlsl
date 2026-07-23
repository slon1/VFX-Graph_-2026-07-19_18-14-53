// Touch input shared types. Layout must match the C# TouchForce struct (SimContext.cs).

#ifndef M3D_TOUCH_INCLUDED
#define M3D_TOUCH_INCLUDED

struct TouchForce
{
    float3 pos;      // world position on the interaction plane
    float3 delta;    // world movement since last frame
    float radius;    // influence radius
    float strength;  // overall multiplier
};

// Quadratic falloff: 1 at the touch point, 0 at radius.
float TouchFalloff(float dist, float radius)
{
    float x = saturate(1.0 - dist / max(radius, 1e-4));
    return x * x;
}

#endif // M3D_TOUCH_INCLUDED
