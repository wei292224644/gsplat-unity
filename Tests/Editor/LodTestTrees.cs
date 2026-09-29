// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.Mathematics;

namespace Gsplat.Tests
{
    static class LodTestTrees
    {
        /// <summary>
        /// A complete tree in breadth-first order (children contiguous and after their parent, as
        /// in a .gsd): the root has size 8 at the origin, every level splits each node into
        /// <paramref name="branching"/> smaller ones spread along X. That is the shape a LoD tree
        /// has — deeper nodes are smaller and together cover their parent.
        /// </summary>
        public static GsplatLodTree Complete(int branching, int depth, out int[] parent)
        {
            var centers = new List<float3> { float3.zero };
            var sizes = new List<float> { 8f };
            var parents = new List<int> { -1 };
            int levelStart = 0, levelCount = 1;
            for (var level = 1; level <= depth; ++level)
            {
                var nextStart = centers.Count;
                for (var p = levelStart; p < levelStart + levelCount; ++p)
                for (var c = 0; c < branching; ++c)
                {
                    var offset = (c - (branching - 1) * 0.5f) * sizes[p] / branching;
                    centers.Add(centers[p] + new float3(offset, 0f, 0f));
                    sizes.Add(sizes[p] / branching);
                    parents.Add(p);
                }

                levelStart = nextStart;
                levelCount *= branching;
            }

            var n = centers.Count;
            var childStart = new uint[n];
            var childCount = new ushort[n];
            for (var i = 1; i < n; ++i)
            {
                var p = parents[i];
                if (childCount[p] == 0)
                    childStart[p] = (uint)i;
                childCount[p]++;
            }

            var nodes = new uint4[n * 2];
            for (var i = 0; i < n; ++i)
                (nodes[i * 2], nodes[i * 2 + 1]) = PackNode(centers[i], sizes[i]);
            parent = parents.ToArray();
            return new GsplatLodTree(nodes, childStart, childCount);
        }

        /// <summary>A node whose derived Size is <paramref name="size"/>: scale = size / 2 on every axis, opacity 0.5.</summary>
        public static (uint4, uint4) PackNode(float3 center, float size)
        {
            var lnScale = math.f32tof16(math.log(size * 0.5f));
            var w0 = new uint4(math.asuint(center), math.f32tof16(0.5f));
            var w1 = new uint4(0u, lnScale << 16, lnScale | (lnScale << 16), 0u);
            return (w0, w1);
        }

        /// <summary>Looking down +Z with Spark's default foveation.</summary>
        public static GsplatLodView View(float3 origin, int budget, float pixelScaleLimit = 0.001f) =>
            GsplatLodView.Create(origin, new float3(0f, 0f, 1f), pixelScaleLimit, budget, 90f, 120f, 0.4f, 0.2f);
    }
}
