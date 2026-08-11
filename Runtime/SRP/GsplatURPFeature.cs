// Originated from the GaussianSplatHDRPPass in aras-p/UnityGaussianSplatting by Aras Pranckevičius
// https://github.com/aras-p/UnityGaussianSplatting/blob/main/package/Runtime/GaussianSplatHDRPPass.cs
// Copyright (c) 2023 Aras Pranckevičius
// Modified by Yize Wu
// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

#if GSPLAT_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Gsplat
{
    class GsplatURPFeature : ScriptableRendererFeature
    {
        class GsplatRenderPass : ScriptableRenderPass
        {
#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(GsplatSorter.k_passName, out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    GsplatSorter.Instance.DispatchSort(commandBuffer, data.CameraData.camera);
                });
            }
#else
            public CommandBuffer CommandBuffer;
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                GsplatSorter.Instance.DispatchSort(CommandBuffer, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(CommandBuffer);
            }
#endif
        }

#if UNITY_6000_0_OR_NEWER
        /// <summary>
        /// Draws the splats into a target of their own and resolves it into the camera colour.
        ///
        /// The splats cannot blend straight into the camera target: the over-chain has to complete
        /// in gamma space (see GsplatToTargetSpace in Gsplat.hlsl), and a shared linear target gives
        /// nowhere to put the single conversion that belongs at the end of the chain.
        /// </summary>
        class GsplatDrawPass : ScriptableRenderPass
        {
            // Float16 rather than the camera's 8-bit format: the composite divides out the
            // premultiplied alpha, scaling quantisation error by 1/alpha. At 8 bits a one-step
            // alpha would amplify a one-step colour error to full scale, speckling every faint
            // splat edge. Bandwidth for this is what dropping the target below camera resolution
            // is meant to buy back.
            const GraphicsFormat k_offscreenFormat = GraphicsFormat.R16G16B16A16_SFloat;

            // Below this the composite's bilinear upsample stops hiding the loss and splat edges
            // visibly step. Matches the range on GsplatSettings.OffscreenScale.
            const float k_minOffscreenScale = 0.25f;

            const string k_offscreenPassName = "Gsplat.Offscreen";
            const string k_compositePassName = "Gsplat.Composite";
            const string k_offscreenTextureName = "_GsplatOffscreen";

            static readonly int k_gsplatOffscreen = Shader.PropertyToID("_GsplatOffscreen");
            static readonly int k_gsplatDepthUvScale = Shader.PropertyToID("_GsplatDepthUvScale");
            static readonly int k_screenParams = Shader.PropertyToID("_ScreenParams");

            public Material CompositeMaterial;

            class DrawPassData
            {
                public TextureHandle Target;
                public Vector4 TargetScreenParams;
                public Vector4 CameraScreenParams;
            }

            class CompositePassData
            {
                public TextureHandle Source;
                public TextureHandle Target;
                public Material Material;
            }

            public GsplatDrawPass()
            {
                // The offscreen target is allocated without MSAA, so it cannot take the camera's
                // depth as an attachment; the splat shaders test occlusion against the depth
                // texture instead. Asking for it here makes URP produce one for this frame
                // regardless of the pipeline asset's own depth-texture setting.
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            static Vector4 ScreenParams(int width, int height) =>
                new(width, height, 1f + 1f / width, 1f + 1f / height);

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!CompositeMaterial)
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                var desc = cameraData.cameraTargetDescriptor;
                var cameraWidth = desc.width;
                var cameraHeight = desc.height;

                desc.graphicsFormat = k_offscreenFormat;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;
                desc.bindMS = false;

                var scale = Mathf.Clamp(GsplatSettings.Instance.OffscreenScale, k_minOffscreenScale, 1f);
                desc.width = Mathf.Max(1, Mathf.RoundToInt(cameraWidth * scale));
                desc.height = Mathf.Max(1, Mathf.RoundToInt(cameraHeight * scale));

                var offscreen = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc,
                    k_offscreenTextureName, false, FilterMode.Bilinear, TextureWrapMode.Clamp);

                using (var builder =
                       renderGraph.AddUnsafePass<DrawPassData>(k_offscreenPassName, out var passData))
                {
                    passData.Target = offscreen;
                    passData.TargetScreenParams = ScreenParams(desc.width, desc.height);
                    passData.CameraScreenParams = ScreenParams(cameraWidth, cameraHeight);
                    builder.UseTexture(offscreen, AccessFlags.Write);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    // The draws are recorded through GsplatSorter, so the graph cannot see that
                    // this pass produces anything until the composite reads the target.
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (DrawPassData data, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        CoreUtils.SetRenderTarget(cmd, data.Target, ClearFlag.Color, Color.clear);
                        cmd.SetGlobalInteger(k_gsplatOffscreen, 1);
                        // The splat shaders size themselves in target pixels: InitCorner derives the
                        // projected footprint and its below-2-pixel cull from _ScreenParams, and the
                        // occlusion test normalises SV_Position by it. Left at the camera's value the
                        // splats would be laid out for a resolution this target does not have.
                        cmd.SetGlobalVector(k_screenParams, data.TargetScreenParams);
                        // _CameraDepthTexture is an RTHandle and may be larger than the region in
                        // use, so normalised coordinates need scaling into the used part.
                        cmd.SetGlobalVector(k_gsplatDepthUvScale, RTHandles.rtHandleProperties.rtHandleScale);
                        GsplatSorter.Instance.RecordDraws(cmd);
                        // Restore, so anything else drawing these materials this frame (BiRP-style
                        // immediate submissions, editor preview cameras) keeps the old behaviour.
                        cmd.SetGlobalInteger(k_gsplatOffscreen, 0);
                        cmd.SetGlobalVector(k_screenParams, data.CameraScreenParams);
                    });
                }

                using (var builder =
                       renderGraph.AddUnsafePass<CompositePassData>(k_compositePassName, out var passData))
                {
                    passData.Source = offscreen;
                    passData.Target = resourceData.activeColorTexture;
                    passData.Material = CompositeMaterial;
                    builder.UseTexture(offscreen, AccessFlags.Read);
                    builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (CompositePassData data, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        CoreUtils.SetRenderTarget(cmd, data.Target);
                        Blitter.BlitTexture(cmd, data.Source, new Vector4(1, 1, 0, 0), data.Material, 0);
                    });
                }
            }
        }
