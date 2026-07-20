Shader "Hidden/Caustics/Source Grid Outline"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct SourceVertex
            {
                float3 position;
                float3 normal;
            };

            StructuredBuffer<SourceVertex> _SourceVertices;
            StructuredBuffer<uint> _SourceIndices;
            StructuredBuffer<uint> _SourceLineIndices;
            float4x4 _SourceLocalToWorld;
            float4x4 _SourceNormalToWorld;
            float4 _Color;
            float4 _SourceSurfaceColor;
            float _SourceSmoothness;
            float _SourceRefractionStrength;
            float _SourceMainLightIntensity;
            float _SourceAmbientIntensity;
            float _SourceSpecularIntensity;
            float _SourceFresnelIntensity;
            float _SourceRefractionLitBlend;
            uint _SourceDisplayMode;
            TEXTURE2D_X(_CausticBackgroundTexture);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                const uint sourceVertexIndex = _SourceDisplayMode == 1
                    ? _SourceLineIndices[vertexId]
                    : _SourceIndices[vertexId];
                const SourceVertex sourceVertex = _SourceVertices[sourceVertexIndex];
                Varyings output;
                output.positionWS = mul(
                    _SourceLocalToWorld,
                    float4(sourceVertex.position, 1)).xyz;
                output.normalWS = normalize(mul((float3x3)_SourceNormalToWorld, sourceVertex.normal));
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            float3 EvaluateSurfaceLighting(float3 positionWS, float3 normalWS)
            {
                const Light mainLight = GetMainLight();
                const float normalDotLight = saturate(dot(normalWS, mainLight.direction));
                const float3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                const float3 halfDirection = normalize(mainLight.direction + viewDirectionWS);
                const float specularPower = exp2(1 + 10 * _SourceSmoothness);
                const float specular = pow(saturate(dot(normalWS, halfDirection)), specularPower);
                const float fresnel = 0.02
                    + 0.98 * pow(1 - saturate(dot(normalWS, viewDirectionWS)), 5);
                const float3 ambient = SampleSH(normalWS);
                return _SourceSurfaceColor.rgb * mainLight.color * normalDotLight * _SourceMainLightIntensity
                    + _SourceSurfaceColor.rgb * ambient * _SourceAmbientIntensity
                    + mainLight.color
                        * specular
                        * lerp(0.1, 1, _SourceSmoothness)
                        * _SourceSpecularIntensity
                    + fresnel * _SourceFresnelIntensity;
            }

            float4 Frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                if (_SourceDisplayMode == 1)
                    return _Color;

                if (_SourceDisplayMode == 3)
                {
                    if (!isFrontFace)
                        discard;

                    const float3 normalWS = normalize(input.normalWS);
                    const float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    const float3 normalVS = mul((float3x3)UNITY_MATRIX_V, normalWS);
                    const float2 refractionOffset = normalVS.xy
                        * _SourceRefractionStrength
                        / _ScreenParams.xy;
                    const float2 refractedUV = UnityStereoTransformScreenSpaceTex(
                        saturate(screenUV + refractionOffset));
                    const float3 refractedColor = SAMPLE_TEXTURE2D_X(
                        _CausticBackgroundTexture,
                        sampler_LinearClamp,
                        refractedUV).rgb;
                    const float3 tintedRefraction = lerp(
                        refractedColor,
                        refractedColor * _SourceSurfaceColor.rgb,
                        _SourceSurfaceColor.a);
                    const float3 litSurface = EvaluateSurfaceLighting(input.positionWS, normalWS);
                    return float4(lerp(
                        tintedRefraction,
                        litSurface,
                        _SourceRefractionLitBlend), 1);
                }

                const float3 normalWS = normalize(input.normalWS) * (isFrontFace ? 1 : -1);
                return float4(
                    EvaluateSurfaceLighting(input.positionWS, normalWS),
                    _SourceSurfaceColor.a);
            }
            ENDHLSL
        }
    }
}
