using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CausticMeshDxr
{
    public sealed class CausticRendererFeature : ScriptableRendererFeature
    {
        sealed class CausticRenderPass : ScriptableRenderPass
        {
            static readonly int BackgroundTextureId = Shader.PropertyToID("_CausticBackgroundTexture");

            sealed class DrawPassData
            {
                public TextureHandle color;
                public TextureHandle depth;
                public Camera camera;
                public bool drawSourceSurface;
            }

            sealed class CopyPassData
            {
                public TextureHandle source;
                public TextureHandle destination;
            }

            public CausticRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var camera = cameraData.camera;
                if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var color = resourceData.activeColorTexture;
                var depth = resourceData.activeDepthTexture;
                if (!color.IsValid() || !depth.IsValid())
                    return;

                AddDrawPass(renderGraph, "Draw Caustics", color, depth, camera, false);

                var backgroundDescriptor = renderGraph.GetTextureDesc(color);
                backgroundDescriptor.name = "Caustics Background Texture";
                backgroundDescriptor.clearBuffer = false;
                backgroundDescriptor.msaaSamples = MSAASamples.None;
                var background = renderGraph.CreateTexture(backgroundDescriptor);
                AddCopyPass(renderGraph, color, background);
                AddDrawPass(renderGraph, "Draw Caustic Source Surface", color, depth, camera, true, background);
            }

            static void AddDrawPass(
                RenderGraph renderGraph,
                string passName,
                TextureHandle color,
                TextureHandle depth,
                Camera camera,
                bool drawSourceSurface,
                TextureHandle background = default)
            {
                using var builder = renderGraph.AddUnsafePass<DrawPassData>(passName, out var passData);
                passData.color = color;
                passData.depth = depth;
                passData.camera = camera;
                passData.drawSourceSurface = drawSourceSurface;
                builder.UseTexture(color, AccessFlags.ReadWrite);
                builder.UseTexture(depth, AccessFlags.Read);
                if (background.IsValid())
                    builder.UseTexture(background, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (DrawPassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.color, data.depth);
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    if (data.drawSourceSurface)
                        CausticRayQueryTest.DrawSourceSurfaces(commandBuffer, data.camera);
                    else
                        CausticRayQueryTest.DrawProjectedCaustics(commandBuffer, data.camera);
                });
            }

            static void AddCopyPass(RenderGraph renderGraph, TextureHandle source, TextureHandle destination)
            {
                using var builder = renderGraph.AddUnsafePass<CopyPassData>(
                    "Copy Caustics Background",
                    out var passData);
                passData.source = source;
                passData.destination = destination;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, AccessFlags.WriteAll);
                builder.SetGlobalTextureAfterPass(destination, BackgroundTextureId);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CopyPassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.destination);
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    Blitter.BlitTexture(commandBuffer, data.source, new Vector4(1, 1, 0, 0), 0, false);
                });
            }
        }

        CausticRenderPass renderPass;

        public override void Create()
        {
            renderPass = new CausticRenderPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(renderPass);
        }
    }
}
