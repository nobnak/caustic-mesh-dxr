using UnityEngine;

namespace CausticMeshDxr
{
    public abstract class CausticHeightField : MonoBehaviour
    {
        RenderTexture gpuHeightMap;

        public abstract bool IsTimeVarying { get; }
        public abstract float MaximumAbsoluteHeight { get; }
        public abstract int StateHash { get; }

        public abstract void Evaluate(
            Vector2 localPosition,
            float time,
            out float height,
            out Vector2 gradient);

        internal virtual bool TryDispatchGpuHeightMap(
            ComputeShader shader,
            int width,
            int height,
            Vector2 sourceSize,
            float time,
            float deltaTime,
            out Texture heightMap)
        {
            heightMap = null;
            return false;
        }

        public virtual bool TryValidate(out string error)
        {
            error = null;
            return true;
        }

        public virtual bool TryValidateCpu(out string error)
        {
            return TryValidate(out error);
        }

        protected RenderTexture EnsureGpuHeightMap(int width, int height)
        {
            if (gpuHeightMap != null
                && gpuHeightMap.width == width
                && gpuHeightMap.height == height
                && gpuHeightMap.IsCreated())
                return gpuHeightMap;

            ReleaseGpuHeightMap();
            gpuHeightMap = new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.RFloat,
                RenderTextureReadWrite.Linear)
            {
                name = $"{name} Height Map",
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            gpuHeightMap.Create();
            return gpuHeightMap;
        }

        protected static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        protected virtual void OnDisable()
        {
            ReleaseGpuHeightMap();
        }

        void ReleaseGpuHeightMap()
        {
            if (gpuHeightMap == null)
                return;
            gpuHeightMap.Release();
            if (Application.isPlaying)
                Destroy(gpuHeightMap);
            else
                DestroyImmediate(gpuHeightMap);
            gpuHeightMap = null;
        }
    }
}
