using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    public sealed class CausticRayQueryTest : MonoBehaviour
    {
        const int ComputeThreadCount = 64;
        const int MaxCellCountPerAxis = 512;
        const uint ReceiverMask = 1;

        static readonly int AccelerationStructureId = Shader.PropertyToID("_AccelerationStructure");
        static readonly int SourceVerticesId = Shader.PropertyToID("_SourceVertices");
        static readonly int SourceIndicesId = Shader.PropertyToID("_SourceIndices");
        static readonly int SourceLineIndicesId = Shader.PropertyToID("_SourceLineIndices");
        static readonly int HitsId = Shader.PropertyToID("_Hits");
        static readonly int TriangleResultsId = Shader.PropertyToID("_TriangleResults");
        static readonly int VertexTriangleOffsetsId = Shader.PropertyToID("_VertexTriangleOffsets");
        static readonly int VertexTriangleIndicesId = Shader.PropertyToID("_VertexTriangleIndices");
        static readonly int RenderNormalsId = Shader.PropertyToID("_RenderNormals");
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

        [Header("Scene")]
        [SerializeField] MeshRenderer[] receivers;
        [SerializeField] Light directionalLight;
        [SerializeField] ComputeShader rayQueryShader;
        [SerializeField] Shader projectedTriangleShader;
        [SerializeField] Shader sourceGridOutlineShader;

        [Header("Input Surface")]
        [SerializeField] Vector2 sourceSize = new(2, 2);
        [SerializeField, Min(0.001f)] float cellSize = 0.25f;
        [SerializeField, Range(0, 0.25f)] float waveAmplitude = 0.03f;
        [SerializeField, Min(0.001f)] float waveLength = 1;
        [SerializeField] Vector2 waveCenter;
        [SerializeField, Min(0.0001f)] float waveCenterSmoothing = 0.05f;
        [SerializeField] bool animateWave;
        [SerializeField] float waveSpeed = 1;
        [SerializeField, Range(0, 1)] float wavePhase;

        [Header("Refraction")]
        [SerializeField, Min(1)] float transmittedRefractiveIndex = 1.333f;
        [SerializeField, Min(0.000001f)] float rayTMin = 0.001f;
        [SerializeField, Min(0.01f)] float rayTMax = 100;
        [SerializeField, Min(0.00000001f)] float minReceiverArea = 0.00001f;

        [Header("Display")]
        [SerializeField] Color causticColor = new(1, 0.7f, 0.1f, 1);
        [SerializeField, Min(0)] float intensityScale = 0.25f;
        [SerializeField, Min(0)] float surfaceOffset = 0.002f;
        [SerializeField] bool showSourceGrid = true;
        [SerializeField] Color sourceGridColor = Color.cyan;

        [Header("Validation")]
        [SerializeField] bool runContinuously;
        [SerializeField, Min(0)] float scalarRelativeTolerance = 0.001f;
        [SerializeField, Min(0)] float densityRelativeTolerance = 0.005f;

        RayTracingAccelerationStructure accelerationStructure;
        GraphicsBuffer sourceVertexBuffer;
        GraphicsBuffer sourceIndexBuffer;
        GraphicsBuffer sourceLineIndexBuffer;
        GraphicsBuffer hitBuffer;
        GraphicsBuffer triangleResultBuffer;
        GraphicsBuffer vertexTriangleOffsetBuffer;
        GraphicsBuffer vertexTriangleIndexBuffer;
        GraphicsBuffer renderNormalBuffer;
        SourceVertex[] sourceVertices;
        uint[] sourceIndices;
        uint[] sourceLineIndices;
        uint[] vertexTriangleOffsets;
        uint[] vertexTriangleIndices;
        int cellCountX;
        int cellCountZ;
        int vertexCount;
        int triangleCount;
        int drawVertexCount;
        int lineVertexCount;
        Vector2 actualSourceSize;
        Material projectedTriangleMaterial;
        Material sourceGridOutlineMaterial;
        CommandBuffer sourceGridOutlineCommandBuffer;
        readonly List<ReceiverState> receiverStates = new();
        int traceKernel;
        int buildTrianglesKernel;
        int buildRenderNormalsKernel;
        bool initialized;
        int readbackPendingCount;
        uint dispatchGeneration;
        uint resourceGeneration;
        bool validationRefreshRequested;
        bool hasDispatched;
        Matrix4x4 lastSourceTransform;
        Vector3 lastIncidentDirection;
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
            public float padding;
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
            sourceLineIndices = CreateGridLineIndices();
            CreateVertexTriangleAdjacency();
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

            var angularFrequency = 2f * Mathf.PI / Mathf.Max(0.001f, waveLength);
            var phaseOffset = 2f * Mathf.PI * (animateWave ? Time.time * waveSpeed : wavePhase);
            var smoothing = Mathf.Max(0.0001f, waveCenterSmoothing);
            var gridCellSize = actualSourceSize.x / cellCountX;
            for (var z = 0; z <= cellCountZ; z++)
            {
                for (var x = 0; x <= cellCountX; x++)
                {
                    var localX = x * gridCellSize - actualSourceSize.x * 0.5f;
                    var localZ = z * gridCellSize - actualSourceSize.y * 0.5f;
                    var offsetX = localX - waveCenter.x;
                    var offsetZ = localZ - waveCenter.y;
                    var smoothDistance = Mathf.Sqrt(offsetX * offsetX + offsetZ * offsetZ + smoothing * smoothing);
                    var radius = smoothDistance - smoothing;
                    var phase = angularFrequency * radius - phaseOffset;
                    var height = waveAmplitude * Mathf.Sin(phase);
                    var radialSlope = waveAmplitude * angularFrequency * Mathf.Cos(phase);
                    var derivativeX = radialSlope * offsetX / smoothDistance;
                    var derivativeZ = radialSlope * offsetZ / smoothDistance;
                    sourceVertices[z * (cellCountX + 1) + x] = new SourceVertex
                    {
                        position = new Vector3(localX, height, localZ),
                        normal = new Vector3(-derivativeX, 1, -derivativeZ).normalized,
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

        void CreateVertexTriangleAdjacency()
        {
            var triangleCounts = new uint[vertexCount];
            for (var index = 0; index < sourceIndices.Length; index++)
                triangleCounts[sourceIndices[index]]++;
            vertexTriangleOffsets = new uint[vertexCount + 1];
            vertexTriangleIndices = new uint[drawVertexCount];
            for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                vertexTriangleOffsets[vertexIndex + 1] = vertexTriangleOffsets[vertexIndex] + triangleCounts[vertexIndex];

            var writeOffsets = (uint[])vertexTriangleOffsets.Clone();
            for (var triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                var indexOffset = triangleIndex * 3;
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertexIndex = sourceIndices[indexOffset + corner];
                    vertexTriangleIndices[writeOffsets[vertexIndex]++] = (uint)triangleIndex;
                }
            }
        }

        void OnEnable()
        {
            RebuildGridData();
            UpdateSourceVertices();
            EnsureSourceGridOutlineResources();
            RenderPipelineManager.endCameraRendering += DrawSourceGridInSceneView;
            hasDispatched = false;
        }

        void OnValidate()
        {
            sourceSize.x = Mathf.Max(0.001f, sourceSize.x);
            sourceSize.y = Mathf.Max(0.001f, sourceSize.y);
            cellSize = Mathf.Max(0.001f, cellSize);
            if (initialized && (GridTopologyChanged() || ReceiversChanged()))
                ReleaseResources();
            RebuildGridData();
            if (!Application.isPlaying || !initialized)
                UpdateSourceVertices();
            if (!Application.isPlaying)
                EnsureSourceGridOutlineResources();
            hasDispatched = false;
        }

        void Update()
        {
            if (!Application.isPlaying)
            {
                EnsureSourceGridOutlineResources();
                return;
            }

            if (!EnsureInitialized())
                return;

            var incidentDirection = directionalLight.transform.forward.normalized;
            var sourceTransform = transform.localToWorldMatrix;
            var changed = sourceTransform != lastSourceTransform
                || ReceiverTransformsChanged()
                || incidentDirection != lastIncidentDirection;

            if (!runContinuously && !animateWave && hasDispatched && !changed)
            {
                QueueProjectedGrid();
                QueueSourceGridOutline();
                return;
            }

            UpdateReceiverTransforms();

            Dispatch(incidentDirection);
            lastSourceTransform = sourceTransform;
            lastIncidentDirection = incidentDirection;
            hasDispatched = true;
            QueueProjectedGrid();
            QueueSourceGridOutline();
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
                accelerationStructure.Build();
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

            var settings = new RayTracingAccelerationStructure.Settings
            {
                managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
                rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                layerMask = ~0,
            };
            accelerationStructure = new RayTracingAccelerationStructure(settings);
            receiverStates.Clear();
            var uniqueReceivers = new HashSet<MeshRenderer>();
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

                var instanceConfig = new RayTracingMeshInstanceConfig(mesh, 0, null)
                {
                    subMeshFlags = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                    enableTriangleCulling = false,
                    mask = ReceiverMask,
                };
                var instanceId = (uint)(receiverIndex + 1);
                var receiverTransform = receiver.transform.localToWorldMatrix;
                receiverStates.Add(new ReceiverState
                {
                    renderer = receiver,
                    handle = accelerationStructure.AddInstance(instanceConfig, receiverTransform, null, instanceId),
                    lastTransform = receiverTransform,
                    instanceId = instanceId,
                });
            }
            accelerationStructure.Build();

            UpdateSourceVertices();
            sourceVertexBuffer ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexCount,
                Marshal.SizeOf<SourceVertex>());
            sourceVertexBuffer.SetData(sourceVertices);
            sourceIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, drawVertexCount, sizeof(uint));
            sourceIndexBuffer.SetData(sourceIndices);
            EnsureSourceGridOutlineResources();
            hitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCount, Marshal.SizeOf<RayHit>());
            triangleResultBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                triangleCount,
                Marshal.SizeOf<TriangleResult>());
            vertexTriangleOffsetBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexTriangleOffsets.Length,
                sizeof(uint));
            vertexTriangleOffsetBuffer.SetData(vertexTriangleOffsets);
            vertexTriangleIndexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexTriangleIndices.Length,
                sizeof(uint));
            vertexTriangleIndexBuffer.SetData(vertexTriangleIndices);
            renderNormalBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                vertexCount,
                Marshal.SizeOf<Vector3>());

            projectedTriangleMaterial = new Material(projectedTriangleShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            projectedTriangleMaterial.SetBuffer(SourceIndicesId, sourceIndexBuffer);
            projectedTriangleMaterial.SetBuffer(HitsId, hitBuffer);
            projectedTriangleMaterial.SetBuffer(TriangleResultsId, triangleResultBuffer);
            projectedTriangleMaterial.SetBuffer(RenderNormalsId, renderNormalBuffer);

            traceKernel = rayQueryShader.FindKernel("TraceVertices");
            buildTrianglesKernel = rayQueryShader.FindKernel("BuildTriangles");
            buildRenderNormalsKernel = rayQueryShader.FindKernel("BuildRenderNormals");
            initialized = true;
            lastSourceTransform = transform.localToWorldMatrix;
            lastIncidentDirection = directionalLight.transform.forward.normalized;

            Debug.Log(
                $"Caustic grid initialized with {vertexCount} shared vertices and {triangleCount} triangles "
                + $"({cellCountX}x{cellCountZ} square cells, actual size {actualSourceSize.x:F3}x{actualSourceSize.y:F3}) "
                + $"with {receiverStates.Count} receivers on {SystemInfo.graphicsDeviceName}.",
                this);
            return true;
        }

        void Dispatch(Vector3 incidentDirection)
        {
            UpdateSourceVertices();
            dispatchGeneration++;
            var currentGeneration = dispatchGeneration;
            var currentResourceGeneration = resourceGeneration;
            rayQueryShader.SetRayTracingAccelerationStructure(traceKernel, AccelerationStructureId, accelerationStructure);
            rayQueryShader.SetBuffer(traceKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(traceKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceIndicesId, sourceIndexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, TriangleResultsId, triangleResultBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, TriangleResultsId, triangleResultBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, VertexTriangleOffsetsId, vertexTriangleOffsetBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, VertexTriangleIndicesId, vertexTriangleIndexBuffer);
            rayQueryShader.SetBuffer(buildRenderNormalsKernel, RenderNormalsId, renderNormalBuffer);
            rayQueryShader.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            rayQueryShader.SetMatrix(SourceNormalToWorldId, transform.worldToLocalMatrix.transpose);
            rayQueryShader.SetInt(VertexCountId, vertexCount);
            rayQueryShader.SetInt(TriangleCountId, triangleCount);
            rayQueryShader.SetVector(IncidentDirectionId, incidentDirection);
            rayQueryShader.SetFloat(EtaId, 1f / transmittedRefractiveIndex);
            rayQueryShader.SetFloat(RayTMinId, rayTMin);
            rayQueryShader.SetFloat(RayTMaxId, rayTMax);
            rayQueryShader.SetFloat(MinReceiverAreaId, minReceiverArea);
            rayQueryShader.Dispatch(traceKernel, DivideRoundUp(vertexCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildTrianglesKernel, DivideRoundUp(triangleCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildRenderNormalsKernel, DivideRoundUp(vertexCount, ComputeThreadCount), 1, 1);

            projectedTriangleMaterial.SetColor(ColorId, causticColor);
            projectedTriangleMaterial.SetFloat(IntensityScaleId, intensityScale);
            projectedTriangleMaterial.SetFloat(SurfaceOffsetId, surfaceOffset);
            if (readbackPendingCount != 0)
            {
                validationRefreshRequested = true;
                return;
            }

            readbackPendingCount = 2;
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
                if (readbackPendingCount == 0 && validationRefreshRequested)
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
                    + $"{crossReceiverCount} cross-receiver, {degenerateCount} degenerate), "
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

        void QueueProjectedGrid()
        {
            if (!initialized || projectedTriangleMaterial == null)
                return;

            Graphics.DrawProcedural(
                projectedTriangleMaterial,
                GetReceiverBounds(),
                MeshTopology.Triangles,
                drawVertexCount,
                1,
                null,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
        }

        Bounds GetReceiverBounds()
        {
            if (receiverStates.Count == 0)
                return new Bounds(transform.position, Vector3.one);

            var bounds = receiverStates[0].renderer.bounds;
            for (var i = 1; i < receiverStates.Count; i++)
                bounds.Encapsulate(receiverStates[i].renderer.bounds);
            return bounds;
        }

        void QueueSourceGridOutline()
        {
            if (!showSourceGrid || !initialized || sourceGridOutlineMaterial == null)
                return;

            sourceGridOutlineMaterial.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            sourceGridOutlineMaterial.SetColor(ColorId, sourceGridColor);
            var maxScale = Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y),
                Mathf.Abs(transform.lossyScale.z));
            var diameter = Mathf.Sqrt(
                actualSourceSize.x * actualSourceSize.x
                + actualSourceSize.y * actualSourceSize.y
                + 4 * waveAmplitude * waveAmplitude) * maxScale;
            var bounds = new Bounds(transform.TransformPoint(Vector3.zero), Vector3.one * Mathf.Max(0.01f, diameter));
            Graphics.DrawProcedural(
                sourceGridOutlineMaterial,
                bounds,
                MeshTopology.Lines,
                lineVertexCount,
                1,
                null,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
        }

        void EnsureSourceGridOutlineResources()
        {
            if (!showSourceGrid || sourceGridOutlineShader == null || sourceVertices == null)
                return;

            if (sourceVertexBuffer == null || sourceVertexBuffer.count != vertexCount)
            {
                sourceVertexBuffer?.Dispose();
                sourceVertexBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    vertexCount,
                    Marshal.SizeOf<SourceVertex>());
            }
            sourceVertexBuffer.SetData(sourceVertices);

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
            sourceGridOutlineMaterial.SetBuffer(SourceLineIndicesId, sourceLineIndexBuffer);
        }

        void DrawSourceGridInSceneView(ScriptableRenderContext context, Camera camera)
        {
            if (!showSourceGrid || camera == null || camera.cameraType != CameraType.SceneView)
                return;

            EnsureSourceGridOutlineResources();
            if (sourceGridOutlineMaterial == null)
                return;

            sourceGridOutlineMaterial.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            sourceGridOutlineMaterial.SetColor(ColorId, sourceGridColor);
            sourceGridOutlineCommandBuffer ??= new CommandBuffer
            {
                name = "Caustic Source Grid Outline",
            };
            sourceGridOutlineCommandBuffer.Clear();
            sourceGridOutlineCommandBuffer.DrawProcedural(
                Matrix4x4.identity,
                sourceGridOutlineMaterial,
                0,
                MeshTopology.Lines,
                lineVertexCount);
            context.ExecuteCommandBuffer(sourceGridOutlineCommandBuffer);
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
            RenderPipelineManager.endCameraRendering -= DrawSourceGridInSceneView;
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
            vertexTriangleOffsetBuffer?.Dispose();
            vertexTriangleOffsetBuffer = null;
            vertexTriangleIndexBuffer?.Dispose();
            vertexTriangleIndexBuffer = null;
            renderNormalBuffer?.Dispose();
            renderNormalBuffer = null;
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
            sourceGridOutlineCommandBuffer?.Release();
            sourceGridOutlineCommandBuffer = null;
        }
    }
}
