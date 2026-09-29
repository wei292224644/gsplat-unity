// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    [ExecuteAlways]
    public class GsplatRenderer : MonoBehaviour, IGsplat
    {
        public enum GsplatSortMode
        {
            Always,
            SortEveryNFrames,
            CutoutsEveryNSorts,
        }

        public GsplatAsset GsplatAsset;

        // Range is enforced by GsplatRendererEditor based on the bound asset's SHBands.
        public int SHDegree = 3;
        [HideInInspector] public uint RenderOrder = 0;
        public float Brightness = 1.0f;

        [Tooltip(
            "Improves rendering speed by shrinking Gaussian splats while trying to keep the impact on visual quality as small as possible.")]
        [Range(0, 1)]
        public float SplatDownscaleFactor = 0.0f;

        public bool GammaToLinear;
        public bool AsyncUpload;
        public bool RenderBeforeUploadComplete = true;

        [Tooltip("Does cutouts update the Gsplat world bounds? (Costly on moving cutouts)")]
        public bool CutoutsUpdateBounds = true;

        GsplatAsset m_prevAsset;
        GsplatRendererImpl m_renderer;
        bool m_warnedLodCutouts;
        bool m_warnedNoCamera;

        // LoD assets draw at most the budget, so their order buffer is sized to it (spec §7).
        uint OrderCapacity => GsplatAsset is GsplatLodAsset
            ? GsplatSettings.Instance.LodSplatBudget
            : GsplatAsset.SplatCount;

        public bool IsLod => m_renderer?.IsLod ?? false;
        public int LodLatencyFrames => m_renderer?.LodLatencyFrames ?? 0;

        /// <summary>One synchronous traversal from Camera.main, timed. For the bench.</summary>
        public double MeasureLodTraversalMs()
        {
            var camera = Camera.main;
            return m_renderer != null && camera ? m_renderer.MeasureLodTraversalMs(camera, transform) : 0;
        }

        public bool Valid => GsplatAsset &&
                             (RenderBeforeUploadComplete ? SplatCount > 0 : SplatCount == GsplatAsset.SplatCount);

        public uint SplatCount => m_renderer != null ? m_renderer.GsplatResource?.UploadedCount ?? 0 : 0;

        public ISorterResource SorterResource => m_renderer.SorterResource;

        // IGsplat global-merge members: expose per-renderer GPU buffers for the global sorter.
        public GsplatResource GsplatResource => m_renderer?.GsplatResource;
        public byte SHBands => GsplatAsset?.SHBands ?? 0;


        public uint RemainingCount
        {
            get => m_renderer.m_remainingCount;
            set => m_renderer.m_remainingCount = value;
        }

        public Bounds Bounds
        {
            get => m_renderer.m_bounds;
            set => m_renderer.m_bounds = value;
        }

        public GsplatCutout[] Cutouts
        {
            get
            {
                var cutouts = GsplatCutout.m_RegisteredCutouts
                    .Where(component => component.enabled)
                    .Where(component =>
                        component.m_Target == GsplatCutout.Target.All ||
                        (component.m_Target == GsplatCutout.Target.Parent && component.transform.parent == transform) ||
                        (component.m_Target == GsplatCutout.Target.Specific && component.m_SpecifcRenderer == this)
                    );
                return cutouts.ToArray();
            }
        }

        public bool ComputeSortRequired => m_renderer.ComputeSortRequired;
        public bool ComputeCutoutsRequired => m_renderer.ComputeCutoutsRequired;
        public GsplatSortMode SortMode = GsplatSortMode.Always;
        [HideInInspector] public uint SortRefreshRate = 1;
        [HideInInspector] public uint CutoutsRefreshRate = 1;

        public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv) => m_renderer.ComputeDepth(cmd, matrixMv);

        void OnEnable()
        {
            GsplatSorter.Instance.RegisterGsplat(this);
            m_prevAsset = null;
        }

        void OnDisable()
        {
            GsplatSorter.Instance.UnregisterGsplat(this);
            m_renderer?.Dispose();
            m_renderer = null;
        }

        public void ForceRefresh()
        {
            m_renderer?.ForceRefresh();
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (GsplatSettings.Instance.DisplayBoundingBoxes && Valid && isActiveAndEnabled)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(Bounds.center, Bounds.size);
            }
        }

        [SerializeField, HideInInspector] string m_assetGuid;
        public string AssetGuid => m_assetGuid;