#endif

        GsplatRenderPass m_pass;
#if UNITY_6000_0_OR_NEWER
        GsplatDrawPass m_drawPass;
        Material m_compositeMaterial;
#endif
        bool m_hasGsplats;

        public override void Create()
        {
            m_pass = new GsplatRenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
#if UNITY_6000_0_OR_NEWER
            m_drawPass = new GsplatDrawPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
#endif
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_hasGsplats = GsplatSorter.Instance.GatherGsplatsForCamera(cameraData.camera);
#if !UNITY_6000_0_OR_NEWER
            m_pass.CommandBuffer ??= new CommandBuffer { name = "SortGsplats" };
            m_pass.CommandBuffer.Clear();
#endif
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!GsplatSorter.Instance.Valid || !GsplatSettings.Instance.Valid || !m_hasGsplats)
                return;

            renderer.EnqueuePass(m_pass);
#if UNITY_6000_0_OR_NEWER
            var shader = GsplatSettings.Instance.CompositeShader;
            if (!shader)
            {
                Debug.LogError(
                    "[GsplatURPFeature] GsplatSettings.CompositeShader is unset — assign Runtime/Shaders/GsplatComposite.shader. Splats will not be drawn.");
                return;
            }

            if (!m_compositeMaterial || m_compositeMaterial.shader != shader)
            {
                CoreUtils.Destroy(m_compositeMaterial);
                m_compositeMaterial = CoreUtils.CreateEngineMaterial(shader);
            }

            m_drawPass.CompositeMaterial = m_compositeMaterial;
            renderer.EnqueuePass(m_drawPass);
#endif
        }

        protected override void Dispose(bool disposing)
        {
#if !UNITY_6000_0_OR_NEWER
            m_pass.CommandBuffer?.Dispose();
            m_pass.CommandBuffer = null;
#endif
#if UNITY_6000_0_OR_NEWER
            CoreUtils.Destroy(m_compositeMaterial);
            m_compositeMaterial = null;
            m_drawPass = null;
#endif
            m_pass = null;
        }
    }
}

#endif
