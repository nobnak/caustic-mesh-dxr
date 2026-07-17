using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CausticMeshDxr
{
    public sealed class CausticRayQueryTest : MonoBehaviour
    {
        const int GridResolution = 4;
        const int VertexCount = GridResolution * GridResolution;
        const int CellCount = GridResolution - 1;
        const int TriangleCount = CellCount * CellCount * 2;
        const int DrawVertexCount = TriangleCount * 3;
        const int ComputeThreadCount = 64;
        const uint ReceiverMask = 1;
        const uint ReceiverInstanceId = 1;

        static readonly int AccelerationStructureId = Shader.PropertyToID("_AccelerationStructure");
        static readonly int SourceVerticesId = Shader.PropertyToID("_SourceVertices");
        static readonly int SourceIndicesId = Shader.PropertyToID("_SourceIndices");
        static readonly int HitsId = Shader.PropertyToID("_Hits");
        static readonly int TriangleResultsId = Shader.PropertyToID("_TriangleResults");
        static readonly int SourceLocalToWorldId = Shader.PropertyToID("_SourceLocalToWorld");
        static readonly int SourceNormalId = Shader.PropertyToID("_SourceNormal");
        static readonly int SourceSizeId = Shader.PropertyToID("_SourceSize");
        static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
        static readonly int TriangleCountId = Shader.PropertyToID("_TriangleCount");
        static readonly int IncidentDirectionId = Shader.PropertyToID("_IncidentDirection");
        static readonly int EtaId = Shader.PropertyToID("_Eta");
        static readonly int RayTMinId = Shader.PropertyToID("_RayTMin");
        static readonly int RayTMaxId = Shader.PropertyToID("_RayTMax");
        static readonly int MinReceiverAreaId = Shader.PropertyToID("_MinReceiverArea");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int IntensityScaleId = Shader.PropertyToID("_IntensityScale");

        static readonly Vector3[] LocalVertices = CreateGridVertices();
        static readonly uint[] SourceIndices = CreateGridIndices();

        [Header("Scene")]
        [SerializeField] MeshRenderer receiver;
        [SerializeField] Light directionalLight;
        [SerializeField] ComputeShader rayQueryShader;
        [SerializeField] Shader projectedTriangleShader;

        [Header("Refraction")]
        [SerializeField, Min(0.001f)] float sourceSize = 2;
        [SerializeField, Min(1)] float transmittedRefractiveIndex = 1.333f;
        [SerializeField, Min(0.000001f)] float rayTMin = 0.001f;
        [SerializeField, Min(0.01f)] float rayTMax = 100;
        [SerializeField, Min(0.00000001f)] float minReceiverArea = 0.00001f;

        [Header("Display")]
        [SerializeField] Color causticColor = new(1, 0.7f, 0.1f, 1);
        [SerializeField, Min(0)] float intensityScale = 0.25f;

        [Header("Validation")]
        [SerializeField] bool runContinuously;
        [SerializeField, Min(0)] float planeHitTolerance = 0.001f;
        [SerializeField, Min(0)] float scalarRelativeTolerance = 0.0001f;

        RayTracingAccelerationStructure accelerationStructure;
        GraphicsBuffer sourceVertexBuffer;
        GraphicsBuffer sourceIndexBuffer;
        GraphicsBuffer hitBuffer;
        GraphicsBuffer triangleResultBuffer;
        Material projectedTriangleMaterial;
        int receiverHandle = -1;
        int traceKernel;
        int buildTrianglesKernel;
        bool initialized;
        int readbackPendingCount;
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
        struct TriangleResult
        {
            public float intensity;
            public float incidentArea;
            public float receiverArea;
            public uint valid;
        }

        static Vector3[] CreateGridVertices()
        {
            var vertices = new Vector3[VertexCount];
            for (var z = 0; z < GridResolution; z++)
            {
                for (var x = 0; x < GridResolution; x++)
                {
                    vertices[z * GridResolution + x] = new Vector3(
                        (float)x / CellCount - 0.5f,
                        0,
                        (float)z / CellCount - 0.5f);
                }
            }
            return vertices;
        }

        static uint[] CreateGridIndices()
        {
            var indices = new uint[DrawVertexCount];
            var writeIndex = 0;
            for (var z = 0; z < CellCount; z++)
            {
                for (var x = 0; x < CellCount; x++)
                {
                    var v00 = (uint)(z * GridResolution + x);
                    var v10 = v00 + 1;
                    var v01 = v00 + GridResolution;
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

        void OnEnable()
        {
            hasDispatched = false;
        }

        void OnValidate()
        {
            hasDispatched = false;
        }

        void Update()
        {
            if (!EnsureInitialized())
                return;

            var incidentDirection = directionalLight.transform.forward.normalized;
            var sourceTransform = transform.localToWorldMatrix;
            var receiverTransform = receiver.transform.localToWorldMatrix;
            var changed = sourceTransform != lastSourceTransform
                || receiverTransform != lastReceiverTransform
                || incidentDirection != lastIncidentDirection;

            if (!runContinuously && hasDispatched && !changed)
            {
                QueueProjectedGrid();
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

            sourceVertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VertexCount, Marshal.SizeOf<Vector3>());
            sourceVertexBuffer.SetData(LocalVertices);
            sourceIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, DrawVertexCount, sizeof(uint));
            sourceIndexBuffer.SetData(SourceIndices);
            hitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VertexCount, Marshal.SizeOf<RayHit>());
            triangleResultBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                TriangleCount,
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
                $"Caustic grid initialized with {VertexCount} shared vertices and {TriangleCount} triangles on {SystemInfo.graphicsDeviceName}.",
                this);
            return true;
        }

        void Dispatch(Vector3 incidentDirection)
        {
            rayQueryShader.SetRayTracingAccelerationStructure(traceKernel, AccelerationStructureId, accelerationStructure);
            rayQueryShader.SetBuffer(traceKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(traceKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceVerticesId, sourceVertexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, SourceIndicesId, sourceIndexBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTrianglesKernel, TriangleResultsId, triangleResultBuffer);
            rayQueryShader.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            rayQueryShader.SetVector(SourceNormalId, transform.up.normalized);
            rayQueryShader.SetFloat(SourceSizeId, sourceSize);
            rayQueryShader.SetInt(VertexCountId, VertexCount);
            rayQueryShader.SetInt(TriangleCountId, TriangleCount);
            rayQueryShader.SetVector(IncidentDirectionId, incidentDirection);
            rayQueryShader.SetFloat(EtaId, 1f / transmittedRefractiveIndex);
            rayQueryShader.SetFloat(RayTMinId, rayTMin);
            rayQueryShader.SetFloat(RayTMaxId, rayTMax);
            rayQueryShader.SetFloat(MinReceiverAreaId, minReceiverArea);
            rayQueryShader.Dispatch(traceKernel, DivideRoundUp(VertexCount, ComputeThreadCount), 1, 1);
            rayQueryShader.Dispatch(buildTrianglesKernel, DivideRoundUp(TriangleCount, ComputeThreadCount), 1, 1);

            projectedTriangleMaterial.SetColor(ColorId, causticColor);
            projectedTriangleMaterial.SetFloat(IntensityScaleId, intensityScale);
            if (readbackPendingCount != 0)
                return;

            readbackPendingCount = 2;
            AsyncGPUReadback.Request(hitBuffer, request =>
            {
                CompleteReadback();
                if (this == null || !isActiveAndEnabled)
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
                CompleteReadback();
                if (this == null || !isActiveAndEnabled)
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

        void CompleteReadback()
        {
            if (this != null)
                readbackPendingCount = Mathf.Max(0, readbackPendingCount - 1);
        }

        void ValidateHits(NativeArray<RayHit> hits, Vector3 incidentDirection)
        {
            var refractedDirection = Refract(
                incidentDirection,
                transform.up.normalized,
                1f / transmittedRefractiveIndex);
            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var primitiveIds = new HashSet<uint>();
            var validCount = 0;
            var maxError = 0f;

            for (var i = 0; i < VertexCount; i++)
            {
                var origin = GetSourceVertexWorld(i);
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

            if (validCount == VertexCount && maxError <= planeHitTolerance)
            {
                Debug.Log(
                    $"Caustic grid Ray Query validation passed: {validCount}/{VertexCount} vertices, "
                    + $"{primitiveIds.Count} receiver primitives, max error={maxError:E3}.",
                    this);
            }
            else
            {
                Debug.LogError(
                    $"Caustic grid Ray Query validation failed: {validCount}/{VertexCount} vertices, max error={maxError:E3}.",
                    this);
            }
        }

        void ValidateTriangles(NativeArray<TriangleResult> results, Vector3 incidentDirection)
        {
            var refractedDirection = Refract(
                incidentDirection,
                transform.up.normalized,
                1f / transmittedRefractiveIndex);
            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var projected = new Vector3[VertexCount];
            var projectedValid = refractedDirection != Vector3.zero;
            for (var i = 0; i < VertexCount && projectedValid; i++)
            {
                var origin = GetSourceVertexWorld(i);
                projectedValid &= receiverPlane.Raycast(new Ray(origin, refractedDirection), out var distance)
                    && distance >= rayTMin
                    && distance <= rayTMax;
                projected[i] = origin + refractedDirection * distance;
            }

            var validCount = 0;
            var minIntensity = float.PositiveInfinity;
            var maxIntensity = float.NegativeInfinity;
            for (var triangleIndex = 0; triangleIndex < TriangleCount; triangleIndex++)
            {
                var indexOffset = triangleIndex * 3;
                var i0 = (int)SourceIndices[indexOffset];
                var i1 = (int)SourceIndices[indexOffset + 1];
                var i2 = (int)SourceIndices[indexOffset + 2];
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
                var valid = projectedValid
                    && result.valid != 0
                    && RelativeError(result.incidentArea, expectedIncidentArea) <= scalarRelativeTolerance
                    && RelativeError(result.receiverArea, expectedReceiverArea) <= scalarRelativeTolerance
                    && RelativeError(result.intensity, expectedIntensity) <= scalarRelativeTolerance;
                if (!valid)
                    continue;

                validCount++;
                minIntensity = Mathf.Min(minIntensity, result.intensity);
                maxIntensity = Mathf.Max(maxIntensity, result.intensity);
            }

            if (validCount == TriangleCount)
            {
                Debug.Log(
                    $"Caustic grid density validation passed: {validCount}/{TriangleCount} triangles, "
                    + $"C={minIntensity:F6}..{maxIntensity:F6}.",
                    this);
            }
            else
            {
                Debug.LogError($"Caustic grid density validation failed: {validCount}/{TriangleCount} triangles.", this);
            }
        }

        static float RelativeError(float actual, float expected)
        {
            return Mathf.Abs(actual - expected) / Mathf.Max(Mathf.Abs(expected), 0.000001f);
        }

        Vector3 GetSourceVertexWorld(int index)
        {
            return transform.TransformPoint(LocalVertices[index] * sourceSize);
        }

        void QueueProjectedGrid()
        {
            if (!initialized || projectedTriangleMaterial == null)
                return;

            Graphics.DrawProcedural(
                projectedTriangleMaterial,
                receiver.bounds,
                MeshTopology.Triangles,
                DrawVertexCount,
                1,
                null,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
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

        void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            for (var i = 0; i < SourceIndices.Length; i += 3)
            {
                var v0 = GetSourceVertexWorld((int)SourceIndices[i]);
                var v1 = GetSourceVertexWorld((int)SourceIndices[i + 1]);
                var v2 = GetSourceVertexWorld((int)SourceIndices[i + 2]);
                Gizmos.DrawLine(v0, v1);
                Gizmos.DrawLine(v1, v2);
                Gizmos.DrawLine(v2, v0);
            }
        }

        void OnDisable()
        {
            initialized = false;
            readbackPendingCount = 0;
            receiverHandle = -1;
            sourceVertexBuffer?.Dispose();
            sourceVertexBuffer = null;
            sourceIndexBuffer?.Dispose();
            sourceIndexBuffer = null;
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
        }
    }
}
