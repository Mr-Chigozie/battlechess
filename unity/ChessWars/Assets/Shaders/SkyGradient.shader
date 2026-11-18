// A stylized sky for the battlefields: ground colour below, a bright horizon band, the sky colour above,
// and a soft sun glow. Cheap: one gradient per pixel, no textures. Colours are set per world by Battlefield.cs.
Shader "ChessWars/SkyGradient"
{
    Properties
    {
        _Top ("Sky", Color) = (0.36, 0.6, 0.85, 1)
        _Horizon ("Horizon", Color) = (0.88, 0.92, 0.9, 1)
        _Bottom ("Below Horizon", Color) = (0.3, 0.35, 0.3, 1)
        _HorizonWidth ("Horizon Width", Range(0.01, 1)) = 0.28
        _SunDir ("Sun Direction", Vector) = (-0.4, 0.5, 0.6, 0)
        _SunColor ("Sun Glow", Color) = (1, 0.92, 0.75, 1)
        _SunSize ("Sun Glow Size", Range(1, 256)) = 24
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Top, _Horizon, _Bottom, _SunDir, _SunColor;
                float _HorizonWidth, _SunSize;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.dir = input.positionOS.xyz;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.dir);
                float up = saturate(d.y / _HorizonWidth);
                float down = saturate(-d.y / (_HorizonWidth * 0.5));
                half3 color = lerp(_Horizon.rgb, _Top.rgb, smoothstep(0, 1, up));
                color = lerp(color, _Bottom.rgb, smoothstep(0, 1, down));
                float sun = pow(saturate(dot(d, normalize(_SunDir.xyz))), _SunSize);
                color += _SunColor.rgb * sun * 0.6;
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
