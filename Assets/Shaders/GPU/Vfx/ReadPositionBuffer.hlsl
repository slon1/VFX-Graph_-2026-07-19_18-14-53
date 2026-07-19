void ReadPositionBuffer(inout VFXAttributes attributes, in StructuredBuffer<float3> PositionBuffer)
{
    attributes.position = PositionBuffer[attributes.particleId];
}
