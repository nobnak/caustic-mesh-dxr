using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    public sealed class CausticRayQueryTest : MonoBehaviour
    {
        const int ComputeThreadCount = 64;
        const int MaxCellCountPerAxis = 512;
        const uint ReceiverMask = 1;
        static readonly uint[] ZeroSubdivisionCounter = { 0 };
        static readonly HashSet<CausticRayQueryTest> ActiveInstances = new();

        static readonly int AccelerationStructureId = Shader.PropertyToID("_AccelerationStructure");
        static readonly int SourceVerticesId = Shader.PropertyToID("_SourceVertices");
        static readonly int SourceIndicesId = Shader.PropertyToID("_SourceIndices");
        static readonly int SourceLineIndicesId = Shader.PropertyToID("_SourceLineIndices");
        static readonly int HitsId = Shader.PropertyToID("_Hits");
        static readonly int TriangleResultsId = Shader.PropertyToID("_TriangleResults");
        static readonly int ReceiverPrimitiveOffsetsId = Shader.PropertyToID("_ReceiverPrimitiveOffsets");
        static readonly int ReceiverPrimitiveNormalsId = Shader.PropertyToID("_ReceiverPrimitiveNormals");
        static readonly int SourceLocalToWorldId = Shader.PropertyToID("_SourceLocalToWorld");
        static readonly int SourceNormalToWorldId = Shader.PropertyToID("_SourceNormalToWorld");
        static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
        static readonly int TriangleCountId = Shader.PropertyToID("_TriangleCount");
        static readonly int IncidentDirectionId = Shader.PropertyToID("_IncidentDirection");
        static readonly int EtaId = Shader.PropertyToID("_Eta");
        static readonly int RayTMinId = Shader.PropertyToID("_RayTMin");
        static readonly int RayTMaxId = Shader.PropertyToID("_RayTMax");
        static readonly int MinReceiverAreaId = Shader.PropertyToID("_MinReceiverArea");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int IntensityScaleId = Shader.PropertyToID("_IntensityScale");
        static readonly int SurfaceOffsetId = Shader.PropertyToID("_SurfaceOffset");
        static readonly int ReceiverBoundaryNormalCosId = Shader.PropertyToID("_ReceiverBoundaryNormalCos");
        static readonly int ShowReceiverBoundariesId = Shader.PropertyToID("_ShowReceiverBoundaries");
        static readonly int ReceiverBoundaryColorId = Shader.PropertyToID("_ReceiverBoundaryColor");
        static readonly int SourceBlendId = Shader.PropertyToID("_SrcBlend");
        static readonly int DestinationBlendId = Shader.PropertyToID("_DstBlend");
        static readonly int SourceEdgesId = Shader.PropertyToID("_SourceEdges");
        static readonly int TriangleEdgesId = Shader.PropertyToID("_TriangleEdges");
        static readonly int EdgeFlagsId = Shader.PropertyToID("_EdgeFlags");
        static readonly int EdgeHitsId = Shader.PropertyToID("_EdgeHits");
        static readonly int ProjectedIndicesId = Shader.PropertyToID("_ProjectedIndices");
        static readonly int ProjectedResultsId = Shader.PropertyToID("_ProjectedResults");
        static readonly int OutputCounterId = Shader.PropertyToID("_OutputCounter");
        static readonly int OutputArgsId = Shader.PropertyToID("_OutputArgs");
        static readonly int EdgeCountId = Shader.PropertyToID("_EdgeCount");
        static readonly int MaxOutputTrianglesId = Shader.PropertyToID("_MaxOutputTriangles");
        static readonly int EnableSubdivisionId = Shader.PropertyToID("_EnableSubdivision");
        static readonly int SourceDisplayModeId = Shader.PropertyToID("_SourceDisplayMode");
        static readonly int SourceSurfaceColorId = Shader.PropertyToID("_SourceSurfaceColor");
        static readonly int SourceSmoothnessId = Shader.PropertyToID("_SourceSmoothness");
        static readonly int SourceRefractionStrengthId = Shader.PropertyToID("_SourceRefractionStrength");
        static readonly int SourceMainLightIntensityId = Shader.PropertyToID("_SourceMainLightIntensity");
        static readonly int SourceAmbientIntensityId = Shader.PropertyToID("_SourceAmbientIntensity");
        static readonly int SourceSpecularIntensityId = Shader.PropertyToID("_SourceSpecularIntensity");
        static readonly int SourceFresnelIntensityId = Shader.PropertyToID("_SourceFresnelIntensity");
        static readonly int SourceRefractionLitBlendId = Shader.PropertyToID("_SourceRefractionLitBlend");
        static readonly int HeightMapId = Shader.PropertyToID("_HeightMap");
        static readonly int HeightMapSizeId = Shader.PropertyToID("_HeightMapSize");
        static readonly int GridCellSizeId = Shader.PropertyToID("_GridCellSize");
        static readonly int ActualSourceSizeId = Shader.PropertyToID("_ActualSourceSize");

        [Header("Scene")]
        [SerializeField] MeshRenderer[] receivers;
        [SerializeField] Light directionalLight;
        [SerializeField] ComputeShader rayQueryShader;
        [SerializeField] Shader projectedTriangleShader;
        [SerializeField] Shader sourceGridOutlineShader;

        [Header("Input Surface")]
        [SerializeField] Vector2 sourceSize = new(2, 2);
        [SerializeField, Min(0.001f)] float cellSize = 0.25f;
        [SerializeField] CausticHeightField heightField;

        [Header("Refraction")]
        [SerializeField, Min(1)] float transmittedRefractiveIndex = 1.333f;
        [SerializeField, Min(0.000001f)] float rayTMin = 0.001f;
        [SerializeField, Min(0.01f)] float rayTMax = 100;
        [SerializeField, Min(0.00000001f)] float minReceiverArea = 0.00001f;

        [Header("Display")]
        [SerializeField] CausticBlendMode causticBlendMode = CausticBlendMode.ModulatedAdditive;
        [SerializeField] Color causticColor = new(1, 0.7f, 0.1f, 1);
        [SerializeField, Min(0)] float intensityScale = 0.25f;
        [SerializeField, Min(0)] float surfaceOffset = 0.002f;
        [SerializeField] bool showReceiverBoundaries;
        [SerializeField] Color receiverBoundaryColor = Color.magenta;
        [SerializeField, Range(0, 45)] float receiverBoundaryNormalAngle = 5;
        [SerializeField] bool subdivideReceiverBoundaries = true;
        [SerializeField, Min(4), InspectorName("Max Additional Triangles")]
        int maxSubdividedTriangles = 262144;
        [SerializeField, FormerlySerializedAs("showSourceGrid")]
        SourceDisplayMode sourceDisplayMode = SourceDisplayMode.Outline;
        [SerializeField] Color sourceGridColor = Color.cyan;
        [SerializeField] Color sourceSurfaceColor = new(0.05f, 0.2f, 0.3f, 0.4f);
        [SerializeField, Range(0, 1)] float sourceSmoothness = 0.85f;
        [SerializeField, FormerlySerializedAs("litLighting")]
        SourceLightingSettings sourceLighting = new(1, 1, 1, 0.05f);
        [SerializeField, Min(0)] float sourceRefractionStrength = 8;
        [SerializeField, Range(0, 1)] float sourceRefractionLitBlend;

        [Header("Validation")]
        [SerializeField] bool enableValidation;
        [SerializeField] bool runContinuously;
        [SerializeField, Min(0)] float scalarRelativeTolerance = 0.001f;
        [SerializeField, Min(0)] float densityRelativeTolerance = 0.005f;

        RayTracingAccelerationStructure accelerationStructure;
        GraphicsBuffer sourceVertexBuffer;
        GraphicsBuffer sourceIndexBuffer;
        GraphicsBuffer sourceLineIndexBuffer;
        GraphicsBuffer hitBuffer;
        GraphicsBuffer triangleResultBuffer;
        GraphicsBuffer receiverPrimitiveOffsetBuffer;
        GraphicsBuffer receiverPrimitiveNormalBuffer;
        GraphicsBuffer sourceEdgeBuffer;
        GraphicsBuffer triangleEdgeBuffer;
        GraphicsBuffer edgeFlagBuffer;
        GraphicsBuffer edgeHitBuffer;
        GraphicsBuffer projectedIndexBuffer;
        GraphicsBuffer projectedResultBuffer;
        GraphicsBuffer outputCounterBuffer;
        GraphicsBuffer outputArgsBuffer;
        SourceVertex[] sourceVertices;
        uint[] sourceIndices;
        uint[] sourceLineIndices;
        SourceEdge[] sourceEdges;
        uint[] triangleEdges;
        int cellCountX;
        int cellCountZ;
        int vertexCount;
        int triangleCount;
        int drawVertexCount;
        int lineVertexCount;
        Vector2 actualSourceSize;
        Material projectedTriangleMaterial;
        Material sourceGridOutlineMaterial;
        readonly List<ReceiverState> receiverStates = new();
        int traceKernel;
        int buildSourceVerticesFromHeightMapKernel;
        int buildTrianglesKernel;
        int clearEdgeFlagsKernel;
        int markBoundaryEdgesKernel;
        int traceEdgeMidpointsKernel;
        int buildProjectedTrianglesKernel;
        int buildOutputArgsKernel;
        bool initialized;
        int readbackPendingCount;
        uint dispatchGeneration;
        uint resourceGeneration;
        bool validationRefreshRequested;
        bool hasDispatched;
        Matrix4x4 lastSourceTransform;
        Vector3 lastIncidentDirection;
        CausticHeightField lastHeightField;
        int lastHeightFieldStateHash;
        bool lastEnableValidation;
        RayHit[] validationHits;
        TriangleResult[] validationTriangleResults;
        uint validationHitsGeneration;
        uint validationTrianglesGeneration;

        sealed class ReceiverState
        {
            public MeshRenderer renderer;
            public int handle;
            public Matrix4x4 lastTransform;
            public uint instanceId;
            public int primitiveOffset;
            public Vector3[] localPrimitiveNormals;
        }

        enum CausticBlendMode
        {
            Additive,
            ModulatedAdditive,
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RayHit
        {
            public Vector3 position;
            public float distance;
            public uint instanceId;
            public uint primitiveIndex;
            public uint valid;
            public uint padding;
            public Vector3 receiverNormal;
            public float normalPadding;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SourceVertex
        {
            public Vector3 position;
            public Vector3 normal;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TriangleResult
        {
            public float intensity;
            public float incidentArea;
            public float receiverArea;
            public uint valid;
            public Vector3 renderNormal;
            public uint receiverBoundary;
        }

        void RebuildGridData()
        {
            var safeCellSize = Mathf.Max(0.001f, cellSize);
            cellCountX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(safeCellSize, sourceSize.x) / safeCellSize), 1, MaxCellCountPerAxis);
            cellCountZ = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(safeCellSize, sourceSize.y) / safeCellSize), 1, MaxCellCountPerAxis);
            actualSourceSize = new Vector2(cellCountX * safeCellSize, cellCountZ * safeCellSize);
            vertexCount = (cellCountX + 1) * (cellCountZ + 1);
            triangleCount = cellCountX * cellCountZ * 2;
            drawVertexCount = triangleCount * 3;
            sourceVertices = new SourceVertex[vertexCount];
            sourceIndices = CreateGridIndices();
            CreateSharedEdges();
            sourceLineIndices = CreateGridLineIndices();
            lineVertexCount = sourceLineIndices.Length;
        }

        bool GridTopologyChanged()
        {
            var safeCellSize = Mathf.Max(0.001f, cellSize);
            var expectedCellCountX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(safeCellSize, sourceSize.x) / safeCellSize), 1, MaxCellCountPerAxis);
            var expectedCellCountZ = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(safeCellSize, sourceSize.y) / safeCellSize), 1, MaxCellCountPerAxis);
            return sourceVertices == null
                || expectedCellCountX != cellCountX
                || expectedCellCountZ != cellCountZ;
        }

        void UpdateSourceVertices()
        {
            if (GridTopologyChanged())
                RebuildGridData();

            var gridCellSize = actualSourceSize.x / cellCountX;
            var evaluationTime = Application.isPlaying ? Time.time : 0;
            for (var z = 0; z <= cellCountZ; z++)
            {
                for (var x = 0; x <= cellCountX; x++)
                {
                    var localX = x * gridCellSize - actualSourceSize.x * 0.5f;
                    var localZ = z * gridCellSize - actualSourceSize.y * 0.5f;
                    var height = 0f;
                    var gradient = Vector2.zero;
                    if (heightField != null)
                    {
                        heightField.Evaluate(
                            new Vector2(localX, localZ),
                            evaluationTime,
                            out height,
                            out gradient);
                    }
                    sourceVertices[z * (cellCountX + 1) + x] = new SourceVertex
                    {
                        position = new Vector3(localX, height, localZ),
                        normal = new Vector3(-gradient.x, 1, -gradient.y).normalized,
                    };
                }
            }
            if (sourceVertexBuffer != null && sourceVertexBuffer.count == vertexCount)
                sourceVertexBuffer.SetData(sourceVertices);
        }

        uint[] CreateGridIndices()
        {
            var indices = new uint[drawVertexCount];
            var writeIndex = 0;
            for (var z = 0; z < cellCountZ; z++)
            {
                for (var x = 0; x < cellCountX; x++)
                {
                    var v00 = (uint)(z * (cellCountX + 1) + x);
                    var v10 = v00 + 1;
                    var v01 = v00 + (uint)(cellCountX + 1);
                    var v11 = v01 + 1;
                    indices[writeIndex++] = v00;
                    indices[writeIndex++] = v01;
                    indices[writeIndex++] = v10;
                    indices[writeIndex++] = v10;
                    indices[writeIndex++] = v01;
                    indices[writeIndex++] = v11;
                }
            }
            return indices;
        }

        uint[] CreateGridLineIndices()
        {
            var horizontalSegmentCount = cellCountX * (cellCountZ + 1);
            var verticalSegmentCount = (cellCountX + 1) * cellCountZ;
            var diagonalSegmentCount = cellCountX * cellCountZ;
            var indices = new uint[(horizontalSegmentCount + verticalSegmentCount + diagonalSegmentCount) * 2];
            var writeIndex = 0;

            for (var z = 0; z <= cellCountZ; z++)
            {
                for (var x = 0; x < cellCountX; x++)
                {
                    var start = (uint)(z * (cellCountX + 1) + x);
                    indices[writeIndex++] = start;
                    indices[writeIndex++] = start + 1;
                }
            }
            for (var z = 0; z < cellCountZ; z++)
            {
                for (var x = 0; x <= cellCountX; x++)
                {
                    var start = (uint)(z * (cellCountX + 1) + x);
                    indices[writeIndex++] = start;
                    indices[writeIndex++] = start + (uint)(cellCountX + 1);
                }
            }
            for (var z = 0; z < cellCountZ; z++)
            {
                for (var x = 0; x < cellCountX; x++)
                {
                    var upperRight = (uint)(z * (cellCountX + 1) + x + 1);
                    indices[writeIndex++] = upperRight;
                    indices[writeIndex++] = upperRight + (uint)cellCountX;
                }
            }
            return indices;
        }

        void OnEnable()
        {
            ActiveInstances.Add(this);
            RebuildGridData();
            UpdateSourceVertices();
            lastHeightField = heightField;
            lastHeightFieldStateHash = heightField != null ? heightField.StateHash : 0;
            EnsureSourceGridOutlineResources();
            hasDispatched = false;
        }

        void OnValidate()
        {
            sourceSize.x = Mathf.Max(0.001f, sourceSize.x);
            sourceSize.y = Mathf.Max(0.001f, sourceSize.y);
            cellSize = Mathf.Max(0.001f, cellSize);
            if (initialized && (GridTopologyChanged()
                || ReceiversChanged()
                || enableValidation != lastEnableValidation
                || projectedResultBuffer == null
                || projectedResultBuffer.count != triangleCount + Mathf.Max(4, maxSubdividedTriangles)))
                ReleaseResources();
            RebuildGridData();
            if (!Application.isPlaying || !initialized)
                UpdateSourceVertices();
            EnsureSourceGridOutlineResources();
            hasDispatched = false;
        }

        void Update()
        {
            if (!Application.isPlaying)
            {
                var heightFieldStateHash = heightField != null ? heightField.StateHash : 0;
                if (heightField != lastHeightField || heightFieldStateHash != lastHeightFieldStateHash)
                {
                    UpdateSourceVertices();
                    lastHeightField = heightField;
                    lastHeightFieldStateHash = heightFieldStateHash;
                }
                EnsureSourceGridOutlineResources();
                return;
            }

            if (!EnsureInitialized())
                return;

            var incidentDirection = directionalLight.transform.forward.normalized;
            var sourceTransform = transform.localToWorldMatrix;
            var changed = sourceTransform != lastSourceTransform
                || ReceiverTransformsChanged()
                || incidentDirection != lastIncidentDirection
                || heightField != lastHeightField
                || (heightField != null && heightField.StateHash != lastHeightFieldStateHash);

            if (!runContinuously
                && (heightField == null || !heightField.IsTimeVarying)
                && hasDispatched
                && !changed)
            {
                return;
            }

            UpdateReceiverTransforms();

            Dispatch(incidentDirection);
            lastSourceTransform = sourceTransform;
            lastIncidentDirection = incidentDirection;
            lastHeightField = heightField;
            lastHeightFieldStateHash = heightField != null ? heightField.StateHash : 0;
            hasDispatched = true;
        }

        bool ReceiverTransformsChanged()
        {
            for (var i = 0; i < receiverStates.Count; i++)
            {
                if (receiverStates[i].renderer.transform.localToWorldMatrix != receiverStates[i].lastTransform)
                    return true;
            }
            return false;
        }

        bool ReceiversChanged()
        {
            if (receivers == null || receivers.Length != receiverStates.Count)
                return true;
            for (var i = 0; i < receivers.Length; i++)
            {
                if (receivers[i] != receiverStates[i].renderer)
                    return true;
            }
            return false;
        }

        void UpdateReceiverTransforms()
        {
            var changed = false;
            for (var i = 0; i < receiverStates.Count; i++)
            {
                var state = receiverStates[i];
                var transformMatrix = state.renderer.transform.localToWorldMatrix;
                if (transformMatrix == state.lastTransform)
                    continue;

                accelerationStructure.UpdateInstanceTransform(state.handle, transformMatrix);
                state.lastTransform = transformMatrix;
                changed = true;
            }
            if (changed)
            {
                accelerationStructure.Build();
                UpdateReceiverPrimitiveNormals();
            }
        }

        void CreateSharedEdges()
        {
            var rowVertexCount = cellCountX + 1;
            var horizontalEdgeCount = cellCountX * (cellCountZ + 1);
            var verticalEdgeCount = rowVertexCount * cellCountZ;
            var diagonalEdgeCount = cellCountX * cellCountZ;
            var verticalOffset = horizontalEdgeCount;
            var diagonalOffset = horizontalEdgeCount + verticalEdgeCount;
            sourceEdges = new SourceEdge[horizontalEdgeCount + verticalEdgeCount + diagonalEdgeCount];
            triangleEdges = new uint[drawVertexCount];

            for (var z = 0; z <= cellCountZ; z++)
            {
                for (var x = 0; x < cellCountX; x++)
                {
                    var edgeIndex = z * cellCountX + x;
                    var vertex = (uint)(z * rowVertexCount + x);
                    sourceEdges[edgeIndex] = new SourceEdge { vertex0 = vertex, vertex1 = vertex + 1 };
                }
            }
            for (var z = 0; z < cellCountZ; z++)
            {
                for (var x = 0; x <= cellCountX; x++)
                {
                    var edgeIndex = verticalOffset + z * rowVertexCount + x;
                    var vertex = (uint)(z * rowVertexCount + x);
                    sourceEdges[edgeIndex] = new SourceEdge
                    {
                        vertex0 = vertex,
                        vertex1 = vertex + (uint)rowVertexCount,
                    };
                }
            }
            for (var z = 0; z < cellCountZ; z++)
            {
                for (var x = 0; x < cellCountX; x++)
                {
                    var cellIndex = z * cellCountX + x;
                    var diagonalEdge = diagonalOffset + cellIndex;
                    var v00 = (uint)(z * rowVertexCount + x);
                    var v10 = v00 + 1;
                    var v01 = v00 + (uint)rowVertexCount;
                    sourceEdges[diagonalEdge] = new SourceEdge { vertex0 = v10, vertex1 = v01 };

                    var triangleOffset = cellIndex * 6;
                    triangleEdges[triangleOffset] = (uint)(verticalOffset + z * rowVertexCount + x);
                    triangleEdges[triangleOffset + 1] = (uint)diagonalEdge;
                    triangleEdges[triangleOffset + 2] = (uint)(z * cellCountX + x);
                    triangleEdges[triangleOffset + 3] = (uint)diagonalEdge;
                    triangleEdges[triangleOffset + 4] = (uint)((z + 1) * cellCountX + x);
                    triangleEdges[triangleOffset + 5] = (uint)(verticalOffset + z * rowVertexCount + x + 1);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SourceEdge
        {
            public uint vertex0;
            public uint vertex1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProjectedResult
        {
            public float intensity;
            public uint receiverBoundary;
            public uint valid;
            public uint padding;
        }

        void UpdateReceiverPrimitiveNormals()
        {
            if (receiverPrimitiveNormalBuffer == null)
                return;

            var worldNormals = new Vector3[receiverPrimitiveNormalBuffer.count];
            foreach (var state in receiverStates)
            {
                var normalMatrix = state.renderer.transform.worldToLocalMatrix.transpose;
                for (var primitiveIndex = 0; primitiveIndex < state.localPrimitiveNormals.Length; primitiveIndex++)
                {
                    worldNormals[state.primitiveOffset + primitiveIndex] = normalMatrix
                        .MultiplyVector(state.localPrimitiveNormals[primitiveIndex]).normalized;
                }
            }
            receiverPrimitiveNormalBuffer.SetData(worldNormals);
        }

        static Vector3[] CreatePrimitiveNormals(Mesh mesh)
        {
            using var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var meshData = meshDataArray[0];
            using var vertices = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            var subMesh = meshData.GetSubMesh(0);
            using var indices = new NativeArray<int>(subMesh.indexCount, Allocator.Temp);
            meshData.GetVertices(vertices);
            meshData.GetIndices(indices, 0);
            var normals = new Vector3[indices.Length / 3];
            for (var primitiveIndex = 0; primitiveIndex < normals.Length; primitiveIndex++)
            {
                var indexOffset = primitiveIndex * 3;
                normals[primitiveIndex] = Vector3.Cross(
                    vertices[indices[indexOffset + 1]] - vertices[indices[indexOffset]],
                    vertices[indices[indexOffset + 2]] - vertices[indices[indexOffset]]).normalized;
            }
            return normals;
        }

        bool EnsureInitialized()
        {
            if (initialized)
                return true;

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Debug.LogError($"Caustic Ray Query requires Direct3D 12; current API is {SystemInfo.graphicsDeviceType}.", this);
                enabled = false;
                return false;
            }
            if (!SystemInfo.supportsInlineRayTracing)
            {
                Debug.LogError("Caustic Ray Query requires inline ray tracing (DXR Tier 1.1).", this);
                enabled = false;
                return false;
            }
            if (receivers == null || receivers.Length == 0 || directionalLight == null || rayQueryShader == null || projectedTriangleShader == null)
            {
                Debug.LogError("At least one Receiver, Directional Light, Ray Query Shader, and Projected Triangle Shader must be assigned.", this);
                enabled = false;
                return false;
            }
            if (directionalLight.type != LightType.Directional)
            {
                Debug.LogError("The assigned light must be a Directional Light.", this);
                enabled = false;
                return false;
            }
            if (heightField != null
                && !(enableValidation
                    ? heightField.TryValidateCpu(out var heightFieldError)
                    : heightField.TryValidate(out heightFieldError)))
            {
                Debug.LogError(heightFieldError, heightField);
                enabled = false;
                return false;
            }

            var settings = new RayTracingAccelerationStructure.Settings
            {
                managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
                rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                layerMask = ~0,
            };
            accelerationStructure = new RayTracingAccelerationStructure(settings);
            receiverStates.Clear();
            var uniqueReceivers = new HashSet<MeshRenderer>();
            var primitiveOffsets = new uint[receivers.Length + 2];
            var totalPrimitiveCount = 0;
            for (var receiverIndex = 0; receiverIndex < receivers.Length; receiverIndex++)
            {
                var receiver = receivers[receiverIndex];
                if (receiver == null || !uniqueReceivers.Add(receiver))
                {
                    Debug.LogError($"Receiver {receiverIndex} is null or duplicated.", this);
                    ReleaseResources();
                    enabled = false;
                    return false;
                }
                var meshFilter = receiver != null ? receiver.GetComponent<MeshFilter>() : null;
                var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null || mesh.subMeshCount != 1)
                {
                    Debug.LogError($"Receiver {receiverIndex} must have one mesh and one submesh.", receiver);
                    ReleaseResources();
                    enabled = false;
                    return false;
                }
                var primitiveNormals = CreatePrimitiveNormals(mesh);

                var instanceConfig = new RayTracingMeshInstanceConfig(mesh, 0, null)
                {
                    subMeshFlags = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                    enableTriangleCulling = false,
                    mask = ReceiverMask,
                };
                var instanceId = (uint)(receiverIndex + 1);
                primitiveOffsets[instanceId] = (uint)totalPrimitiveCount;
                var receiverTransform = receiver.transform.localToWorldMatrix;
                receiverStates.Add(new ReceiverState
                {
                    renderer = receiver,
                    handle = accelerationStructure.AddInstance(instanceConfig, receiverTransform, null, instanceId),
                    lastTransform = receiverTransform,
                    instanceId = instanceId,
                    primitiveOffset = totalPrimitiveCount,
                    localPrimitiveNormals = primitiveNormals,
                });
                totalPrimitiveCount += primitiveNormals.Length;
            }
            primitiveOffsets[receivers.Length + 1] = (uint)totalPrimitiveCount;
            accelerationStructure.Build();

            receiverPrimitiveOffsetBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                primitiveOffsets.Length,
                sizeof(uint));
            receiverPrimitiveOffsetBuffer.SetData(primitiveOffsets);
            receiverPrimitiveNormalBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, totalPrimitiveCount),
                Marshal.SizeOf<Vector3>());
            UpdateReceiverPrimitiveNormals();

            UpdateSourceVertices();
            sourceVertexBuffer ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexCount,
                Marshal.SizeOf<SourceVertex>());
            sourceVertexBuffer.SetData(sourceVertices);
            sourceIndexBuffer ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                drawVertexCount,
                sizeof(uint));
            sourceIndexBuffer.SetData(sourceIndices);
            EnsureSourceGridOutlineResources();
            hitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCount, Marshal.SizeOf<RayHit>());
            triangleResultBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                triangleCount,
                Marshal.SizeOf<TriangleResult>());
            sourceEdgeBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                sourceEdges.Length,
                Marshal.SizeOf<SourceEdge>());
            sourceEdgeBuffer.SetData(sourceEdges);
            triangleEdgeBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                triangleEdges.Length,
                sizeof(uint));
            triangleEdgeBuffer.SetData(triangleEdges);
            edgeFlagBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                sourceEdges.Length,
                sizeof(uint));
            edgeHitBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                sourceEdges.Length,
                Marshal.SizeOf<RayHit>());
            var maxOutputTriangles = triangleCount + Mathf.Max(4, maxSubdividedTriangles);
            projectedIndexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxOutputTriangles * 3,
                sizeof(uint));
            projectedResultBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxOutputTriangles,
                Marshal.SizeOf<ProjectedResult>());
            outputCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            outputArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
                4,
                sizeof(uint));

            projectedTriangleMaterial = new Material(projectedTriangleShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            projectedTriangleMaterial.SetBuffer(HitsId, hitBuffer);
            projectedTriangleMaterial.SetBuffer(EdgeHitsId, edgeHitBuffer);
            projectedTriangleMaterial.SetBuffer(ProjectedIndicesId, projectedIndexBuffer);
            projectedTriangleMaterial.SetBuffer(ProjectedResultsId, projectedResultBuffer);
            projectedTriangleMaterial.SetInt(VertexCountId, vertexCount);

            traceKernel = rayQueryShader.FindKernel("TraceVertices");
            buildSourceVerticesFromHeightMapKernel = rayQueryShader.FindKernel("BuildSourceVerticesFromHeightMap");
            buildTrianglesKernel = rayQueryShader.FindKernel("BuildTriangles");
            clearEdgeFlagsKernel = rayQueryShader.FindKernel("ClearEdgeFlags");
            markBoundaryEdgesKernel = rayQueryShader.FindKernel("MarkBoundaryEdges");
            traceEdgeMidpointsKernel = rayQueryShader.FindKernel("TraceEdgeMidpoints");
            buildProjectedTrianglesKernel = rayQueryShader.FindKernel("BuildProjectedTriangles");
            buildOutputArgsKernel = rayQueryShader.FindKernel("BuildOutputArgs");
            initialized = true;
            lastSourceTransform = transform.localToWorldMatrix;
            lastIncidentDirection = directionalLight.transform.forward.normalized;
            lastHeightField = heightField;
            lastHeightFieldStateHash = heightField != null ? heightField.StateHash : 0;
            lastEnableValidation = enableValidation;

            Debug.Log(
                $"Caustic grid initialized with {vertexCount} shared vertices and {triangleCount} triangles "
                + $"({cellCountX}x{cellCountZ} square cells, actual size {actualSourceSize.x:F3}x{actualSourceSize.y:F3}) "
                + $"with {sourceEdges.Length} shared edges, {projectedResultBuffer.count} projected triangle slots, "
                + $"and {receiverStates.Count} receivers on {SystemInfo.graphicsDeviceName}.",
                this);
            return true;
        }

        void Dispatch(Vector3 incidentDirection)
        {
            if (!TryDispatchSourceVerticesGpu())
                UpdateSourceVertices();
            dispatchGeneration++;
            var currentGeneration = dispatchGeneration;
            var currentResourceGeneration = resourceGeneration;
            rayQueryShader.SetRayTracingAccelerationStructure(traceKernel, AccelerationStructureId, accelerationStructure);
            rayQueryShader.SetBuffer(traceKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(traceKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(traceKernel, ReceiverPrimitiveOffsetsId, receiverPrimitiveOffsetBuffer);
            rayQueryShader.SetBuffer(traceKernel, ReceiverPrimitiveNormalsId, receiverPrimitiveNormalBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceIndicesId, sourceIndexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, TriangleResultsId, triangleResultBuffer);
            rayQueryShader.SetBuffer(clearEdgeFlagsKernel, EdgeFlagsId, edgeFlagBuffer);
            rayQueryShader.SetBuffer(markBoundaryEdgesKernel, SourceIndicesId, sourceIndexBuffer);
            rayQueryShader.SetBuffer(markBoundaryEdgesKernel, TriangleEdgesId, triangleEdgeBuffer);
            rayQueryShader.SetBuffer(markBoundaryEdgesKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(markBoundaryEdgesKernel, EdgeFlagsId, edgeFlagBuffer);
            rayQueryShader.SetRayTracingAccelerationStructure(traceEdgeMidpointsKernel, AccelerationStructureId, accelerationStructure);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, SourceEdgesId, sourceEdgeBuffer);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, EdgeFlagsId, edgeFlagBuffer);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, EdgeHitsId, edgeHitBuffer);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, ReceiverPrimitiveOffsetsId, receiverPrimitiveOffsetBuffer);
            rayQueryShader.SetBuffer(traceEdgeMidpointsKernel, ReceiverPrimitiveNormalsId, receiverPrimitiveNormalBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, SourceIndicesId, sourceIndexBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, SourceEdgesId, sourceEdgeBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, TriangleEdgesId, triangleEdgeBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, EdgeFlagsId, edgeFlagBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, EdgeHitsId, edgeHitBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, ProjectedIndicesId, projectedIndexBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, ProjectedResultsId, projectedResultBuffer);
            rayQueryShader.SetBuffer(buildProjectedTrianglesKernel, OutputCounterId, outputCounterBuffer);
            rayQueryShader.SetBuffer(buildOutputArgsKernel, OutputCounterId, outputCounterBuffer);
            rayQueryShader.SetBuffer(buildOutputArgsKernel, OutputArgsId, outputArgsBuffer);
            rayQueryShader.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            rayQueryShader.SetMatrix(SourceNormalToWorldId, transform.worldToLocalMatrix.transpose);
            rayQueryShader.SetInt(VertexCountId, vertexCount);
            rayQueryShader.SetInt(TriangleCountId, triangleCount);
            rayQueryShader.SetVector(IncidentDirectionId, incidentDirection);
            rayQueryShader.SetFloat(EtaId, 1f / transmittedRefractiveIndex);
            rayQueryShader.SetFloat(RayTMinId, rayTMin);
            rayQueryShader.SetFloat(RayTMaxId, rayTMax);
            rayQueryShader.SetFloat(MinReceiverAreaId, minReceiverArea);
            rayQueryShader.SetFloat(ReceiverBoundaryNormalCosId, Mathf.Cos(receiverBoundaryNormalAngle * Mathf.Deg2Rad));
            rayQueryShader.SetInt(EdgeCountId, sourceEdges.Length);
            rayQueryShader.SetInt(MaxOutputTrianglesId, projectedResultBuffer.count);
            rayQueryShader.SetInt(EnableSubdivisionId, subdivideReceiverBoundaries ? 1 : 0);
            outputCounterBuffer.SetData(ZeroSubdivisionCounter);
            rayQueryShader.Dispatch(traceKernel, DivideRoundUp(vertexCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildTrianglesKernel, DivideRoundUp(triangleCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(clearEdgeFlagsKernel, DivideRoundUp(sourceEdges.Length, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(markBoundaryEdgesKernel, DivideRoundUp(triangleCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(traceEdgeMidpointsKernel, DivideRoundUp(sourceEdges.Length, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildProjectedTrianglesKernel, DivideRoundUp(triangleCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildOutputArgsKernel, 1, 1, 1);

            projectedTriangleMaterial.SetColor(ColorId, causticColor);
            projectedTriangleMaterial.SetFloat(IntensityScaleId, intensityScale);
            projectedTriangleMaterial.SetFloat(SurfaceOffsetId, surfaceOffset);
            projectedTriangleMaterial.SetInt(ShowReceiverBoundariesId, showReceiverBoundaries ? 1 : 0);
            projectedTriangleMaterial.SetColor(ReceiverBoundaryColorId, receiverBoundaryColor);
            projectedTriangleMaterial.SetInt(
                SourceBlendId,
                causticBlendMode == CausticBlendMode.ModulatedAdditive
                    ? (int)BlendMode.DstColor
                    : (int)BlendMode.One);
            projectedTriangleMaterial.SetInt(DestinationBlendId, (int)BlendMode.One);
            if (!enableValidation)
            {
                validationRefreshRequested = false;
                return;
            }

            if (readbackPendingCount != 0)
            {
                validationRefreshRequested = true;
                return;
            }

            readbackPendingCount = 4;
            AsyncGPUReadback.Request(hitBuffer, request =>
            {
                CompleteReadback(currentResourceGeneration);
                if (this == null || !isActiveAndEnabled)
                    return;
                if (currentResourceGeneration != resourceGeneration)
                    return;
                if (currentGeneration != dispatchGeneration)
                    return;
                if (request.hasError)
                {
                    Debug.LogError("Failed to read Ray Query results from the GPU.", this);
                    return;
                }
                var data = request.GetData<RayHit>();
                validationHits = new RayHit[data.Length];
                data.CopyTo(validationHits);
                validationHitsGeneration = currentGeneration;
                ValidateHits(validationHits);
                TryValidateTriangles(currentGeneration, incidentDirection);
            });
            AsyncGPUReadback.Request(triangleResultBuffer, request =>
            {
                CompleteReadback(currentResourceGeneration);
                if (this == null || !isActiveAndEnabled)
                    return;
                if (currentResourceGeneration != resourceGeneration)
                    return;
                if (currentGeneration != dispatchGeneration)
                    return;
                if (request.hasError)
                {
                    Debug.LogError("Failed to read caustic triangle results from the GPU.", this);
                    return;
                }
                var data = request.GetData<TriangleResult>();
                validationTriangleResults = new TriangleResult[data.Length];
                data.CopyTo(validationTriangleResults);
                validationTrianglesGeneration = currentGeneration;
                TryValidateTriangles(currentGeneration, incidentDirection);
            });
            AsyncGPUReadback.Request(edgeFlagBuffer, request =>
            {
                CompleteReadback(currentResourceGeneration);
                if (this == null || !isActiveAndEnabled
                    || currentResourceGeneration != resourceGeneration
                    || currentGeneration != dispatchGeneration)
                    return;
                if (request.hasError)
                {
                    Debug.LogError("Failed to read receiver boundary edge flags from the GPU.", this);
                    return;
                }

                var flags = request.GetData<uint>();
                var boundaryEdgeCount = 0;
                var sharpNormalEdgeCount = 0;
                var crossReceiverEdgeCount = 0;
                for (var edgeIndex = 0; edgeIndex < flags.Length; edgeIndex++)
                {
                    var flag = flags[edgeIndex];
                    if (flag == 0)
                        continue;
                    boundaryEdgeCount++;
                    if ((flag & 1) != 0)
                        sharpNormalEdgeCount++;
                    if ((flag & 2) != 0)
                        crossReceiverEdgeCount++;
                }
                Debug.Log(
                    $"Caustic conforming subdivision marked {boundaryEdgeCount}/{sourceEdges.Length} shared edges "
                    + $"({sharpNormalEdgeCount} sharp-normal, {crossReceiverEdgeCount} cross-receiver).",
                    this);
            });
            AsyncGPUReadback.Request(outputCounterBuffer, request =>
            {
                CompleteReadback(currentResourceGeneration);
                if (this == null || !isActiveAndEnabled
                    || currentResourceGeneration != resourceGeneration
                    || currentGeneration != dispatchGeneration)
                    return;
                if (request.hasError)
                {
                    Debug.LogError("Failed to read projected triangle output count from the GPU.", this);
                    return;
                }

                var outputCount = request.GetData<uint>()[0];
                if (outputCount > (uint)projectedResultBuffer.count)
                {
                    Debug.LogError(
                        $"Caustic projected triangle buffer overflow: {outputCount} requested for "
                        + $"{projectedResultBuffer.count} slots. Increase Max Additional Triangles.",
                        this);
                }
                else
                {
                    Debug.Log(
                        $"Caustic conforming subdivision emitted {outputCount} triangles "
                        + $"into {projectedResultBuffer.count} slots.",
                        this);
                }
            });
        }

        enum SourceDisplayMode
        {
            Off,
            Outline,
            Lit,
            Refraction,
        }

        [System.Serializable]
        struct SourceLightingSettings
        {
            [Min(0)] public float mainLight;
            [Min(0)] public float ambient;
            [Min(0)] public float specular;
            [Min(0)] public float fresnel;

            public SourceLightingSettings(float mainLight, float ambient, float specular, float fresnel)
            {
                this.mainLight = mainLight;
                this.ambient = ambient;
                this.specular = specular;
                this.fresnel = fresnel;
            }
        }

        bool TryDispatchSourceVerticesGpu()
        {
            if (enableValidation || heightField == null)
                return false;

            var heightMapWidth = cellCountX + 1;
            var heightMapHeight = cellCountZ + 1;
            if (!heightField.TryDispatchGpuHeightMap(
                    rayQueryShader,
                    heightMapWidth,
                    heightMapHeight,
                    actualSourceSize,
                    Time.time,
                    Time.deltaTime,
                    out var heightMap))
                return false;

            rayQueryShader.SetBuffer(buildSourceVerticesFromHeightMapKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetTexture(buildSourceVerticesFromHeightMapKernel, HeightMapId, heightMap);
            rayQueryShader.SetInt(VertexCountId, vertexCount);
            rayQueryShader.SetInts(HeightMapSizeId, heightMapWidth, heightMapHeight);
            rayQueryShader.SetFloat(GridCellSizeId, actualSourceSize.x / cellCountX);
            rayQueryShader.SetVector(
                ActualSourceSizeId,
                new Vector4(actualSourceSize.x, actualSourceSize.y, 0, 0));
            rayQueryShader.Dispatch(
                buildSourceVerticesFromHeightMapKernel,
                DivideRoundUp(vertexCount, ComputeThreadCount),
                1,
                1);
            return true;
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        void CompleteReadback(uint completedResourceGeneration)
        {
            if (this != null && completedResourceGeneration == resourceGeneration)
            {
                readbackPendingCount = Mathf.Max(0, readbackPendingCount - 1);
                if (readbackPendingCount == 0 && validationRefreshRequested && enableValidation)
                {
                    validationRefreshRequested = false;
                    hasDispatched = false;
                }
            }
        }

        void ValidateHits(RayHit[] hits)
        {
            var receiverInstanceIds = new HashSet<uint>();
            var receiverPrimitives = new HashSet<ulong>();
            var validCount = 0;
            for (var i = 0; i < vertexCount; i++)
            {
                var hit = hits[i];
                if (hit.valid == 0)
                    continue;

                receiverInstanceIds.Add(hit.instanceId);
                receiverPrimitives.Add(((ulong)hit.instanceId << 32) | hit.primitiveIndex);
                validCount++;
            }

            if (validCount == 0)
                Debug.LogError(
                    $"Caustic grid Ray Query produced no receiver hits for {vertexCount} vertices.",
                    this);
            else
                Debug.Log(
                    $"Caustic grid Ray Query completed: {validCount}/{vertexCount} vertices hit "
                    + $"{receiverInstanceIds.Count}/{receiverStates.Count} receivers and "
                    + $"{receiverPrimitives.Count} receiver primitives.",
                    this);
        }

        void TryValidateTriangles(uint generation, Vector3 incidentDirection)
        {
            if (validationHits == null
                || validationTriangleResults == null
                || validationHitsGeneration != generation
                || validationTrianglesGeneration != generation)
                return;

            ValidateTriangles(validationTriangleResults, validationHits, incidentDirection);
        }

        void ValidateTriangles(TriangleResult[] results, RayHit[] hits, Vector3 incidentDirection)
        {

            var validatedCount = 0;
            var drawableCount = 0;
            var degenerateCount = 0;
            var missCount = 0;
            var crossReceiverCount = 0;
            var receiverBoundaryCount = 0;
            var firstMismatch = -1;
            var maxIncidentAreaError = 0f;
            var maxReceiverAreaError = 0f;
            var maxIntensityError = 0f;
            var firstExpectedReceiverArea = 0f;
            var firstGpuReceiverArea = 0f;
            var minIntensity = float.PositiveInfinity;
            var maxIntensity = float.NegativeInfinity;
            var totalIncidentEnergy = 0f;
            var totalExpectedReceivedEnergy = 0f;
            var totalReceivedEnergy = 0f;
            for (var triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                var indexOffset = triangleIndex * 3;
                var i0 = (int)sourceIndices[indexOffset];
                var i1 = (int)sourceIndices[indexOffset + 1];
                var i2 = (int)sourceIndices[indexOffset + 2];
                var x0 = GetSourceVertexWorld(i0);
                var x1 = GetSourceVertexWorld(i1);
                var x2 = GetSourceVertexWorld(i2);
                var h0 = hits[i0];
                var h1 = hits[i1];
                var h2 = hits[i2];
                var allHit = h0.valid != 0 && h1.valid != 0 && h2.valid != 0;
                var sameReceiver = allHit
                    && h0.instanceId == h1.instanceId
                    && h1.instanceId == h2.instanceId;
                var expectedIncidentArea = 0.5f * Mathf.Max(
                    0,
                    Vector3.Dot(-incidentDirection.normalized, Vector3.Cross(x1 - x0, x2 - x0)));
                var expectedReceiverArea = sameReceiver
                    ? 0.5f * Vector3.Cross(h1.position - h0.position, h2.position - h0.position).magnitude
                    : 0;
                var expectedIntensity = expectedIncidentArea / Mathf.Max(expectedReceiverArea, minReceiverArea);
                var result = results[triangleIndex];
                var expectedReceiverBoundary = sameReceiver && (
                    CrossesReceiverPlane(h0, h1)
                    || CrossesReceiverPlane(h1, h2)
                    || CrossesReceiverPlane(h2, h0));
                var expectedDrawable = sameReceiver
                    && expectedIncidentArea > 0
                    && expectedReceiverArea > 0;
                var gpuDrawable = result.valid != 0;
                if (!expectedDrawable && !gpuDrawable)
                {
                    validatedCount++;
                    if (!allHit)
                        missCount++;
                    else if (!sameReceiver)
                        crossReceiverCount++;
                    else
                        degenerateCount++;
                    continue;
                }

                var incidentAreaError = RelativeError(result.incidentArea, expectedIncidentArea);
                var receiverAreaError = RelativeError(result.receiverArea, expectedReceiverArea);
                var intensityError = RelativeError(result.intensity, expectedIntensity);
                maxIncidentAreaError = Mathf.Max(maxIncidentAreaError, incidentAreaError);
                maxReceiverAreaError = Mathf.Max(maxReceiverAreaError, receiverAreaError);
                maxIntensityError = Mathf.Max(maxIntensityError, intensityError);
                var valuesMatch = expectedDrawable
                    && gpuDrawable
                    && (result.receiverBoundary != 0) == expectedReceiverBoundary
                    && incidentAreaError <= scalarRelativeTolerance
                    && WithinTolerance(
                        result.receiverArea,
                        expectedReceiverArea,
                        scalarRelativeTolerance,
                        minReceiverArea * densityRelativeTolerance)
                    && intensityError <= densityRelativeTolerance;
                if (!valuesMatch)
                {
                    if (firstMismatch < 0)
                    {
                        firstMismatch = triangleIndex;
                        firstExpectedReceiverArea = expectedReceiverArea;
                        firstGpuReceiverArea = result.receiverArea;
                    }
                    continue;
                }

                validatedCount++;
                drawableCount++;
                if (result.receiverBoundary != 0)
                    receiverBoundaryCount++;
                minIntensity = Mathf.Min(minIntensity, result.intensity);
                maxIntensity = Mathf.Max(maxIntensity, result.intensity);
                totalIncidentEnergy += result.incidentArea;
                totalExpectedReceivedEnergy += expectedIntensity * expectedReceiverArea;
                totalReceivedEnergy += result.intensity * result.receiverArea;
            }

            var energyValidationError = RelativeError(totalReceivedEnergy, totalExpectedReceivedEnergy);
            var energyRetention = totalIncidentEnergy > 0
                ? totalExpectedReceivedEnergy / totalIncidentEnergy
                : 1;
            if (validatedCount == triangleCount && energyValidationError <= densityRelativeTolerance)
            {
                Debug.Log(
                    $"Caustic grid density validation passed: {validatedCount}/{triangleCount} triangles "
                    + $"({drawableCount} drawable, {missCount} missed, "
                    + $"{crossReceiverCount} cross-receiver, {receiverBoundaryCount} receiver-boundary, "
                    + $"{degenerateCount} degenerate), "
                    + $"C={minIntensity:F6}..{maxIntensity:F6}, "
                    + $"GPU/CPU energy error={energyValidationError:E3}, "
                    + $"clamped energy retention={energyRetention:P3}.",
                    this);
            }
            else
            {
                Debug.LogError(
                    $"Caustic grid density validation failed: {validatedCount}/{triangleCount} triangles, "
                    + $"first mismatch={firstMismatch}, max relative errors "
                    + $"Ai={maxIncidentAreaError:E3}, Ar={maxReceiverAreaError:E3}, C={maxIntensityError:E3}; "
                    + $"first Ar expected={firstExpectedReceiverArea:E6}, GPU={firstGpuReceiverArea:E6}, "
                    + $"absolute error={Mathf.Abs(firstGpuReceiverArea - firstExpectedReceiverArea):E3}, "
                    + $"GPU/CPU energy error={energyValidationError:E3}, "
                    + $"clamped energy retention={energyRetention:P3}.",
                    this);
            }
        }

        static float RelativeError(float actual, float expected)
        {
            return Mathf.Abs(actual - expected) / Mathf.Max(Mathf.Abs(expected), 0.000001f);
        }

        bool CrossesReceiverPlane(RayHit a, RayHit b)
        {
            if (a.primitiveIndex == b.primitiveIndex)
                return false;

            var normalAlignment = Mathf.Abs(Vector3.Dot(a.receiverNormal, b.receiverNormal));
            return normalAlignment < Mathf.Cos(receiverBoundaryNormalAngle * Mathf.Deg2Rad);
        }

        static bool WithinTolerance(float actual, float expected, float relativeTolerance, float absoluteTolerance)
        {
            return Mathf.Abs(actual - expected) <= absoluteTolerance
                || RelativeError(actual, expected) <= relativeTolerance;
        }

        Vector3 GetSourceVertexWorld(int index)
        {
            return transform.TransformPoint(sourceVertices[index].position);
        }

        Vector3 GetSourceNormalWorld(int index)
        {
            return transform.worldToLocalMatrix.transpose.MultiplyVector(sourceVertices[index].normal).normalized;
        }

        internal static void DrawProjectedCaustics(CommandBuffer commandBuffer, Camera camera)
        {
            foreach (var instance in ActiveInstances)
            {
                if (instance == null
                    || !instance.isActiveAndEnabled
                    || !instance.initialized
                    || instance.projectedTriangleMaterial == null
                    || instance.outputArgsBuffer == null
                    || (camera.cullingMask & (1 << instance.gameObject.layer)) == 0)
                    continue;
                commandBuffer.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    instance.projectedTriangleMaterial,
                    0,
                    MeshTopology.Triangles,
                    instance.outputArgsBuffer,
                    0);
            }
        }

        internal static void DrawSourceSurfaces(CommandBuffer commandBuffer, Camera camera)
        {
            foreach (var instance in ActiveInstances)
            {
                if (instance == null
                    || !instance.isActiveAndEnabled
                    || instance.sourceDisplayMode == SourceDisplayMode.Off
                    || instance.sourceGridOutlineMaterial == null
                    || (camera.cullingMask & (1 << instance.gameObject.layer)) == 0)
                    continue;
                instance.ConfigureSourceDisplayMaterial();
                instance.GetSourceDisplayDrawParameters(out var topology, out var indexCount);
                commandBuffer.DrawProcedural(
                    Matrix4x4.identity,
                    instance.sourceGridOutlineMaterial,
                    0,
                    topology,
                    indexCount);
            }
        }

        void EnsureSourceGridOutlineResources()
        {
            if (sourceDisplayMode == SourceDisplayMode.Off || sourceGridOutlineShader == null || sourceVertices == null)
                return;

            if (sourceVertexBuffer == null || sourceVertexBuffer.count != vertexCount)
            {
                sourceVertexBuffer?.Dispose();
                sourceVertexBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    vertexCount,
                    Marshal.SizeOf<SourceVertex>());
                sourceVertexBuffer.SetData(sourceVertices);
            }

            if (sourceIndexBuffer == null || sourceIndexBuffer.count != drawVertexCount)
            {
                sourceIndexBuffer?.Dispose();
                sourceIndexBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    drawVertexCount,
                    sizeof(uint));
                sourceIndexBuffer.SetData(sourceIndices);
            }

            if (sourceLineIndexBuffer == null || sourceLineIndexBuffer.count != lineVertexCount)
            {
                sourceLineIndexBuffer?.Dispose();
                sourceLineIndexBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    lineVertexCount,
                    sizeof(uint));
                sourceLineIndexBuffer.SetData(sourceLineIndices);
            }
            if (sourceGridOutlineMaterial == null)
            {
                sourceGridOutlineMaterial = new Material(sourceGridOutlineShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            sourceGridOutlineMaterial.SetBuffer(SourceVerticesId, sourceVertexBuffer);
            sourceGridOutlineMaterial.SetBuffer(SourceIndicesId, sourceIndexBuffer);
            sourceGridOutlineMaterial.SetBuffer(SourceLineIndicesId, sourceLineIndexBuffer);
        }

        void ConfigureSourceDisplayMaterial()
        {
            sourceGridOutlineMaterial.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            sourceGridOutlineMaterial.SetMatrix(SourceNormalToWorldId, transform.worldToLocalMatrix.transpose);
            sourceGridOutlineMaterial.SetInt(SourceDisplayModeId, (int)sourceDisplayMode);
            sourceGridOutlineMaterial.SetColor(ColorId, sourceGridColor);
            sourceGridOutlineMaterial.SetColor(SourceSurfaceColorId, sourceSurfaceColor);
            sourceGridOutlineMaterial.SetFloat(SourceSmoothnessId, sourceSmoothness);
            sourceGridOutlineMaterial.SetFloat(SourceRefractionStrengthId, sourceRefractionStrength);
            sourceGridOutlineMaterial.SetFloat(SourceMainLightIntensityId, sourceLighting.mainLight);
            sourceGridOutlineMaterial.SetFloat(SourceAmbientIntensityId, sourceLighting.ambient);
            sourceGridOutlineMaterial.SetFloat(SourceSpecularIntensityId, sourceLighting.specular);
            sourceGridOutlineMaterial.SetFloat(SourceFresnelIntensityId, sourceLighting.fresnel);
            sourceGridOutlineMaterial.SetFloat(SourceRefractionLitBlendId, sourceRefractionLitBlend);
        }

        void GetSourceDisplayDrawParameters(out MeshTopology topology, out int indexCount)
        {
            var drawOutline = sourceDisplayMode == SourceDisplayMode.Outline;
            topology = drawOutline ? MeshTopology.Lines : MeshTopology.Triangles;
            indexCount = drawOutline ? lineVertexCount : drawVertexCount;
        }

        static Vector3 Refract(Vector3 incident, Vector3 normal, float eta)
        {
            incident.Normalize();
            normal.Normalize();
            var normalDotIncident = Vector3.Dot(normal, incident);
            var discriminant = 1f - eta * eta * (1f - normalDotIncident * normalDotIncident);
            if (discriminant < 0)
                return Vector3.zero;
            return (eta * incident - (eta * normalDotIncident + Mathf.Sqrt(discriminant)) * normal).normalized;
        }

        void OnDisable()
        {
            ActiveInstances.Remove(this);
            ReleaseResources();
        }

        void ReleaseResources()
        {
            resourceGeneration++;
            initialized = false;
            readbackPendingCount = 0;
            dispatchGeneration = 0;
            validationRefreshRequested = false;
            receiverStates.Clear();
            validationHits = null;
            validationTriangleResults = null;
            sourceVertexBuffer?.Dispose();
            sourceVertexBuffer = null;
            sourceIndexBuffer?.Dispose();
            sourceIndexBuffer = null;
            sourceLineIndexBuffer?.Dispose();
            sourceLineIndexBuffer = null;
            hitBuffer?.Dispose();
            hitBuffer = null;
            triangleResultBuffer?.Dispose();
            triangleResultBuffer = null;
            receiverPrimitiveOffsetBuffer?.Dispose();
            receiverPrimitiveOffsetBuffer = null;
            receiverPrimitiveNormalBuffer?.Dispose();
            receiverPrimitiveNormalBuffer = null;
            sourceEdgeBuffer?.Dispose();
            sourceEdgeBuffer = null;
            triangleEdgeBuffer?.Dispose();
            triangleEdgeBuffer = null;
            edgeFlagBuffer?.Dispose();
            edgeFlagBuffer = null;
            edgeHitBuffer?.Dispose();
            edgeHitBuffer = null;
            projectedIndexBuffer?.Dispose();
            projectedIndexBuffer = null;
            projectedResultBuffer?.Dispose();
            projectedResultBuffer = null;
            outputCounterBuffer?.Dispose();
            outputCounterBuffer = null;
            outputArgsBuffer?.Dispose();
            outputArgsBuffer = null;
            accelerationStructure?.Dispose();
            accelerationStructure = null;
            if (projectedTriangleMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(projectedTriangleMaterial);
                else
                    DestroyImmediate(projectedTriangleMaterial);
                projectedTriangleMaterial = null;
            }
            if (sourceGridOutlineMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(sourceGridOutlineMaterial);
                else
                    DestroyImmediate(sourceGridOutlineMaterial);
                sourceGridOutlineMaterial = null;
            }
        }
    }
}
