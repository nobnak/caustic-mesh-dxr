using UnityEngine;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    public sealed class TextureHeightField : CausticHeightField
    {
        const int ThreadGroupSize = 8;
        const string HeightMapKernelName = "BuildTextureHeightMap";
        static readonly int HeightMapOutputId = Shader.PropertyToID("_HeightMapOutput");
        static readonly int HeightMapSizeId = Shader.PropertyToID("_HeightMapSize");
        static readonly int HeightMapSourceSizeId = Shader.PropertyToID("_HeightMapSourceSize");
        static readonly int HeightTextureId = Shader.PropertyToID("_HeightTexture");
        static readonly int TextureFieldSizeId = Shader.PropertyToID("_TextureFieldSize");
        static readonly int TextureFieldCenterId = Shader.PropertyToID("_TextureFieldCenter");
        static readonly int TextureHeightScaleId = Shader.PropertyToID("_TextureHeightScale");
        static readonly int TextureHeightOffsetId = Shader.PropertyToID("_TextureHeightOffset");

        [SerializeField] Texture2D heightTexture;
        [SerializeField] Vector2 fieldSize = new(2, 2);
        [SerializeField] Vector2 center;
        [SerializeField] float heightScale = 0.1f;
        [SerializeField] float heightOffset;

        ComputeShader heightMapShader;
        int heightMapKernel = -1;

        public override bool IsTimeVarying => false;
        public override float MaximumAbsoluteHeight => Mathf.Abs(heightOffset) + Mathf.Abs(heightScale);

        public override int StateHash
        {
            get
            {
                unchecked
                {
                    var hash = heightTexture != null ? heightTexture.GetInstanceID() : 0;
                    hash = hash * 397 ^ fieldSize.GetHashCode();
                    hash = hash * 397 ^ center.GetHashCode();
                    hash = hash * 397 ^ heightScale.GetHashCode();
                    return hash * 397 ^ heightOffset.GetHashCode();
                }
            }
        }

        public override bool TryValidate(out string error)
        {
            if (heightTexture == null)
            {
                error = "Texture Height Field requires a Height Texture.";
                return false;
            }
            error = null;
            return true;
        }

        public override bool TryValidateCpu(out string error)
        {
            if (!TryValidate(out error))
                return false;
            if (!heightTexture.isReadable)
            {
                error = $"Height Texture '{heightTexture.name}' must have Read/Write enabled for CPU validation.";
                return false;
            }
            return true;
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
            if (heightTexture == null)
            {
                outputHeightMap = null;
                return false;
            }

            var heightMap = EnsureGpuHeightMap(width, height);
            if (heightMapShader != shader || heightMapKernel < 0)
            {
                heightMapShader = shader;
                heightMapKernel = shader.FindKernel(HeightMapKernelName);
            }

            shader.SetTexture(heightMapKernel, HeightMapOutputId, heightMap);
            shader.SetTexture(heightMapKernel, HeightTextureId, heightTexture);
            shader.SetInts(HeightMapSizeId, width, height);
            shader.SetVector(
                HeightMapSourceSizeId,
                new Vector4(sourceSize.x, sourceSize.y, 0, 0));
            shader.SetVector(
                TextureFieldSizeId,
                new Vector4(fieldSize.x, fieldSize.y, 0, 0));
            shader.SetVector(
                TextureFieldCenterId,
                new Vector4(center.x, center.y, 0, 0));
            shader.SetFloat(TextureHeightScaleId, heightScale);
            shader.SetFloat(TextureHeightOffsetId, heightOffset);
            shader.Dispatch(
                heightMapKernel,
                DivideRoundUp(width, ThreadGroupSize),
                DivideRoundUp(height, ThreadGroupSize),
                1);
            outputHeightMap = heightMap;
            return true;
        }

        public override void Evaluate(
            Vector2 localPosition,
            float time,
            out float height,
            out Vector2 gradient)
        {
            if (heightTexture == null || !heightTexture.isReadable)
            {
                height = 0;
                gradient = Vector2.zero;
                return;
            }

            var safeSize = new Vector2(
                Mathf.Max(0.0001f, Mathf.Abs(fieldSize.x)),
                Mathf.Max(0.0001f, Mathf.Abs(fieldSize.y)));
            var offset = localPosition - center;
            var uv = new Vector2(offset.x / safeSize.x, offset.y / safeSize.y) + Vector2.one * 0.5f;
            var texel = new Vector2(1f / heightTexture.width, 1f / heightTexture.height);
            var centerSample = heightTexture.GetPixelBilinear(uv.x, uv.y).r;
            var left = heightTexture.GetPixelBilinear(uv.x - texel.x, uv.y).r;
            var right = heightTexture.GetPixelBilinear(uv.x + texel.x, uv.y).r;
            var bottom = heightTexture.GetPixelBilinear(uv.x, uv.y - texel.y).r;
            var top = heightTexture.GetPixelBilinear(uv.x, uv.y + texel.y).r;
            height = heightOffset + heightScale * centerSample;
            gradient = new Vector2(
                heightScale * (right - left) / (2 * texel.x * safeSize.x),
                heightScale * (top - bottom) / (2 * texel.y * safeSize.y));
        }

        void OnValidate()
        {
            fieldSize.x = Mathf.Max(0.0001f, Mathf.Abs(fieldSize.x));
            fieldSize.y = Mathf.Max(0.0001f, Mathf.Abs(fieldSize.y));
        }
    }
}
