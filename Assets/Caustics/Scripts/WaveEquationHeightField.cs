using UnityEngine;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    public sealed class WaveEquationHeightField : CausticHeightField
    {
        const int ThreadGroupSize = 8;
        const int MaximumStepsPerFrame = 4;
        const string ClearKernelName = "ClearWaveHeightMap";
        const string WaveKernelName = "BuildWaveHeightMap";
        static readonly int HeightMapSizeId = Shader.PropertyToID("_HeightMapSize");
        static readonly int HeightMapSourceSizeId = Shader.PropertyToID("_HeightMapSourceSize");
        static readonly int WavePreviousId = Shader.PropertyToID("_WavePrevious");
        static readonly int WaveCurrentId = Shader.PropertyToID("_WaveCurrent");
        static readonly int WaveNextId = Shader.PropertyToID("_WaveNext");
        static readonly int WaveStepCoefficientId = Shader.PropertyToID("_WaveStepCoefficient");
        static readonly int WaveDampingId = Shader.PropertyToID("_WaveDamping");
        static readonly int WaveSourceCenterId = Shader.PropertyToID("_WaveSourceCenter");
        static readonly int WaveSourceRadiusId = Shader.PropertyToID("_WaveSourceRadius");
        static readonly int WaveSourceHeightId = Shader.PropertyToID("_WaveSourceHeight");

        [SerializeField] bool simulate = true;
        [SerializeField, Min(0.001f)] float waveSpeed = 1;
        [SerializeField, Min(0)] float damping = 0.5f;
        [SerializeField, Min(1)] float simulationRate = 60;
        [Header("Wave Source")]
        [SerializeField] Vector2 sourceCenter;
        [SerializeField, Min(0.001f)] float sourceRadius = 0.15f;
        [SerializeField, Range(0, 0.25f)] float sourceAmplitude = 0.03f;
        [SerializeField, Min(0)] float sourceFrequency = 1;
        [Header("Source Motion")]
        [SerializeField] bool moveSourceWithNoise;
        [SerializeField, Min(0)] float sourceMovementRange = 1;
        [SerializeField, Min(0)] float sourceMovementSpeed = 0.2f;
        [SerializeField] int sourceNoiseSeed;

        RenderTexture previousHeight;
        RenderTexture currentHeight;
        RenderTexture nextHeight;
        ComputeShader simulationShader;
        int clearKernel = -1;
        int waveKernel = -1;
        int textureWidth;
        int textureHeight;
        int simulationStateHash;
        float accumulatedTime;

        public override bool IsTimeVarying => simulate;
        public override float MaximumAbsoluteHeight => Mathf.Abs(sourceAmplitude) * 4;

        public override int StateHash
        {
            get
            {
                unchecked
                {
                    var hash = simulate.GetHashCode();
                    hash = hash * 397 ^ waveSpeed.GetHashCode();
                    hash = hash * 397 ^ damping.GetHashCode();
                    hash = hash * 397 ^ simulationRate.GetHashCode();
                    hash = hash * 397 ^ sourceCenter.GetHashCode();
                    hash = hash * 397 ^ sourceRadius.GetHashCode();
                    hash = hash * 397 ^ sourceAmplitude.GetHashCode();
                    hash = hash * 397 ^ sourceFrequency.GetHashCode();
                    hash = hash * 397 ^ moveSourceWithNoise.GetHashCode();
                    hash = hash * 397 ^ sourceMovementRange.GetHashCode();
                    hash = hash * 397 ^ sourceMovementSpeed.GetHashCode();
                    return hash * 397 ^ sourceNoiseSeed;
                }
            }
        }

        public override void Evaluate(
            Vector2 localPosition,
            float time,
            out float height,
            out Vector2 gradient)
        {
            height = 0;
            gradient = Vector2.zero;
        }

        public override bool TryValidateCpu(out string error)
        {
            error = "Wave Equation Height Field is GPU-only and cannot use CPU validation.";
            return false;
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
            EnsureSimulationResources(shader, width, height);
            if (!simulate)
            {
                outputHeightMap = currentHeight;
                return true;
            }

            var cellSize = Mathf.Min(
                sourceSize.x / Mathf.Max(1, width - 1),
                sourceSize.y / Mathf.Max(1, height - 1));
            var requestedStep = 1f / Mathf.Max(1, simulationRate);
            var stableStep = 0.95f * cellSize / (Mathf.Max(0.001f, waveSpeed) * Mathf.Sqrt(2));
            var stepDuration = Mathf.Min(requestedStep, stableStep);
            accumulatedTime = Mathf.Min(accumulatedTime + Mathf.Max(0, deltaTime), stepDuration * MaximumStepsPerFrame);
            var stepCount = Mathf.Min(MaximumStepsPerFrame, Mathf.FloorToInt(accumulatedTime / stepDuration));
            for (var step = 0; step < stepCount; step++)
            {
                var stepTime = time - accumulatedTime + stepDuration;
                DispatchSimulationStep(shader, width, height, sourceSize, cellSize, stepDuration, stepTime);
                accumulatedTime -= stepDuration;
            }

            outputHeightMap = currentHeight;
            return true;
        }

        void EnsureSimulationResources(ComputeShader shader, int width, int height)
        {
            var currentStateHash = StateHash;
            if (simulationShader == shader
                && previousHeight != null
                && textureWidth == width
                && textureHeight == height
                && simulationStateHash == currentStateHash)
                return;

            ReleaseSimulationResources();
            simulationShader = shader;
            clearKernel = shader.FindKernel(ClearKernelName);
            waveKernel = shader.FindKernel(WaveKernelName);
            textureWidth = width;
            textureHeight = height;
            simulationStateHash = currentStateHash;
            previousHeight = CreateHeightTexture(width, height, "Previous");
            currentHeight = CreateHeightTexture(width, height, "Current");
            nextHeight = CreateHeightTexture(width, height, "Next");
            ClearHeightTexture(shader, previousHeight, width, height);
            ClearHeightTexture(shader, currentHeight, width, height);
            ClearHeightTexture(shader, nextHeight, width, height);
            accumulatedTime = 0;
        }

        void DispatchSimulationStep(
            ComputeShader shader,
            int width,
            int height,
            Vector2 sourceSize,
            float cellSize,
            float stepDuration,
            float stepTime)
        {
            var coefficient = Mathf.Pow(waveSpeed * stepDuration / cellSize, 2);
            shader.SetTexture(waveKernel, WavePreviousId, previousHeight);
            shader.SetTexture(waveKernel, WaveCurrentId, currentHeight);
            shader.SetTexture(waveKernel, WaveNextId, nextHeight);
            shader.SetInts(HeightMapSizeId, width, height);
            shader.SetVector(
                HeightMapSourceSizeId,
                new Vector4(sourceSize.x, sourceSize.y, 0, 0));
            shader.SetFloat(WaveStepCoefficientId, coefficient);
            shader.SetFloat(WaveDampingId, Mathf.Clamp01(damping * stepDuration));
            var animatedSourceCenter = GetSourceCenter(stepTime, sourceSize);
            shader.SetVector(
                WaveSourceCenterId,
                new Vector4(animatedSourceCenter.x, animatedSourceCenter.y, 0, 0));
            shader.SetFloat(WaveSourceRadiusId, sourceRadius);
            shader.SetFloat(
                WaveSourceHeightId,
                sourceAmplitude * Mathf.Sin(2 * Mathf.PI * sourceFrequency * stepTime));
            shader.Dispatch(
                waveKernel,
                DivideRoundUp(width, ThreadGroupSize),
                DivideRoundUp(height, ThreadGroupSize),
                1);

            var releasedHeight = previousHeight;
            previousHeight = currentHeight;
            currentHeight = nextHeight;
            nextHeight = releasedHeight;
        }

        Vector2 GetSourceCenter(float time, Vector2 sourceSize)
        {
            var result = sourceCenter;
            if (moveSourceWithNoise)
            {
                var noiseTime = time * sourceMovementSpeed;
                var seed = sourceNoiseSeed * 0.12347f;
                var noiseX = Mathf.PerlinNoise(seed + 11.17f, noiseTime + 37.31f) * 2 - 1;
                var noiseZ = Mathf.PerlinNoise(seed + 71.43f, noiseTime + 19.73f) * 2 - 1;
                result += new Vector2(noiseX, noiseZ) * sourceMovementRange;
            }

            var sourceLimit = new Vector2(
                Mathf.Max(0, sourceSize.x * 0.5f - sourceRadius),
                Mathf.Max(0, sourceSize.y * 0.5f - sourceRadius));
            result.x = Mathf.Clamp(result.x, -sourceLimit.x, sourceLimit.x);
            result.y = Mathf.Clamp(result.y, -sourceLimit.y, sourceLimit.y);
            return result;
        }

        void ClearHeightTexture(ComputeShader shader, RenderTexture texture, int width, int height)
        {
            shader.SetTexture(clearKernel, WaveNextId, texture);
            shader.SetInts(HeightMapSizeId, width, height);
            shader.Dispatch(
                clearKernel,
                DivideRoundUp(width, ThreadGroupSize),
                DivideRoundUp(height, ThreadGroupSize),
                1);
        }

        RenderTexture CreateHeightTexture(int width, int height, string suffix)
        {
            var texture = new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.RFloat,
                RenderTextureReadWrite.Linear)
            {
                name = $"{name} {suffix} Height",
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.Create();
            return texture;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ReleaseSimulationResources();
        }

        void ReleaseSimulationResources()
        {
            ReleaseTexture(ref previousHeight);
            ReleaseTexture(ref currentHeight);
            ReleaseTexture(ref nextHeight);
            simulationShader = null;
            clearKernel = -1;
            waveKernel = -1;
            textureWidth = 0;
            textureHeight = 0;
            accumulatedTime = 0;
        }

        static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            texture.Release();
            if (Application.isPlaying)
                Destroy(texture);
            else
                DestroyImmediate(texture);
            texture = null;
        }
    }
}
