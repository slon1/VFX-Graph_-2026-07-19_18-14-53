#ifndef M3D_PARTICLE_DIRECTION_INCLUDED
#define M3D_PARTICLE_DIRECTION_INCLUDED

// value = 0.5 when the velocity is zero (length squared below 1e-12).
// The axis is normalized on the CPU.
float DirectionToValue(float3 velocity, float3 axis)
{
    float speedSq = dot(velocity, velocity);
    if (speedSq < 1e-12)
        return 0.5;
    return dot(velocity * rsqrt(speedSq), axis) * 0.5 + 0.5;
}

#endif
