// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.XR;

namespace Gsplat
{
    /// <summary>
    /// Binds one renderer's <see cref="GsplatLodAsset"/> to its order buffer (spec §6–§7): builds the
    /// traversal table, runs the selection, uploads each published cut. For LoD assets the order
    /// buffer is written here and nowhere else (D14).
    /// </summary>
    public sealed class GsplatLodDriver : IDisposable
    {
        /// <summary>Live drivers across all renderers. The budget is frame-level, so at most one may exist (D12).</summary>
        public static int LiveCount { get; private set; }

        readonly GsplatLodTree m_tree;
        readonly GsplatLodSelector m_selector;
        bool m_primed;
        bool m_disposed;

        public int Budget => m_selector.Budget;
        public int LastLatencyFrames => m_selector.LastLatencyFrames;

        public GsplatLodDriver(GsplatLodAsset asset, int budget)
        {
            m_tree = new GsplatLodTree(asset.Nodes, asset.ChildStart, asset.ChildCount);
            m_selector = new GsplatLodSelector(m_tree, budget);
            ++LiveCount;
        }

        /// <summary>
        /// Advances the selection one frame. Returns the size of a cut uploaded into
        /// <paramref name="sorter"/>'s order buffer this frame, or -1 when nothing new was published.
        /// </summary>
        public int Update(Camera camera, Transform model, ISorterResource sorter, int frame)
        {
            var view = BuildView(camera, model, Budget);
            if (!m_primed)
            {
                m_primed = true;
                return Upload(m_selector.RunNow(view), sorter);
            }

            var published = m_selector.TryComplete(frame, out var indices) ? Upload(indices, sorter) : -1;
            m_selector.TrySchedule(view, frame);
            return published;
        }

        public double MeasureTraversalMilliseconds(Camera camera, Transform model) =>
            m_selector.MeasureMilliseconds(BuildView(camera, model, Budget));

        static int Upload(NativeArray<uint> indices, ISorterResource sorter)
        {
            sorter.OrderBuffer.SetData(indices, 0, 0, indices.Length);
            sorter.Initialized = true;
            return indices.Length;
        }

        /// <summary>The view in model space, so the selection is unaffected by where the model sits.</summary>
        public static GsplatLodView BuildView(Camera camera, Transform model, int budget)
        {
            var settings = GsplatSettings.Instance;
            var worldToModel = model.worldToLocalMatrix;
            var origin = (float3)worldToModel.MultiplyPoint3x4(camera.transform.position);
            var forward = (float3)worldToModel.MultiplyVector(camera.transform.forward);
            // Splats land in the offscreen target (D16): a pixel is an offscreen pixel, not a camera one.
            float height = XRSettings.enabled && XRSettings.eyeTextureHeight > 0
                ? XRSettings.eyeTextureHeight * XRSettings.renderViewportScale
                : camera.pixelHeight;
            if (GsplatSorter.DeferDraws)
                height *= settings.EffectiveOffscreenScale;
            var limit = 2f * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) / Mathf.Max(1f, height);
            return GsplatLodView.Create(origin, forward, limit, budget,
                settings.LodConeFov0, settings.LodConeFov, settings.LodConeFoveate, settings.LodBehindFoveate);
        }

        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            m_selector.Dispose();
            m_tree.Dispose();
            --LiveCount;
        }
    }
}
