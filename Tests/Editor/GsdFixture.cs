// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEngine;

namespace Gsplat.Tests
{
    /// <summary>
    /// The fixture written by <c>Tools~/gsd-build/tests/fixture.rs</c>: an 8-node .gsd and the Rust
    /// decoder's view of it. Regenerate both there, never by hand.
    /// </summary>
    static class GsdFixture
    {
        public const string Dir = "Packages/wu.yize.gsplat/Tests/Editor/Fixtures/";
        public const string GsdPath = Dir + "tiny.gsd";

        public static byte[] Bytes() => File.ReadAllBytes(Path.GetFullPath(GsdPath));

        public static Expected LoadExpected() =>
            JsonUtility.FromJson<Expected>(File.ReadAllText(Path.GetFullPath(Dir + "tiny.expected.json")));

        [Serializable]
        public class Expected
        {
            public uint nodeCount;
            public uint leafCount;
            public int shDegree;
            public Node[] nodes;
        }

        [Serializable]
        public class Node
        {
            public float[] center;
            public float alpha;
            public float[] rgb;
            public float[] scale;
            public float[] quat; // real quaternion (x, y, z, w)
            public float[] sh;   // 45 floats: band 1, 2, 3, coefficient-major, channel-minor
            public uint childStart;
            public int childCount;
        }
    }
}
