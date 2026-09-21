Shader "M3D/ParticleBillboard"
{
    Properties
    {
        _Size ("Size", Float) = 0.05
        _Color ("Color", Color) = (1,1,1,1)
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

            StructuredBuffer<float3> _Positions;
            float _Size;
            float4 _Color;

            static const float2 QuadOffsets[6] =
            {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(-0.5, 0.5),
                float2(-0.5, 0.5), float2(0.5, -0.5), float2(0.5, 0.5),
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                float3 centerWS = _Positions[instanceID];
                float2 offset = QuadOffsets[vertexID % 6] * _Size;
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float3 posWS = centerWS + right * offset.x + up * offset.y;

                Varyings output;
                output.positionCS = TransformWorldToHClip(posWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
}
