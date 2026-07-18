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

            #include "UnityCG.cginc"

            struct SourceVertex
            {
                float3 position;
                float3 normal;
            };

            StructuredBuffer<SourceVertex> _SourceVertices;
            StructuredBuffer<uint> _SourceLineIndices;
            float4x4 _SourceLocalToWorld;
            float4 _Color;

            float4 Vert(uint vertexId : SV_VertexID) : SV_POSITION
            {
                const uint sourceVertexIndex = _SourceLineIndices[vertexId];
                const float3 positionWS = mul(
                    _SourceLocalToWorld,
                    float4(_SourceVertices[sourceVertexIndex].position, 1)).xyz;
                return mul(UNITY_MATRIX_VP, float4(positionWS, 1));
            }

            float4 Frag() : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }
}
