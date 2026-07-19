// Pure twist around Y. strength is a precomputed coefficient from CPU
// (e.g. twistStrength * simulationSpeed * deltaTime). No DeltaTime here.

float3 Twist(float3 position, float strength)
{
    float angle = position.y * strength;
    float s;
    float c;
    sincos(angle, s, c);

    float3 result = position;
    result.x = position.x * c - position.z * s;
    result.z = position.x * s + position.z * c;
    return result;
}
