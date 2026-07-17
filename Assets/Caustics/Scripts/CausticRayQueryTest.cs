using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CausticMeshDxr
{
    public sealed class CausticRayQueryTest : MonoBehaviour
    {
        const int VertexCount = 3;
        const uint ReceiverMask = 1;
        const uint ReceiverInstanceId = 1;

        static readonly int AccelerationStructureId = Shader.PropertyToID("_AccelerationStructure");
        static readonly int HitsId = Shader.PropertyToID("_Hits");
        static readonly int SourceLocalToWorldId = Shader.PropertyToID("_SourceLocalToWorld");
        static readonly int SourceNormalId = Shader.PropertyToID("_SourceNormal");
        static readonly int SourceSizeId = Shader.PropertyToID("_SourceSize");
        static readonly int IncidentDirectionId = Shader.PropertyToID("_IncidentDirection");
        static readonly int EtaId = Shader.PropertyToID("_Eta");
        static readonly int RayTMinId = Shader.PropertyToID("_RayTMin");
        static readonly int RayTMaxId = Shader.PropertyToID("_RayTMax");
        static readonly int TriangleResultId = Shader.PropertyToID("_TriangleResult");
        static readonly int MinReceiverAreaId = Shader.PropertyToID("_MinReceiverArea");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int IntensityScaleId = Shader.PropertyToID("_IntensityScale");

        static readonly Vector3[] LocalVertices =
        {
            new(-0.5f, 0, -0.5f),
            new(-0.5f, 0,  0.5f),
            new( 0.5f, 0, -0.5f),
        };

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
        GraphicsBuffer hitBuffer;
        GraphicsBuffer triangleResultBuffer;
        Material projectedTriangleMaterial;
        int receiverHandle = -1;
        int traceKernel;
        int buildTriangleKernel;
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
                QueueProjectedTriangle();
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
            QueueProjectedTriangle();
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

            hitBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                VertexCount,
                Marshal.SizeOf<RayHit>());
            triangleResultBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                Marshal.SizeOf<TriangleResult>());
            projectedTriangleMaterial = new Material(projectedTriangleShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            projectedTriangleMaterial.SetBuffer(HitsId, hitBuffer);
            projectedTriangleMaterial.SetBuffer(TriangleResultId, triangleResultBuffer);

            traceKernel = rayQueryShader.FindKernel("TraceVertices");
            buildTriangleKernel = rayQueryShader.FindKernel("BuildTriangle");
            initialized = true;

            lastSourceTransform = transform.localToWorldMatrix;
            lastReceiverTransform = receiver.transform.localToWorldMatrix;
            lastIncidentDirection = directionalLight.transform.forward.normalized;

            Debug.Log(
                $"Caustic Ray Query initialized with {SystemInfo.graphicsDeviceName}; inline ray tracing is supported.",
                this);
            return true;
        }

        void Dispatch(Vector3 incidentDirection)
        {
            rayQueryShader.SetRayTracingAccelerationStructure(traceKernel, AccelerationStructureId, accelerationStructure);
            rayQueryShader.SetBuffer(traceKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTriangleKernel, HitsId, hitBuffer);
            rayQueryShader.SetBuffer(buildTriangleKernel, TriangleResultId, triangleResultBuffer);
            rayQueryShader.SetMatrix(SourceLocalToWorldId, transform.localToWorldMatrix);
            rayQueryShader.SetVector(SourceNormalId, transform.up.normalized);
            rayQueryShader.SetFloat(SourceSizeId, sourceSize);
            rayQueryShader.SetVector(IncidentDirectionId, incidentDirection);
            rayQueryShader.SetFloat(EtaId, 1f / transmittedRefractiveIndex);
            rayQueryShader.SetFloat(RayTMinId, rayTMin);
            rayQueryShader.SetFloat(RayTMaxId, rayTMax);
            rayQueryShader.SetFloat(MinReceiverAreaId, minReceiverArea);
            rayQueryShader.Dispatch(traceKernel, 1, 1, 1);
            rayQueryShader.Dispatch(buildTriangleKernel, 1, 1, 1);

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

                ValidateTriangle(request.GetData<TriangleResult>()[0], incidentDirection);
            });
        }

        void CompleteReadback()
        {
            if (this != null)
                readbackPendingCount = Mathf.Max(0, readbackPendingCount - 1);
        }

        void ValidateHits(Unity.Collections.NativeArray<RayHit> hits, Vector3 incidentDirection)
        {
            var sourceNormal = transform.up.normalized;
            var refractedDirection = Refract(incidentDirection, sourceNormal, 1f / transmittedRefractiveIndex);
            if (refractedDirection == Vector3.zero)
            {
                Debug.LogError("The test ray produced total internal reflection.", this);
                return;
            }

            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var allValid = true;
            var sameReceiver = true;
            var details = string.Empty;

            for (var i = 0; i < VertexCount; i++)
            {
                var origin = GetSourceVertexWorld(i);
                var expectedValid = receiverPlane.Raycast(new Ray(origin, refractedDirection), out var expectedDistance)
                    && expectedDistance >= rayTMin
                    && expectedDistance <= rayTMax;
                var expectedPosition = origin + refractedDirection * expectedDistance;
                var hit = hits[i];
                var error = expectedValid && hit.valid != 0
                    ? Vector3.Distance(expectedPosition, hit.position)
                    : float.PositiveInfinity;

                allValid &= expectedValid && hit.valid != 0 && error <= planeHitTolerance;
                sameReceiver &= hit.valid != 0 && hit.instanceId == ReceiverInstanceId;
                details += $"\n  v{i}: valid={hit.valid}, instance={hit.instanceId}, primitive={hit.primitiveIndex}, "
                    + $"t={hit.distance:F6}, error={error:E3}";
            }

            if (allValid && sameReceiver)
                Debug.Log($"Caustic Ray Query validation passed.{details}", this);
            else
                Debug.LogError($"Caustic Ray Query validation failed.{details}", this);
        }

        void ValidateTriangle(TriangleResult result, Vector3 incidentDirection)
        {
            var x0 = GetSourceVertexWorld(0);
            var x1 = GetSourceVertexWorld(1);
            var x2 = GetSourceVertexWorld(2);
            var expectedIncidentArea = 0.5f * Mathf.Max(
                0,
                Vector3.Dot(-incidentDirection.normalized, Vector3.Cross(x1 - x0, x2 - x0)));

            var refractedDirection = Refract(
                incidentDirection,
                transform.up.normalized,
                1f / transmittedRefractiveIndex);
            var receiverPlane = new Plane(receiver.transform.forward, receiver.transform.position);
            var projected = new Vector3[VertexCount];
            var expectedValid = refractedDirection != Vector3.zero;
            for (var i = 0; i < VertexCount && expectedValid; i++)
            {
                var origin = GetSourceVertexWorld(i);
                expectedValid &= receiverPlane.Raycast(new Ray(origin, refractedDirection), out var distance)
                    && distance >= rayTMin
                    && distance <= rayTMax;
                projected[i] = origin + refractedDirection * distance;
            }

            var expectedReceiverArea = expectedValid
                ? 0.5f * Vector3.Cross(projected[1] - projected[0], projected[2] - projected[0]).magnitude
                : 0;
            var expectedIntensity = expectedIncidentArea / Mathf.Max(expectedReceiverArea, minReceiverArea);
            var passed = expectedValid
                && result.valid != 0
                && RelativeError(result.incidentArea, expectedIncidentArea) <= scalarRelativeTolerance
                && RelativeError(result.receiverArea, expectedReceiverArea) <= scalarRelativeTolerance
                && RelativeError(result.intensity, expectedIntensity) <= scalarRelativeTolerance;
            var details = $"Ai={result.incidentArea:F6}, Ar={result.receiverArea:F6}, C={result.intensity:F6}";

            if (passed)
                Debug.Log($"Caustic triangle density validation passed: {details}", this);
            else
                Debug.LogError($"Caustic triangle density validation failed: {details}", this);
        }

        static float RelativeError(float actual, float expected)
        {
            return Mathf.Abs(actual - expected) / Mathf.Max(Mathf.Abs(expected), 0.000001f);
        }

        Vector3 GetSourceVertexWorld(int index)
        {
            return transform.TransformPoint(LocalVertices[index] * sourceSize);
        }

        void QueueProjectedTriangle()
        {
            if (!initialized || projectedTriangleMaterial == null)
                return;

            Graphics.DrawProcedural(
                projectedTriangleMaterial,
                receiver.bounds,
                MeshTopology.Triangles,
                VertexCount,
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
            var v0 = GetSourceVertexWorld(0);
            var v1 = GetSourceVertexWorld(1);
            var v2 = GetSourceVertexWorld(2);
            Gizmos.DrawLine(v0, v1);
            Gizmos.DrawLine(v1, v2);
            Gizmos.DrawLine(v2, v0);
        }

        void OnDisable()
        {
            initialized = false;
            readbackPendingCount = 0;
            receiverHandle = -1;
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
