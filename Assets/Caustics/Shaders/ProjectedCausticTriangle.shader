Shader "Hidden/Caustics/Projected Triangle"
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
            Blend One One
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "UnityCG.cginc"

            struct RayHit
            {
                float3 position;
                float distance;
                uint instanceId;
                uint primitiveIndex;
                uint valid;
                uint padding;
            };

            struct TriangleResult
            {
                float intensity;
                float incidentArea;
                float receiverArea;
                uint valid;
                float3 renderNormal;
                float padding;
            };

            StructuredBuffer<uint> _SourceIndices;
            StructuredBuffer<RayHit> _Hits;
            StructuredBuffer<TriangleResult> _TriangleResults;
            StructuredBuffer<float3> _RenderNormals;
            float4 _Color;
            float _IntensityScale;
            float _SurfaceOffset;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float intensity : TEXCOORD0;
                nointerpolation float valid : TEXCOORD1;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                const uint triangleIndex = vertexId / 3;
                const uint sourceVertexIndex = _SourceIndices[vertexId];
                const TriangleResult triangleResult = _TriangleResults[triangleIndex];
                const float3 positionWS = _Hits[sourceVertexIndex].position
                    + _RenderNormals[sourceVertexIndex] * _SurfaceOffset;
                Varyings output;
                output.positionCS = mul(UNITY_MATRIX_VP, float4(positionWS, 1));
                output.intensity = triangleResult.intensity * _IntensityScale;
                output.valid = triangleResult.valid;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                clip(input.valid - 0.5);
                return _Color * input.intensity;
            }
            ENDHLSL
        }
    }
}
