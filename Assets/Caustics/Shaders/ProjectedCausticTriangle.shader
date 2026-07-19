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
                float3 receiverNormal;
                float normalPadding;
            };

            struct ProjectedResult
            {
                float intensity;
                uint receiverBoundary;
                uint valid;
                uint padding;
            };

            StructuredBuffer<RayHit> _Hits;
            StructuredBuffer<RayHit> _EdgeHits;
            StructuredBuffer<uint> _ProjectedIndices;
            StructuredBuffer<ProjectedResult> _ProjectedResults;
            uint _VertexCount;
            float4 _Color;
            float _IntensityScale;
            float _SurfaceOffset;
            int _ShowReceiverBoundaries;
            float4 _ReceiverBoundaryColor;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float intensity : TEXCOORD0;
                nointerpolation float valid : TEXCOORD1;
                nointerpolation float receiverBoundary : TEXCOORD2;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                Varyings output;
                const uint triangleIndex = vertexId / 3;
                const uint vertexReference = _ProjectedIndices[vertexId];
                RayHit hit = (RayHit)0;
                if (vertexReference < _VertexCount)
                    hit = _Hits[vertexReference];
                else
                    hit = _EdgeHits[vertexReference - _VertexCount];
                const ProjectedResult projectedResult = _ProjectedResults[triangleIndex];
                const float3 positionWS = hit.position + hit.receiverNormal * _SurfaceOffset;
                output.positionCS = mul(UNITY_MATRIX_VP, float4(positionWS, 1));
                output.intensity = projectedResult.intensity * _IntensityScale;
                output.valid = projectedResult.valid;
                output.receiverBoundary = projectedResult.receiverBoundary;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                clip(input.valid - 0.5);
                if (_ShowReceiverBoundaries != 0 && input.receiverBoundary > 0.5)
                    return _ReceiverBoundaryColor;
                return _Color * input.intensity;
            }
            ENDHLSL
        }
    }
}
