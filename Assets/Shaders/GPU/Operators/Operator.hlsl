// Single entry point for the GPU operator pipeline.
// Add / chain operators here — ParticleSimulate.compute must not change.

#include "Assets/Shaders/GPU/Operators/TwistOperator.hlsl"

float OperatorStrength;

float3 ApplyOperator(float3 position)
{
    return Twist(position, OperatorStrength);
}
