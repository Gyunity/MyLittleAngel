// 강아지 발밑 블롭 그림자용 셰이더.
// 가운데가 진하고 가장자리로 갈수록 옅어지는 원을 텍스처 없이 UV로 계산하고,
// 곱하기(Multiply) 블렌딩으로 카메라 영상 위에 어둡게 얹는다.
Shader "MyLittleAngel/BlobShadow"
{
    Properties
    {
        _Color ("Shadow Color", Color) = (0, 0, 0, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.5
        _Hardness ("Hardness", Range(0, 0.99)) = 0.2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-10"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "BlobShadow"
            Tags { "LightMode" = "UniversalForward" }

            // 결과 = 화면색 x 출력색 → 출력이 1이면 그대로, 0에 가까울수록 어두워짐
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Off
            // 바닥과 같은 높이에서 깜빡이지 않도록 깊이를 살짝 당김
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Opacity;
                half _Hardness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 중심 0, 가장자리 1
                half d = length(input.uv * 2.0 - 1.0);
                half a = 1.0 - smoothstep(_Hardness, 1.0, d);
                a *= a; // 가장자리를 더 부드럽게
                a *= _Opacity;
                return half4(lerp(half3(1, 1, 1), _Color.rgb, a), 1);
            }
            ENDHLSL
        }
    }
}
