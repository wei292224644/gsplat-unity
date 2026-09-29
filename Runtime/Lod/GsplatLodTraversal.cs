// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Gsplat
{
    /// <summary>Everything one traversal depends on, in the model's space.</summary>
    public struct GsplatLodView : IEquatable<GsplatLodView>
    {
        public float3 Origin;
        public float3 Forward;
        /// <summary>Angular size of one target pixel: nodes smaller than this are never refined.</summary>
        public float PixelScaleLimit;
        public int Budget;
        public float ConeDot0;
        public float ConeDot;
        public float ConeFoveate;
        public float BehindFoveate;

        public static GsplatLodView Create(float3 origin, float3 forward, float pixelScaleLimit, int budget,
            float coneFov0Degrees, float coneFovDegrees, float coneFoveate, float behindFoveate)
        {
            if (budget < 1)
                throw new ArgumentOutOfRangeException(nameof(budget), budget, "a LoD cut needs room for at least the root");
            var coneDot0 = coneFov0Degrees > 0f ? math.cos(math.radians(0.5f * math.clamp(coneFov0Degrees, 0f, 180f))) : 1f;
            var coneDot = coneFovDegrees > 0f ? math.cos(math.radians(0.5f * math.clamp(coneFovDegrees, 0f, 180f))) : 1f;
            return new GsplatLodView
            {
                Origin = origin,
                Forward = math.normalizesafe(forward, new float3(0f, 0f, 1f)),
                PixelScaleLimit = pixelScaleLimit,
                Budget = budget,
                ConeDot0 = coneDot0,
                ConeDot = math.min(coneDot, coneDot0),
                ConeFoveate = coneFoveate,
                BehindFoveate = behindFoveate,
            };
        }

        // Exact on purpose (spec §6): any change of pose reschedules, a still view never does.
        public bool Equals(GsplatLodView other) =>
            Origin.Equals(other.Origin) && Forward.Equals(other.Forward) && PixelScaleLimit == other.PixelScaleLimit &&
            Budget == other.Budget && ConeDot0 == other.ConeDot0 && ConeDot == other.ConeDot &&
            ConeFoveate == other.ConeFoveate && BehindFoveate == other.BehindFoveate;

        public override bool Equals(object obj) => obj is GsplatLodView other && Equals(other);
        public override int GetHashCode() => Origin.GetHashCode() ^ (Forward.GetHashCode() * 397) ^ Budget;
    }

    /// <summary>Working memory for traversals of one budget; the frontier and the output never exceed it.</summary>
    public sealed class GsplatLodScratch : IDisposable
    {
        public readonly int Budget;
        public NativeArray<float> HeapKey;
        public NativeArray<uint> HeapNode;
        public NativeArray<uint> Output;
        public NativeArray<int> Count;

        public GsplatLodScratch(int budget)
        {
            if (budget < 1)
                throw new ArgumentOutOfRangeException(nameof(budget), budget, "budget must be at least 1");
            Budget = budget;
            HeapKey = new NativeArray<float>(budget, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            HeapNode = new NativeArray<uint>(budget, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Output = new NativeArray<uint>(budget, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Count = new NativeArray<int>(1, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (HeapKey.IsCreated) HeapKey.Dispose();
            if (HeapNode.IsCreated) HeapNode.Dispose();
            if (Output.IsCreated) Output.Dispose();
            if (Count.IsCreated) Count.Dispose();
        }
    }

    public static class GsplatLodTraversal
    {
        public static GsplatLodTraverseJob CreateJob(GsplatLodTree tree, in GsplatLodView view, GsplatLodScratch scratch)
        {
            if (view.Budget > scratch.Budget)
                throw new ArgumentException($"view budget {view.Budget} exceeds scratch capacity {scratch.Budget}");
            return new GsplatLodTraverseJob
            {
                Center = tree.Center,
                Size = tree.Size,
                ChildStart = tree.ChildStart,
                ChildCount = tree.ChildCount,
                View = view,
                HeapKey = scratch.HeapKey,
                HeapNode = scratch.HeapNode,
                Output = scratch.Output,
                Count = scratch.Count,
            };
        }
    }

    /// <summary>
    /// Budgeted LoD cut (spec §6): a port of Spark's <c>lod_tree::traverse_lod_trees</c> for one
    /// tree. The node largest on screen is refined first, so when the budget runs out the detail is
    /// spread evenly; nodes below a pixel are never refined. O(N log N) in the budget, independent
    /// of the tree size.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct GsplatLodTraverseJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Center;
        [ReadOnly] public NativeArray<float> Size;
        [ReadOnly] public NativeArray<uint> ChildStart;
        [ReadOnly] public NativeArray<ushort> ChildCount;
        public GsplatLodView View;
        public NativeArray<float> HeapKey;
        public NativeArray<uint> HeapNode;
        [WriteOnly] public NativeArray<uint> Output;
        [WriteOnly] public NativeArray<int> Count;

        public void Execute()
        {
            int heapSize = 0, outCount = 0, inCut = 1; // inCut = frontier + output = splats this cut draws
            Push(ref heapSize, PixelScale(0), 0u);
            while (heapSize > 0)
            {
                var node = HeapNode[0];
                if (HeapKey[0] <= View.PixelScaleLimit)
                    break;
                int children = ChildCount[(int)node];
                if (children == 0)
                {
                    Pop(ref heapSize);
                    Output[outCount++] = node;
                    continue;
                }

                var next = inCut - 1 + children;
                if (next > View.Budget)
                    break;
                Pop(ref heapSize);
                var first = ChildStart[(int)node];
                for (var c = first; c < first + (uint)children; ++c)
                {
                    var scale = PixelScale(c);
                    if (scale <= View.PixelScaleLimit)
                        Output[outCount++] = c;
                    else
                        Push(ref heapSize, scale, c);
                }

                inCut = next;
            }

            for (var i = 0; i < heapSize; ++i)
                Output[outCount++] = HeapNode[i];
            Count[0] = outCount;
        }

        float PixelScale(uint node)
        {
            var delta = Center[(int)node] - View.Origin;
            var distance = math.max(math.length(delta), 1e-6f);
            var scale = Size[(int)node] / distance;
            var forward = math.dot(delta, View.Forward);
            float foveate;
            if (forward <= 0f)
            {
                foveate = View.BehindFoveate;
            }
            else
            {
                var dot = forward / distance;
                if (dot >= View.ConeDot0)
                    foveate = 1f;
                else if (dot >= View.ConeDot)
                    foveate = View.ConeFoveate + (1f - View.ConeFoveate) * (dot - View.ConeDot) / (View.ConeDot0 - View.ConeDot);
                else
                    foveate = View.BehindFoveate + (View.ConeFoveate - View.BehindFoveate) * dot / View.ConeDot;
            }

            return scale * foveate;
        }

        void Push(ref int size, float key, uint node)
        {
            var i = size++;
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (HeapKey[parent] >= key)
                    break;
                HeapKey[i] = HeapKey[parent];
                HeapNode[i] = HeapNode[parent];
                i = parent;
            }

            HeapKey[i] = key;
            HeapNode[i] = node;
        }

        void Pop(ref int size)
        {
            --size;
            var lastKey = HeapKey[size];
            var lastNode = HeapNode[size];
            var i = 0;
            while (true)
            {
                var child = 2 * i + 1;
                if (child >= size)
                    break;
                if (child + 1 < size && HeapKey[child + 1] > HeapKey[child])
                    ++child;
                if (HeapKey[child] <= lastKey)
                    break;
                HeapKey[i] = HeapKey[child];
                HeapNode[i] = HeapNode[child];
                i = child;
            }

            if (size > 0)
            {
                HeapKey[i] = lastKey;
                HeapNode[i] = lastNode;
            }
        }
    }
}