#endif // #if UNITY_EDITOR

        void OnValidate()
        {
            ForceRefresh();
#if UNITY_EDITOR
            if (GsplatAsset &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(GsplatAsset, out var guid, out long localId))
                m_assetGuid = guid;
#endif // #if UNITY_EDITOR
        }

        public void ReloadAsset()
        {
            m_prevAsset = null;
        }

        public void Update()
        {
            if (!GsplatAsset)
                m_prevAsset = null;
            // ponytail: a budget change rebinds and re-uploads the asset; only the bench changes it at runtime.
            if (GsplatAsset && m_renderer != null && m_prevAsset == GsplatAsset && m_renderer.SplatCount != OrderCapacity)
                m_prevAsset = null;
            if (m_prevAsset != GsplatAsset)
            {
                m_renderer?.ReleaseGsplatAsset();
                m_prevAsset = GsplatAsset;
                if (GsplatAsset)
                {
                    if (m_renderer == null)
                        m_renderer = new GsplatRendererImpl(OrderCapacity);
                    else
                        m_renderer.RecreateResources(OrderCapacity);
#if UNITY_EDITOR
                    var asyncUpload = AsyncUpload && Application.isPlaying;
#else
                    var asyncUpload = AsyncUpload;
#endif
                    m_renderer.BindGsplatAsset(GsplatAsset, asyncUpload);
                    GsplatSorter.Instance.MarkGlobalBuffersDirty();
                }
            }

            if (Valid && GsplatSettings.Instance.Valid && GsplatSorter.Instance.Valid)
            {
                m_renderer.EvaluateRefreshRequired(SortMode, SortRefreshRate - 1, CutoutsRefreshRate - 1);
                if (m_renderer.IsLod)
                    UpdateLod();
                else
                    m_renderer.DispatchInitOrder(Cutouts, transform.localToWorldMatrix, CutoutsUpdateBounds);
                // When the global sorter has merged all renderers into a single draw call,
                // skip the per-renderer draw — GsplatGlobalRenderer handles rendering.
                // Under a pipeline that owns the splat render target, the draw is recorded from
                // RecordDraw during the pass instead of being submitted here.
                if (!GsplatSorter.Instance.GlobalRenderEnabled && !GsplatSorter.DeferDraws)
                {
                    PrepareDraw();
                    m_renderer.SubmitImmediate();
                }
            }
        }

        void UpdateLod()
        {
            if (!m_warnedLodCutouts && Cutouts.Length > 0)
            {
                Debug.LogError($"[Gsplat] '{name}': .gsd assets do not support cutouts (spec D13); they are ignored.", this);
                m_warnedLodCutouts = true;
            }

            var camera = Camera.main;
            if (!camera)
            {
                if (!m_warnedNoCamera)
                {
                    Debug.LogError($"[Gsplat] '{name}': LoD selection needs a camera tagged MainCamera; none found, nothing is drawn.", this);
                    m_warnedNoCamera = true;
                }

                return;
            }

            m_renderer.UpdateLod(camera, transform, Time.frameCount);
        }

        void PrepareDraw() =>
            m_renderer.PrepareDraw(transform, gameObject.layer, GammaToLinear, SHDegree, Brightness,
                1.0f - SplatDownscaleFactor, RenderOrder);

        public void RecordDraw(RasterCommandBuffer cmd)
        {
            if (m_renderer == null || !Valid || !GsplatSettings.Instance.Valid || !GsplatSorter.Instance.Valid)
                return;
            PrepareDraw();
            m_renderer.RecordDraw(cmd);
        }
    }
}