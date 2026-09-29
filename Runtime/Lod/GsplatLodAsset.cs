// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>
    /// A LoD tree loaded from a <c>.gsd</c> file (spec §4–§7). <see cref="GsplatAsset.SplatCount"/>
    /// counts every node, leaves and merged interiors alike, because every node lives on the GPU;
    /// which of them are drawn each frame is decided by <see cref="GsplatLodDriver"/>.
    /// </summary>
    public class GsplatLodAsset : GsplatAsset
    {
        public override CompressionMode Compression => CompressionMode.Lod;

        public uint LeafCount;
        [HideInInspector] public uint4[] Nodes; // 2 per node, ExtSplat layout
        [HideInInspector] public uint[] PackedSH1; // 2 words per node
        [HideInInspector] public uint[] PackedSH2; // 4 words per node
        [HideInInspector] public uint[] PackedSH3; // 4 words per node
        [HideInInspector] public uint[] ChildStart;
        [HideInInspector] public ushort[] ChildCount;

        static readonly int k_lodNodesBuffer = Shader.PropertyToID("_LodNodesBuffer");
        static readonly int k_packedSH1Buffer = Shader.PropertyToID("_PackedSH1Buffer");
        static readonly int k_packedSH2Buffer = Shader.PropertyToID("_PackedSH2Buffer");
        static readonly int k_packedSH3Buffer = Shader.PropertyToID("_PackedSH3Buffer");
        static readonly int k_splatCount = Shader.PropertyToID("_SplatCount");
        static readonly int k_matrixMv = Shader.PropertyToID("_MatrixMV");
        static readonly int k_depthBuffer = Shader.PropertyToID("_DepthBuffer");
        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");

        /// <summary>Fills this asset from .gsd bytes; throws <see cref="GsdFormatException"/> on malformed input.</summary>
        public void LoadFromGsd(ReadOnlySpan<byte> bytes)
        {
            var data = GsdReader.Read(bytes);
            SplatCount = data.NodeCount;
            PrunedSplatCount = 0;
            LeafCount = data.LeafCount;
            SHBands = data.SHDegree;
            var bounds = new Bounds();
            bounds.SetMinMax(data.BoundsMin, data.BoundsMax);
            Bounds = bounds;
            Nodes = data.Nodes;
            PackedSH1 = data.SH1;
            PackedSH2 = data.SH2;
            PackedSH3 = data.SH3;
            ChildStart = data.ChildStart;
            ChildCount = data.ChildCount;
        }

        public override void Allocate()
        {
            Nodes = new uint4[SplatCount * 2];
            PackedSH1 = SHBands >= 1 ? new uint[SplatCount * 2] : Array.Empty<uint>();
            PackedSH2 = SHBands >= 2 ? new uint[SplatCount * 4] : Array.Empty<uint>();
            PackedSH3 = SHBands >= 3 ? new uint[SplatCount * 4] : Array.Empty<uint>();
            ChildStart = new uint[SplatCount];
            ChildCount = new ushort[SplatCount];
        }

        public override void LoadFromPly(string plyPath, ProgressCallback progressCallback = null,
            SourceCoordinates sourceCoordinates = SourceCoordinates.RUF, float opacityPruneThreshold = 0f) =>
            throw new NotSupportedException("GsplatLodAsset is built by gsd-build; import the .gsd file, not the .ply.");

        public override GsplatResource CreateResource() => new GsplatResourceLod(SplatCount, SHBands);

        protected override void _UploadData(GsplatResource resource)
        {
            var res = (GsplatResourceLod)resource;
            res.NodesBuffer.SetData(Nodes);
            if (SHBands >= 1)
                res.PackedSH1Buffer.SetData(PackedSH1);
            if (SHBands >= 2)
                res.PackedSH2Buffer.SetData(PackedSH2);
            if (SHBands >= 3)
                res.PackedSH3Buffer.SetData(PackedSH3);
        }

        // The selection addresses nodes by index anywhere in the tree, so a half-uploaded tree is
        // unusable: upload in one piece and report the full count only then.
        protected override async Task _UploadDataAsync(GsplatResource resource)
        {
            await Task.Yield();
            _UploadData(resource);
            resource.UploadedCount = SplatCount;
        }

        public override void SetupMaterialPropertyBlock(MaterialPropertyBlock propertyBlock, GsplatResource resource)
        {
            var res = (GsplatResourceLod)resource;
            propertyBlock.SetBuffer(k_lodNodesBuffer, res.NodesBuffer);
            if (SHBands >= 1)
                propertyBlock.SetBuffer(k_packedSH1Buffer, res.PackedSH1Buffer);
            if (SHBands >= 2)
                propertyBlock.SetBuffer(k_packedSH2Buffer, res.PackedSH2Buffer);
            if (SHBands >= 3)
                propertyBlock.SetBuffer(k_packedSH3Buffer, res.PackedSH3Buffer);
        }

        public override void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv, ISorterResource sorterResource,
            GsplatResource resource, uint activeCount)
        {
            // Only the selected cut is sorted: dispatch by the cut size, not the node count (spec §7).
            if (activeCount == 0)
                return;
            var res = (GsplatResourceLod)resource;
            var cs = GsplatMaterial.CalcDepthShader;
            const int kernelCalcDepthLod = 0;
            cmd.SetComputeIntParam(cs, k_splatCount, (int)activeCount);
            cmd.SetComputeMatrixParam(cs, k_matrixMv, matrixMv);
            cmd.SetComputeBufferParam(cs, kernelCalcDepthLod, k_lodNodesBuffer, res.NodesBuffer);
            cmd.SetComputeBufferParam(cs, kernelCalcDepthLod, k_depthBuffer, sorterResource.InputKeys);
            cmd.SetComputeBufferParam(cs, kernelCalcDepthLod, k_orderBuffer, sorterResource.OrderBuffer);
            cmd.DispatchCompute(cs, kernelCalcDepthLod, (int)GsplatUtils.DivRoundUp(activeCount, 1024), 1, 1);
        }

        public override void InitOrder(ISorterResource sorterResource, GsplatResource resource, bool updateBounds) =>
            throw new InvalidOperationException(
                "GsplatLodAsset: the LoD selection owns the order buffer; InitOrder must never run for it (spec D14).");
    }
}
