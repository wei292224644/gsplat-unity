// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Gsplat
{
    /// <summary>
    /// Contents of a <c>.gsd</c> file, laid out exactly as in the file so each array uploads to a
    /// GraphicsBuffer unchanged. Only <see cref="GsdReader.Read"/> produces one.
    /// </summary>
    public sealed class GsdData
    {
        public uint NodeCount;
        public uint LeafCount;
        public byte SHDegree;
        public float3 BoundsMin;
        public float3 BoundsMax;
        /// <summary>Two <c>uint4</c> per node: the 32-byte ExtSplat layout.</summary>
        public uint4[] Nodes;
        public uint[] SH1; // 2 words per node when SHDegree >= 1, else empty
        public uint[] SH2; // 4 words per node when SHDegree >= 2, else empty
        public uint[] SH3; // 4 words per node when SHDegree >= 3, else empty
        public uint[] ChildStart;
        public ushort[] ChildCount;
    }

    public sealed class GsdFormatException : Exception
    {
        public GsdFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// The one parser for <c>.gsd</c> files (spec §4), shared by the importer and runtime byte
    /// loading. Runtime bytes cross a trust boundary, so every section length and every tree
    /// invariant is checked before anything is returned. Messages match <c>gsd-build</c>'s reader.
    /// </summary>
    public static class GsdReader
    {
        public const uint Version = 1;
        public const int HeaderSize = 48;

        public static GsdData Read(ReadOnlySpan<byte> bytes)
        {
            if (!BitConverter.IsLittleEndian)
                throw new GsdFormatException("gsd: big-endian hosts are not supported");
            if (bytes.Length < HeaderSize)
                throw new GsdFormatException($"gsd: truncated (expected {HeaderSize} bytes, got {bytes.Length})");
            if (bytes[0] != (byte)'G' || bytes[1] != (byte)'S' || bytes[2] != (byte)'D' || bytes[3] != 0)
                throw new GsdFormatException("gsd: bad magic");
            var version = U32(bytes, 4);
            if (version != Version)
                throw new GsdFormatException($"gsd: unsupported version {version}");
            var nodeCount = U32(bytes, 8);
            var leafCount = U32(bytes, 12);
            var shDegree = bytes[16];
            if (shDegree > 3)
                throw new GsdFormatException($"gsd: sh degree {shDegree} out of range 0..3");

            // Lengths are computed in long and checked against the actual size before any array is
            // allocated, so a header claiming billions of nodes fails here instead of in the allocator.
            var layout = Layout.For(nodeCount, shDegree);
            if (bytes.Length < layout.Total)
                throw new GsdFormatException($"gsd: truncated (expected {layout.Total} bytes, got {bytes.Length})");
            if (bytes.Length > layout.Total)
                throw new GsdFormatException($"gsd: trailing bytes (expected {layout.Total} bytes, got {bytes.Length})");

            var n = (int)nodeCount;
            var data = new GsdData
            {
                NodeCount = nodeCount,
                LeafCount = leafCount,
                SHDegree = shDegree,
                BoundsMin = new float3(F32(bytes, 20), F32(bytes, 24), F32(bytes, 28)),
                BoundsMax = new float3(F32(bytes, 32), F32(bytes, 36), F32(bytes, 40)),
                Nodes = Slice<uint4>(bytes, layout.Nodes, n * 2),
                SH1 = shDegree >= 1 ? Slice<uint>(bytes, layout.SH1, n * 2) : Array.Empty<uint>(),
                SH2 = shDegree >= 2 ? Slice<uint>(bytes, layout.SH2, n * 4) : Array.Empty<uint>(),
                SH3 = shDegree >= 3 ? Slice<uint>(bytes, layout.SH3, n * 4) : Array.Empty<uint>(),
                ChildStart = Slice<uint>(bytes, layout.ChildStart, n),
                ChildCount = Slice<ushort>(bytes, layout.ChildCount, n),
            };
            CheckTree(data.ChildStart, data.ChildCount, leafCount);
            return data;
        }

        /// <summary>Invariants ① – ④ of spec §4.</summary>
        public static void CheckTree(uint[] childStart, ushort[] childCount, uint leafCount)
        {
            var n = childStart.Length;
            if (childCount.Length != n)
                throw new ArgumentException("childStart and childCount must have the same length");
            if (n == 0)
                throw new GsdFormatException("gsd: empty (nodeCount 0)");

            var hasParent = new bool[n];
            hasParent[0] = true; // ①: node 0 is the root
            uint leaves = 0;
            for (var i = 0; i < n; ++i)
            {
                var count = childCount[i];
                if (count == 0)
                {
                    ++leaves;
                    continue;
                }

                var start = childStart[i];
                if (start <= (uint)i)
                    throw new GsdFormatException($"gsd invariant 2: node {i} childStart {start} is not after its parent");
                var end = (long)start + count;
                if (end > n)
                    throw new GsdFormatException($"gsd invariant 2: node {i} child range ends at {end}, past nodeCount {n}");
                for (var c = (int)start; c < end; ++c)
                {
                    if (hasParent[c])
                        throw new GsdFormatException(
                            $"gsd invariant 3: node {c} is claimed by more than one parent (second: node {i})");
                    hasParent[c] = true;
                }
            }

            var orphan = Array.IndexOf(hasParent, false);
            if (orphan >= 0)
                throw new GsdFormatException($"gsd invariant 3: node {orphan} has no parent");
            if (leaves != leafCount)
                throw new GsdFormatException(
                    $"gsd invariant 4: leafCount declares {leafCount} but the tree has {leaves} leaves");
        }

        static T[] Slice<T>(ReadOnlySpan<byte> bytes, long offset, int count) where T : struct =>
            MemoryMarshal.Cast<byte, T>(bytes.Slice((int)offset, count * Marshal.SizeOf<T>())).ToArray();

        static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4));

        static float F32(ReadOnlySpan<byte> bytes, int at) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4)));

        /// <summary>Section offsets; mirrors <c>layout()</c> in <c>gsd-build/src/format.rs</c>.</summary>
        readonly struct Layout
        {
            public readonly long Nodes, SH1, SH2, SH3, ChildStart, ChildCount, Total;

            Layout(long nodes, long sh1, long sh2, long sh3, long childStart, long childCount, long total)
            {
                Nodes = nodes; SH1 = sh1; SH2 = sh2; SH3 = sh3;
                ChildStart = childStart; ChildCount = childCount; Total = total;
            }

            static long Align16(long n) => (n + 15) & ~15L;

            public static Layout For(uint nodeCount, byte shDegree)
            {
                long n = nodeCount, pos = HeaderSize;
                var nodes = pos;
                pos += n * 32;
                long sh1 = 0, sh2 = 0, sh3 = 0;
                if (shDegree >= 1) { pos = Align16(pos); sh1 = pos; pos += n * 8; }
                if (shDegree >= 2) { pos = Align16(pos); sh2 = pos; pos += n * 16; }
                if (shDegree >= 3) { pos = Align16(pos); sh3 = pos; pos += n * 16; }
                pos = Align16(pos);
                var childStart = pos;
                pos += n * 4;
                pos = Align16(pos);
                var childCount = pos;
                pos += n * 2;
                return new Layout(nodes, sh1, sh2, sh3, childStart, childCount, Align16(pos));
            }
        }
    }
}
