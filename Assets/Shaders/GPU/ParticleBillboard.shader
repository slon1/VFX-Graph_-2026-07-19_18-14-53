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
                nointerpolation uint instanceID : TEXCOORD0;
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
                output.instanceID = instanceID;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                bool useValue = _UseLut > 0.5;
                bool useTeam = _UseTeams > 0.5;
                if (useValue || useTeam)
                {
                    float d = useValue ? saturate(_Values[input.instanceID] * _Scale) : 0.0;
                    float row = 0.5;
                    if (useTeam)
                    {
                        uint t = min(_TeamIds[input.instanceID], (uint)max(_TeamCount - 1.0, 0.0));
                        row = (t + 0.5) / max(_TeamCount, 1.0);
                    }

                    float4 lut = _LutTex.SampleLevel(sampler_LutTex, float2(d, row), 0);
                    return half4(lut.rgb, lut.a * _Color.a);
                }

                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
}
