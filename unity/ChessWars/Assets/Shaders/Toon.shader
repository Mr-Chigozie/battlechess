// Chess Wars toon look for URP: soft cel bands with a cool shade tint, a rim light, an ink outline and an optional
// world-space stone/grass detail (for the board and ground). Written for low-end phones: no textures on characters,
// one light, SRP Batcher compatible. The outline extrudes along smoothed normals stored in the mesh tangents
// (baked at import by Assets/Editor/CharacterImport.cs), so faceted models don't crack, and skinned ones follow
// their bones. Set the global _CW_OutlineScale to 0 to drop outlines on the Low quality tier.
Shader "ChessWars/Toon"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _ShadeColor ("Shade Tint", Color) = (0.8, 0.8, 0.88, 1)
        _Bands ("Light Bands", Range(1, 4)) = 2
        _Softness ("Band Softness", Range(0.001, 0.5)) = 0.08
        _RimColor ("Rim Color", Color) = (1, 0.97, 0.9, 1)
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.35
        _OutlineWidth ("Outline Width", Range(0, 0.06)) = 0.014
        _OutlineColor ("Outline Color", Color) = (0.07, 0.06, 0.07, 1)
        _DetailTex ("Detail (R)", 2D) = "white" {}
        _DetailScale ("Detail Scale", Float) = 0.5
        _DetailStrength ("Detail Strength", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _EmissionColor;
            float4 _ShadeColor;
            float _Bands;
            float _Softness;
            float4 _RimColor;
            float _RimStrength;
            float _OutlineWidth;
            float4 _OutlineColor;
            float4 _DetailTex_ST;
            float _DetailScale;
            float _DetailStrength;
        CBUFFER_END
        TEXTURE2D(_DetailTex);
        SAMPLER(sampler_DetailTex);
        float _CW_OutlineScale;   // global: 1 normally, 0 on the Low tier
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fog : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // A world-space stone/grass grain, projected from three sides so it never stretches.
            half Detail(float3 positionWS, float3 n)
            {
                float3 w = abs(n);
                w /= (w.x + w.y + w.z + 1e-4);
                half x = SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, positionWS.zy * _DetailScale).r;
                half y = SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, positionWS.xz * _DetailScale).r;
                half z = SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, positionWS.xy * _DetailScale).r;
                return x * w.x + y * w.y + z * w.z;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 n = normalize(input.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float ndl = dot(n, light.direction);
                float lit = saturate(ndl) * light.shadowAttenuation;
                // Soft-edged bands: the cel look without hard aliasing.
                float scaled = lit * _Bands;
                float band = (floor(scaled) + smoothstep(0.5 - _Softness, 0.5 + _Softness, frac(scaled))) / _Bands;

                half3 albedo = _BaseColor.rgb;
                if (_DetailStrength > 0)
                    albedo *= lerp(1.0, 0.55 + Detail(input.positionWS, n) * 0.9, _DetailStrength);

                half3 ambient = SampleSH(n);
                half3 shaded = albedo * _ShadeColor.rgb * (ambient * 0.9 + 0.35);
                half3 color = lerp(shaded, albedo * (light.color + ambient * 0.35), band);

                // Rim: a bright edge on the lit side that lifts silhouettes off the background.
                float3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * _RimStrength * saturate(ndl * 0.6 + 0.6);
                color += rim * _RimColor.rgb * light.color;
                color += _EmissionColor.rgb;
                color = MixFog(color, input.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // Ink outline: the back faces pushed out along smoothed normals (kept in the tangents).
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Front
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float fog : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 dir = dot(input.tangentOS.xyz, input.tangentOS.xyz) > 0.01 ? normalize(input.tangentOS.xyz) : input.normalOS;
                float width = _OutlineWidth * _CW_OutlineScale;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz + dir * width);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(MixFog(_OutlineColor.rgb, input.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float4 Vert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 Vert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return TransformObjectToHClip(input.positionOS.xyz);
            }
            half Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack Off
}
