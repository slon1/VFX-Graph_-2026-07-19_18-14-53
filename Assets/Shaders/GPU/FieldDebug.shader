Shader "M3D/FieldDebug"
{
    Properties
    {
        _MainTex ("Field", 2D) = "black" {}
        _Scale ("Color Scale", Float) = 2
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
            float _Scale;

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
                float2 v = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rg;
                float3 color = float3(v.x, v.y, 0) * _Scale * 0.5 + 0.5;
                float alpha = saturate(length(v) * _Scale);
                return half4(color, alpha * 0.65);
            }
            ENDHLSL
        }
    }
}
