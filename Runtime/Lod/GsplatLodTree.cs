// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Gsplat
{
    /// <summary>
    /// The CPU-side traversal table (spec §6): centre and feature size per node plus the child
    /// ranges. Centre and size are derived from the node payload at bind time, never stored in the
    /// file, so they cannot disagree with what the GPU draws (D4).
    /// </summary>
    public sealed class GsplatLodTree : IDisposable
    {
        public NativeArray<float3> Center;
        public NativeArray<float> Size;
        public NativeArray<uint> ChildStart;
        public NativeArray<ushort> ChildCount;

        public int NodeCount => Center.Length;

        public GsplatLodTree(uint4[] nodes, uint[] childStart, ushort[] childCount)
        {
            var n = childStart.Length;
            if (childCount.Length != n || nodes.Length != n * 2)
                throw new ArgumentException("nodes must hold two uint4 per node, and childStart/childCount one entry per node");

            ChildStart = new NativeArray<uint>(childStart, Allocator.Persistent);
            ChildCount = new NativeArray<ushort>(childCount, Allocator.Persistent);
            Center = new NativeArray<float3>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Size = new NativeArray<float>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // ponytail: copies the payload once more at bind time (~2× node bytes, transient); pin the
            // managed array instead if that spike ever matters on device.
            using var payload = new NativeArray<uint4>(nodes, Allocator.TempJob);
            new DeriveJob { Nodes = payload, Center = Center, Size = Size }.Schedule(n, 4096).Complete();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct DeriveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<uint4> Nodes;
            [WriteOnly] public NativeArray<float3> Center;
            [WriteOnly] public NativeArray<float> Size;

            public void Execute(int i)
            {
                var w0 = Nodes[i * 2];
                var w1 = Nodes[i * 2 + 1];
                Center[i] = math.asfloat(w0.xyz);
                var alphaOrD = math.f16tof32(w0.w & 0xFFFFu);
                var lnScale = new float3(math.f16tof32(w1.y >> 16), math.f16tof32(w1.z & 0xFFFFu), math.f16tof32(w1.z >> 16));
                // Spark's feature_size: 2 · max(scale) · max(1, D).
                Size[i] = 2f * math.cmax(math.exp(lnScale)) * math.max(1f, alphaOrD);
            }
        }

        public void Dispose()
        {
            if (Center.IsCreated) Center.Dispose();
            if (Size.IsCreated) Size.Dispose();
            if (ChildStart.IsCreated) ChildStart.Dispose();
            if (ChildCount.IsCreated) ChildCount.Dispose();
        }
    }
}
