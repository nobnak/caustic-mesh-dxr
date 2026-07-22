using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CausticMeshDxr
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class NoiseGridReceiverGeometry : CausticReceiverGeometry
    {
        const int ThreadCount = 64;

        enum GridPlane
        {
            XZ,
            XY,
        }

        static readonly int BasePositionsId = Shader.PropertyToID("_BasePositions");
        static readonly int VertexBufferId = Shader.PropertyToID("_VertexBuffer");
        static readonly int IndexBufferId = Shader.PropertyToID("_IndexBuffer");
        static readonly int PrimitiveNormalsId = Shader.PropertyToID("_PrimitiveNormals");
        static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
        static readonly int PrimitiveCountId = Shader.PropertyToID("_PrimitiveCount");
        static readonly int VertexStrideId = Shader.PropertyToID("_VertexStride");
        static readonly int PositionOffsetId = Shader.PropertyToID("_PositionOffset");
        static readonly int NormalOffsetId = Shader.PropertyToID("_NormalOffset");
        static readonly int PrimitiveOffsetId = Shader.PropertyToID("_PrimitiveOffset");
        static readonly int SizeId = Shader.PropertyToID("_Size");
        static readonly int AmplitudeId = Shader.PropertyToID("_Amplitude");
        static readonly int FrequencyId = Shader.PropertyToID("_Frequency");
        static readonly int AnimationOffsetId = Shader.PropertyToID("_AnimationOffset");
        static readonly int NormalToWorldId = Shader.PropertyToID("_NormalToWorld");
        static readonly int GridPlaneId = Shader.PropertyToID("_GridPlane");

        [SerializeField] ComputeShader deformationShader;
        [SerializeField] GridPlane gridPlane = GridPlane.XZ;
        [SerializeField] Vector2 size = new(10, 10);
        [SerializeField, Min(1)] int cellsPerAxis = 64;
        [SerializeField, Min(0)] float amplitude = 0.2f;
        [SerializeField, Min(0.0001f)] float frequency = 0.35f;
        [SerializeField] Vector2 animationDirection = new(0.23f, 0.17f);
        [SerializeField, Min(0)] float animationSpeed = 0.4f;
        [SerializeField] bool animate = true;

        Mesh runtimeMesh;
        Mesh originalMesh;
        GraphicsBuffer basePositionBuffer;
        GraphicsBuffer meshVertexBuffer;
        GraphicsBuffer meshIndexBuffer;
        int deformKernel;
        int buildPrimitiveNormalsKernel;
        int vertexCount;
        int primitiveCount;
        int vertexStride;
        int positionOffset;
        int normalOffset;
        bool geometryDirty = true;
        bool topologyDirty = true;
        uint topologyVersion = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct GridVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
        }

        public override bool IsTimeVarying => animate && animationSpeed > 0 && amplitude > 0;

        public override uint TopologyVersion => topologyVersion;

        public override GraphicsBuffer VertexBuffer => meshVertexBuffer;

        public override GraphicsBuffer IndexBuffer => meshIndexBuffer;

        public override int VertexStride => vertexStride;

        public override int PositionOffset => positionOffset;

        public override Mesh PrepareMesh()
        {
            if (deformationShader == null)
            {
                Debug.LogError("Noise Grid Receiver requires a deformation Compute Shader.", this);
                return null;
            }
            if (topologyDirty || runtimeMesh == null)
                RebuildMesh();
            return runtimeMesh;
        }

        public override bool DispatchGeometry(
            float time,
            GraphicsBuffer primitiveNormalBuffer,
            int primitiveOffset,
            Matrix4x4 normalToWorld,
            bool forceNormalUpdate)
        {
            if (deformationShader == null || primitiveNormalBuffer == null)
                return false;

            PrepareMesh();
            var updateVertices = geometryDirty || IsTimeVarying;
            if (!updateVertices && !forceNormalUpdate)
                return false;

            if (updateVertices)
            {
                deformationShader.SetBuffer(deformKernel, BasePositionsId, basePositionBuffer);
                deformationShader.SetBuffer(deformKernel, VertexBufferId, meshVertexBuffer);
                deformationShader.SetInt(VertexCountId, vertexCount);
                deformationShader.SetInt(VertexStrideId, vertexStride);
                deformationShader.SetInt(PositionOffsetId, positionOffset);
                deformationShader.SetInt(NormalOffsetId, normalOffset);
                deformationShader.SetInt(GridPlaneId, (int)gridPlane);
                deformationShader.SetVector(SizeId, new Vector4(size.x, size.y, 0, 0));
                deformationShader.SetFloat(AmplitudeId, amplitude);
                deformationShader.SetFloat(FrequencyId, frequency);
                deformationShader.SetVector(
                    AnimationOffsetId,
                    animate ? animationDirection * (animationSpeed * time) : Vector2.zero);
                deformationShader.Dispatch(deformKernel, DivideRoundUp(vertexCount, ThreadCount), 1, 1);
                geometryDirty = false;
            }

            deformationShader.SetBuffer(buildPrimitiveNormalsKernel, VertexBufferId, meshVertexBuffer);
            deformationShader.SetBuffer(buildPrimitiveNormalsKernel, IndexBufferId, meshIndexBuffer);
            deformationShader.SetBuffer(buildPrimitiveNormalsKernel, PrimitiveNormalsId, primitiveNormalBuffer);
            deformationShader.SetInt(PrimitiveCountId, primitiveCount);
            deformationShader.SetInt(VertexStrideId, vertexStride);
            deformationShader.SetInt(PositionOffsetId, positionOffset);
            deformationShader.SetInt(PrimitiveOffsetId, primitiveOffset);
            deformationShader.SetMatrix(NormalToWorldId, normalToWorld);
            deformationShader.Dispatch(
                buildPrimitiveNormalsKernel,
                DivideRoundUp(primitiveCount, ThreadCount),
                1,
                1);
            return updateVertices;
        }

        void RebuildMesh()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (runtimeMesh == null && meshFilter.sharedMesh != null)
                originalMesh = meshFilter.sharedMesh;
            ReleaseMesh();
            var safeCells = Mathf.Clamp(cellsPerAxis, 1, 512);
            var safeSize = new Vector2(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y));
            var row = safeCells + 1;
            vertexCount = row * row;
            primitiveCount = safeCells * safeCells * 2;
            var vertices = new GridVertex[vertexCount];
            var basePositions = new Vector3[vertexCount];
            var indices = new uint[primitiveCount * 3];

            for (var z = 0; z <= safeCells; z++)
            {
                for (var x = 0; x <= safeCells; x++)
                {
                    var uv = new Vector2((float)x / safeCells, (float)z / safeCells);
                    var axis0 = (uv.x - 0.5f) * safeSize.x;
                    var axis1 = (uv.y - 0.5f) * safeSize.y;
                    var position = gridPlane == GridPlane.XZ
                        ? new Vector3(axis0, 0, axis1)
                        : new Vector3(axis0, axis1, 0);
                    var index = z * row + x;
                    basePositions[index] = position;
                    vertices[index] = new GridVertex
                    {
                        position = position,
                        normal = gridPlane == GridPlane.XZ ? Vector3.up : Vector3.back,
                        uv = uv,
                    };
                }
            }

            var writeIndex = 0;
            for (var z = 0; z < safeCells; z++)
            {
                for (var x = 0; x < safeCells; x++)
                {
                    var v00 = (uint)(z * row + x);
                    var v10 = v00 + 1;
                    var v01 = v00 + (uint)row;
                    var v11 = v01 + 1;
                    indices[writeIndex++] = v00;
                    indices[writeIndex++] = v01;
                    indices[writeIndex++] = v10;
                    indices[writeIndex++] = v10;
                    indices[writeIndex++] = v01;
                    indices[writeIndex++] = v11;
                }
            }

            runtimeMesh = new Mesh { name = $"Noise Receiver Grid ({safeCells}x{safeCells})" };
            runtimeMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            runtimeMesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            runtimeMesh.SetVertexBufferParams(
                vertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
            runtimeMesh.SetVertexBufferData(vertices, 0, 0, vertexCount);
            runtimeMesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            runtimeMesh.SetIndexBufferData(indices, 0, 0, indices.Length);
            runtimeMesh.subMeshCount = 1;
            runtimeMesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length, MeshTopology.Triangles));
            runtimeMesh.bounds = new Bounds(
                Vector3.zero,
                gridPlane == GridPlane.XZ
                    ? new Vector3(safeSize.x, Mathf.Max(0.01f, amplitude * 2), safeSize.y)
                    : new Vector3(safeSize.x, safeSize.y, Mathf.Max(0.01f, amplitude * 2)));
            meshFilter.sharedMesh = runtimeMesh;

            basePositionBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexCount,
                Marshal.SizeOf<Vector3>());
            basePositionBuffer.SetData(basePositions);
            meshVertexBuffer = runtimeMesh.GetVertexBuffer(0);
            meshIndexBuffer = runtimeMesh.GetIndexBuffer();
            vertexStride = runtimeMesh.GetVertexBufferStride(0);
            positionOffset = runtimeMesh.GetVertexAttributeOffset(VertexAttribute.Position);
            normalOffset = runtimeMesh.GetVertexAttributeOffset(VertexAttribute.Normal);
            deformKernel = deformationShader != null ? deformationShader.FindKernel("DeformNoiseGrid") : -1;
            buildPrimitiveNormalsKernel = deformationShader != null
                ? deformationShader.FindKernel("BuildPrimitiveNormals")
                : -1;
            size = safeSize;
            cellsPerAxis = safeCells;
            topologyDirty = false;
            geometryDirty = true;
        }

        void OnValidate()
        {
            size.x = Mathf.Max(0.001f, size.x);
            size.y = Mathf.Max(0.001f, size.y);
            cellsPerAxis = Mathf.Clamp(cellsPerAxis, 1, 512);
            amplitude = Mathf.Max(0, amplitude);
            frequency = Mathf.Max(0.0001f, frequency);
            animationSpeed = Mathf.Max(0, animationSpeed);
            topologyDirty = true;
            geometryDirty = true;
            topologyVersion++;
        }

        void OnDisable()
        {
            ReleaseMesh();
        }

        void ReleaseMesh()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (runtimeMesh != null && meshFilter.sharedMesh == runtimeMesh)
                meshFilter.sharedMesh = originalMesh;
            meshVertexBuffer?.Dispose();
            meshVertexBuffer = null;
            meshIndexBuffer?.Dispose();
            meshIndexBuffer = null;
            basePositionBuffer?.Dispose();
            basePositionBuffer = null;
            if (runtimeMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(runtimeMesh);
                else
                    DestroyImmediate(runtimeMesh);
                runtimeMesh = null;
            }
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }
    }
}
