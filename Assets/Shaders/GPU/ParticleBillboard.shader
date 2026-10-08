Shader "M3D/ParticleBillboard"
{
    Properties
    {
        _Size ("Size", Float) = 0.05
        _Color ("Color", Color) = (1,1,1,1)
        _Scale ("Value Scale", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/GPU/Includes/ParticlePalette.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<float> _Values;
            StructuredBuffer<uint> _TeamIds;
            Texture2D<float4> _LutTex;
            SamplerState sampler_LutTex;
            float _Size;
            float4 _Color;
            float _Scale;
            float _UseLut;
            float _UseTeams;
            float _TeamCount;

            static const float2 QuadOffsets[6] =
            {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(-0.5, 0.5),
                float2(-0.5, 0.5), float2(0.5, -0.5), float2(0.5, 0.5),
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float4 color : TEXCOORD0;
            };

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                float3 centerWS = _Positions[instanceID];
                float2 offset = QuadOffsets[vertexID % 6] * _Size;
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float3 posWS = centerWS + right * offset.x + up * offset.y;

                // Dummy _Values and _TeamIds are length 1. Read only the buffer the flag turns on.
                float value = 0.0;
                uint team = 0;
                if (_UseLut > 0.5)
                    value = _Values[instanceID];
                if (_UseTeams > 0.5)
                    team = _TeamIds[instanceID];

                Varyings output;
                output.positionCS = TransformWorldToHClip(posWS);
                output.color = ParticlePaletteColor(
                    value, team, _UseLut, _UseTeams, _TeamCount, _Scale, _Color, _LutTex, sampler_LutTex);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return (half4)input.color;
            }
            ENDHLSL
        }
    }
}
