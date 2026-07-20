using UnityEngine;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    public sealed class RadialSineHeightField : CausticHeightField
    {
        const int ThreadGroupSize = 8;
        const string HeightMapKernelName = "BuildRadialSineHeightMap";
        static readonly int HeightMapOutputId = Shader.PropertyToID("_HeightMapOutput");
        static readonly int HeightMapSizeId = Shader.PropertyToID("_HeightMapSize");
        static readonly int HeightMapSourceSizeId = Shader.PropertyToID("_HeightMapSourceSize");
        static readonly int HeightFieldAmplitudeId = Shader.PropertyToID("_HeightFieldAmplitude");
        static readonly int HeightFieldAngularFrequencyId = Shader.PropertyToID("_HeightFieldAngularFrequency");
        static readonly int HeightFieldCenterId = Shader.PropertyToID("_HeightFieldCenter");
        static readonly int HeightFieldCenterSmoothingId = Shader.PropertyToID("_HeightFieldCenterSmoothing");
        static readonly int HeightFieldPhaseOffsetId = Shader.PropertyToID("_HeightFieldPhaseOffset");

        [SerializeField, Range(0, 0.25f)] float amplitude = 0.03f;
        [SerializeField, Min(0.001f)] float waveLength = 1;
        [SerializeField] Vector2 center;
        [SerializeField, Min(0.0001f)] float centerSmoothing = 0.05f;
        [SerializeField] bool animate;
        [SerializeField] float speed = 1;
        [SerializeField, Range(0, 1)] float phase;

        public override bool IsTimeVarying => animate;
        public override float MaximumAbsoluteHeight => Mathf.Abs(amplitude);
        ComputeShader heightMapShader;
        int heightMapKernel = -1;

        public override int StateHash
        {
            get
            {
                unchecked
                {
                    var hash = amplitude.GetHashCode();
                    hash = hash * 397 ^ waveLength.GetHashCode();
                    hash = hash * 397 ^ center.GetHashCode();
                    hash = hash * 397 ^ centerSmoothing.GetHashCode();
                    hash = hash * 397 ^ animate.GetHashCode();
                    hash = hash * 397 ^ speed.GetHashCode();
                    return hash * 397 ^ phase.GetHashCode();
                }
            }
        }

        public override void Evaluate(
            Vector2 localPosition,
            float time,
            out float height,
            out Vector2 gradient)
        {
            var angularFrequency = 2f * Mathf.PI / Mathf.Max(0.001f, waveLength);
            var phaseOffset = 2f * Mathf.PI * (animate ? time * speed : phase);
            var smoothing = Mathf.Max(0.0001f, centerSmoothing);
            var offset = localPosition - center;
            var smoothDistance = Mathf.Sqrt(offset.sqrMagnitude + smoothing * smoothing);
            var radius = smoothDistance - smoothing;
            var wavePhase = angularFrequency * radius - phaseOffset;
            height = amplitude * Mathf.Sin(wavePhase);
            var radialSlope = amplitude * angularFrequency * Mathf.Cos(wavePhase);
            gradient = radialSlope * offset / smoothDistance;
        }

        internal override bool TryDispatchGpuHeightMap(
            ComputeShader shader,
            int width,
            int height,
            Vector2 sourceSize,
            float time,
            float deltaTime,
            out Texture outputHeightMap)
        {
            var heightMap = EnsureGpuHeightMap(width, height);
            if (heightMapShader != shader || heightMapKernel < 0)
            {
                heightMapShader = shader;
                heightMapKernel = shader.FindKernel(HeightMapKernelName);
            }

            var angularFrequency = 2f * Mathf.PI / Mathf.Max(0.001f, waveLength);
            var phaseOffset = 2f * Mathf.PI * (animate ? time * speed : phase);
            shader.SetTexture(heightMapKernel, HeightMapOutputId, heightMap);
            shader.SetInts(HeightMapSizeId, width, height);
            shader.SetVector(
                HeightMapSourceSizeId,
                new Vector4(sourceSize.x, sourceSize.y, 0, 0));
            shader.SetFloat(HeightFieldAmplitudeId, amplitude);
            shader.SetFloat(HeightFieldAngularFrequencyId, angularFrequency);
            shader.SetVector(HeightFieldCenterId, new Vector4(center.x, center.y, 0, 0));
            shader.SetFloat(HeightFieldCenterSmoothingId, centerSmoothing);
            shader.SetFloat(HeightFieldPhaseOffsetId, phaseOffset);
            shader.Dispatch(
                heightMapKernel,
                DivideRoundUp(width, ThreadGroupSize),
                DivideRoundUp(height, ThreadGroupSize),
                1);
            outputHeightMap = heightMap;
            return true;
        }

    }
}
