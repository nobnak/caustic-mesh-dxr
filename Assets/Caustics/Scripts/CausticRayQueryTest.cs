using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
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
        const uint ReceiverInstanceId = 1;

        static readonly int AccelerationStructureId = Shader.PropertyToID("_AccelerationStructure");
        static readonly int SourceVerticesId = Shader.PropertyToID("_SourceVertices");
        static readonly int SourceIndicesId = Shader.PropertyToID("_SourceIndices");
        static readonly int SourceLineIndicesId = Shader.PropertyToID("_SourceLineIndices");
        static readonly int HitsId = Shader.PropertyToID("_Hits");
        static readonly int TriangleResultsId = Shader.PropertyToID("_TriangleResults");
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

        [Header("Scene")]
        [SerializeField] MeshRenderer receiver;
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
        [SerializeField] bool showSourceGrid = true;
        [SerializeField] Color sourceGridColor = Color.cyan;

        [Header("Validation")]
        [SerializeField] bool runContinuously;
        [SerializeField, Min(0)] float planeHitTolerance = 0.001f;
        [SerializeField, Min(0)] float scalarRelativeTolerance = 0.001f;
        [SerializeField, Min(0)] float densityRelativeTolerance = 0.005f;

        RayTracingAccelerationStructure accelerationStructure;
        GraphicsBuffer sourceVertexBuffer;
        GraphicsBuffer sourceIndexBuffer;
        GraphicsBuffer sourceLineIndexBuffer;
        GraphicsBuffer hitBuffer;
        GraphicsBuffer triangleResultBuffer;
        SourceVertex[] sourceVertices;
        uint[] sourceIndices;
        uint[] sourceLineIndices;
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
        int receiverHandle = -1;
        int traceKernel;
        int buildTrianglesKernel;
        bool initialized;
        int readbackPendingCount;
        uint dispatchGeneration;
        uint resourceGeneration;
        bool validationRefreshRequested;
        bool hasDispatched;
        Matrix4x4 lastSourceTransform;
        Matrix4x4 lastReceiverTransform;
        Vector3 lastIncidentDirection;

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
            sourceVertexBuffer?.SetData(sourceVertices);
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
            if (initialized && GridTopologyChanged())
                ReleaseResources();
            RebuildGridData();
            if (!Application.isPlaying || !initialized)
                UpdateSourceVertices();
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
            var receiverTransform = receiver.transform.localToWorldMatrix;
            var changed = sourceTransform != lastSourceTransform
                || receiverTransform != lastReceiverTransform
                || incidentDirection != lastIncidentDirection;

            if (!runContinuously && !animateWave && hasDispatched && !changed)
            {
                QueueProjectedGrid();
                QueueSourceGridOutline();
                return;
            }

            if (receiverTransform != lastReceiverTransform)
            {
                accelerationStructure.UpdateInstanceTransform(receiverHandle, receiverTransform);
                accelerationStructure.Build();
            }

            Dispatch(incidentDirection);
            lastSourceTransform = sourceTransform;
            lastReceiverTransform = receiverTransform;
            lastIncidentDirection = incidentDirection;
            hasDispatched = true;
            QueueProjectedGrid();
            QueueSourceGridOutline();
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
            if (receiver == null || directionalLight == null || rayQueryShader == null || projectedTriangleShader == null)
            {
                Debug.LogError("Receiver, Directional Light, Ray Query Shader, and Projected Triangle Shader must be assigned.", this);
                enabled = false;
                return false;
            }
            if (directionalLight.type != LightType.Directional)
            {
                Debug.LogError("The assigned light must be a Directional Light.", this);
                enabled = false;
                return false;
            }

            var meshFilter = receiver.GetComponent<MeshFilter>();
            var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null || mesh.subMeshCount != 1)
            {
                Debug.LogError("The minimal receiver must have one mesh and one submesh.", receiver);
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
            var instanceConfig = new RayTracingMeshInstanceConfig(mesh, 0, null)
            {
                subMeshFlags = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                enableTriangleCulling = false,
                mask = ReceiverMask,
            };
            receiverHandle = accelerationStructure.AddInstance(
                instanceConfig,
                receiver.transform.localToWorldMatrix,
                null,
                ReceiverInstanceId);
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

            projectedTriangleMaterial = new Material(projectedTriangleShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            projectedTriangleMaterial.SetBuffer(SourceIndicesId, sourceIndexBuffer);
            projectedTriangleMaterial.SetBuffer(HitsId, hitBuffer);
            projectedTriangleMaterial.SetBuffer(TriangleResultsId, triangleResultBuffer);

            traceKernel = rayQueryShader.FindKernel("TraceVertices");
            buildTrianglesKernel = rayQueryShader.FindKernel("BuildTriangles");
            initialized = true;
            lastSourceTransform = transform.localToWorldMatrix;
            lastReceiverTransform = receiver.transform.localToWorldMatrix;
            lastIncidentDirection = directionalLight.transform.forward.normalized;

            Debug.Log(
                $"Caustic grid initialized with {vertexCount} shared vertices and {triangleCount} triangles "
                + $"({cellCountX}x{cellCountZ} square cells, actual size {actualSourceSize.x:F3}x{actualSourceSize.y:F3}) "
                + $"on {SystemInfo.graphicsDeviceName}.",
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

            projectedTriangleMaterial.SetColor(ColorId, causticColor);
            projectedTriangleMaterial.SetFloat(IntensityScaleId, intensityScale);
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
                ValidateHits(request.GetData<RayHit>(), incidentDirection);
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
                ValidateTriangles(request.GetData<TriangleResult>(), incidentDirection);
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

        void ValidateHits(NativeArray<RayHit> hits, Vector3 incidentDirection)
        {
            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var primitiveIds = new HashSet<uint>();
            var validCount = 0;
            var maxError = 0f;

            for (var i = 0; i < vertexCount; i++)
            {
                var origin = GetSourceVertexWorld(i);
                var refractedDirection = Refract(
                    incidentDirection,
                    GetSourceNormalWorld(i),
                    1f / transmittedRefractiveIndex);
                var expectedDistance = 0f;
                var expectedValid = refractedDirection != Vector3.zero
                    && receiverPlane.Raycast(new Ray(origin, refractedDirection), out expectedDistance)
                    && expectedDistance >= rayTMin
                    && expectedDistance <= rayTMax;
                var hit = hits[i];
                if (!expectedValid || hit.valid == 0 || hit.instanceId != ReceiverInstanceId)
                    continue;

                var expectedPosition = origin + refractedDirection * expectedDistance;
                maxError = Mathf.Max(maxError, Vector3.Distance(expectedPosition, hit.position));
                primitiveIds.Add(hit.primitiveIndex);
                validCount++;
            }

            if (validCount == vertexCount && maxError <= planeHitTolerance)
            {
                Debug.Log(
                    $"Caustic grid Ray Query validation passed: {validCount}/{vertexCount} vertices, "
                    + $"{primitiveIds.Count} receiver primitives, max error={maxError:E3}.",
                    this);
            }
            else
            {
                Debug.LogError(
                    $"Caustic grid Ray Query validation failed: {validCount}/{vertexCount} vertices, max error={maxError:E3}.",
                    this);
            }
        }

        void ValidateTriangles(NativeArray<TriangleResult> results, Vector3 incidentDirection)
        {
            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var projected = new Vector3[vertexCount];
            var projectedValid = true;
            for (var i = 0; i < vertexCount; i++)
            {
                var origin = GetSourceVertexWorld(i);
                var refractedDirection = Refract(
                    incidentDirection,
                    GetSourceNormalWorld(i),
                    1f / transmittedRefractiveIndex);
                var distance = 0f;
                projectedValid &= refractedDirection != Vector3.zero
                    && receiverPlane.Raycast(new Ray(origin, refractedDirection), out distance)
                    && distance >= rayTMin
                    && distance <= rayTMax;
                projected[i] = origin + refractedDirection * distance;
            }

            var validatedCount = 0;
            var drawableCount = 0;
            var degenerateCount = 0;
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
                var expectedIncidentArea = 0.5f * Mathf.Max(
                    0,
                    Vector3.Dot(-incidentDirection.normalized, Vector3.Cross(x1 - x0, x2 - x0)));
                var expectedReceiverArea = projectedValid
                    ? 0.5f * Vector3.Cross(projected[i1] - projected[i0], projected[i2] - projected[i0]).magnitude
                    : 0;
                var expectedIntensity = expectedIncidentArea / Mathf.Max(expectedReceiverArea, minReceiverArea);
                var result = results[triangleIndex];
                var expectedDrawable = projectedValid
                    && expectedIncidentArea > 0
                    && expectedReceiverArea > 0;
                var gpuDrawable = result.valid != 0;
                if (!expectedDrawable && !gpuDrawable)
                {
                    validatedCount++;
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
                    + $"({drawableCount} drawable, {degenerateCount} degenerate), "
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
                receiver.bounds,
                MeshTopology.Triangles,
                drawVertexCount,
                1,
                null,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
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
            receiverHandle = -1;
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
