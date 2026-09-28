// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class GsdReaderTests
    {
        // root 0 → [1, 2]; node 1 → [3, 4]; leaves 2, 3, 4.
        static readonly uint[] k_start = { 1, 3, 0, 0, 0 };
        static readonly ushort[] k_count = { 2, 2, 0, 0, 0 };

        [Test]
        public void ReadsTheFixture()
        {
            var expected = GsdFixture.LoadExpected();
            var data = GsdReader.Read(GsdFixture.Bytes());
            Assert.AreEqual(expected.nodeCount, data.NodeCount);
            Assert.AreEqual(expected.leafCount, data.LeafCount);
            Assert.AreEqual(expected.shDegree, data.SHDegree);
            Assert.AreEqual(data.NodeCount * 2, data.Nodes.Length);
            Assert.AreEqual(data.NodeCount * 2, data.SH1.Length);
            Assert.AreEqual(data.NodeCount * 4, data.SH3.Length);
            for (var i = 0; i < expected.nodes.Length; ++i)
            {
                Assert.AreEqual(expected.nodes[i].childStart, data.ChildStart[i], $"childStart[{i}]");
                Assert.AreEqual(expected.nodes[i].childCount, data.ChildCount[i], $"childCount[{i}]");
            }
        }

        [Test] public void RejectsBadMagic() => ExpectError(b => b[0] = (byte)'X', "bad magic");
        [Test] public void RejectsUnsupportedVersion() => ExpectError(b => b[4] = 2, "unsupported version 2");
        [Test] public void RejectsShDegreeOutOfRange() => ExpectError(b => b[16] = 4, "sh degree 4");
        [Test] public void RejectsLeafCountMismatch() => ExpectError(b => b[12] += 1, "invariant 4");

        [Test]
        public void RejectsHugeNodeCountBeforeAllocating() =>
            ExpectError(b => { b[8] = 0xff; b[9] = 0xff; b[10] = 0xff; b[11] = 0x7f; }, "truncated");

        [Test]
        public void RejectsTruncatedFile()
        {
            var bytes = GsdFixture.Bytes();
            Assert.That(() => GsdReader.Read(bytes.AsSpan(0, bytes.Length - 1)),
                Throws.TypeOf<GsdFormatException>().With.Message.Contains("truncated"));
        }

        [Test]
        public void RejectsTrailingBytes()
        {
            var bytes = GsdFixture.Bytes();
            Array.Resize(ref bytes, bytes.Length + 16);
            Assert.That(() => GsdReader.Read(bytes),
                Throws.TypeOf<GsdFormatException>().With.Message.Contains("trailing bytes"));
        }

        [Test] public void AcceptsAWellFormedTree() => Assert.DoesNotThrow(() => GsdReader.CheckTree(k_start, k_count, 3));
        [Test] public void RejectsAnEmptyTree() => ExpectTree(Array.Empty<uint>(), Array.Empty<ushort>(), 0, "empty");
        [Test] public void RejectsChildStartNotAfterParent() => ExpectTree(new uint[] { 1, 1, 0, 0, 0 }, k_count, 3, "invariant 2");
        [Test] public void RejectsAChildRangePastTheEnd() => ExpectTree(new uint[] { 1, 4, 0, 0, 0 }, k_count, 3, "invariant 2");
        [Test] public void RejectsANodeClaimedTwice() => ExpectTree(new uint[] { 1, 2, 0, 0, 0 }, k_count, 3, "claimed by more than one parent");
        [Test] public void RejectsAnOrphan() => ExpectTree(new uint[] { 1, 3, 0, 0, 0 }, new ushort[] { 1, 2, 0, 0, 0 }, 3, "has no parent");

        static void ExpectError(Action<byte[]> corrupt, string message)
        {
            var bytes = GsdFixture.Bytes();
            corrupt(bytes);
            Assert.That(() => GsdReader.Read(bytes), Throws.TypeOf<GsdFormatException>().With.Message.Contains(message));
        }

        static void ExpectTree(uint[] start, ushort[] count, uint leaves, string message) =>
            Assert.That(() => GsdReader.CheckTree(start, count, leaves),
                Throws.TypeOf<GsdFormatException>().With.Message.Contains(message));
    }
}
