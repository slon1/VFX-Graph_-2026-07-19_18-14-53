Shader "M3D/FieldDebug"
{
    Properties
    {
        _MainTex ("Field", 2D) = "black" {}
        _LutTex ("LUT", 2D) = "white" {}
        _Scale ("Color Scale", Float) = 2
        _HdrIntensity ("HDR Intensity", Float) = 1
        // 0 = VectorRg, 1 = ScalarHeatmap (FieldQuadVisualMode)
        _VisualMode ("Visual Mode", Float) = 0
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

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_LutTex);
            SAMPLER(sampler_LutTex);
            float _Scale;
            float _HdrIntensity;
            float _VisualMode;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float4 s = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                if (_VisualMode > 0.5)
                {
                    // ScalarHeatmap: normalize → LUT (LDR stops) → optional HDR boost.
                    float d = saturate(max(s.r, 0.0) * _Scale);
                    float3 lutColor = SAMPLE_TEXTURE2D(_LutTex, sampler_LutTex, float2(d, 0.5)).rgb;
                    float3 color = lutColor * _HdrIntensity;
                    float alpha = saturate(d);
                    return half4(color, alpha * 0.7);
                }

                float2 v = s.rg;
                float3 color = float3(v.x, v.y, 0) * _Scale * 0.5 + 0.5;
                float alpha = saturate(length(v) * _Scale);
                return half4(color, alpha * 0.65);
            }
            ENDHLSL
        }
    }
}
