// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Jobs;
using Unity.Mathematics;

namespace Gsplat.Tests
{
    public class GsplatLodTraversalTests
    {
        GsplatLodTree m_tree;
        int[] m_parent;

        [SetUp] public void SetUp() => m_tree = LodTestTrees.Complete(4, 4, out m_parent); // 341 nodes, 256 leaves
        [TearDown] public void TearDown() => m_tree.Dispose();

        uint[] Select(GsplatLodView view)
        {
            using var scratch = new GsplatLodScratch(view.Budget);
            GsplatLodTraversal.CreateJob(m_tree, view, scratch).Run();
            return scratch.Output.GetSubArray(0, scratch.Count[0]).ToArray();
        }

        [TestCase(1)] [TestCase(2)] [TestCase(5)] [TestCase(17)] [TestCase(100)] [TestCase(341)] [TestCase(1000)]
        public void OutputNeverExceedsTheBudget(int budget)
        {
            var output = Select(LodTestTrees.View(new float3(0, 0, -2), budget, 0f));
            Assert.That(output.Length, Is.InRange(1, budget));
        }

        [Test]
        public void OutputIsAlwaysAValidCut()
        {
            foreach (var budget in new[] { 1, 7, 50, 400 })
            foreach (var distance in new[] { 1f, 10f, 100f })
                AssertValidCut(Select(LodTestTrees.View(new float3(0, 0, -distance), budget)), $"budget {budget} distance {distance}");
        }

        [Test]
        public void AFartherViewSelectsFewerNodes()
        {
            var near = Select(LodTestTrees.View(new float3(0, 0, -2), 1000, 0.01f)).Length;
            var far = Select(LodTestTrees.View(new float3(0, 0, -100), 1000, 0.01f)).Length;
            Assert.Less(far, near);
        }

        [TestCase(341)]
        [TestCase(3410)]
        public void UnboundedBudgetAndZeroLimitSelectEveryLeaf(int budget)
        {
            var output = Select(LodTestTrees.View(new float3(0, 0, -2), budget, 0f));
            var leaves = Enumerable.Range(0, m_tree.NodeCount).Where(i => m_tree.ChildCount[i] == 0).Select(i => (uint)i);
            CollectionAssert.AreEquivalent(leaves, output);
        }

        [Test]
        public void BudgetOfOneSelectsTheRoot() =>
            CollectionAssert.AreEqual(new uint[] { 0 }, Select(LodTestTrees.View(new float3(0, 0, -2), 1, 0f)));

        [Test]
        public void CameraOnANodeCentreStaysFinite()
        {
            var output = Select(LodTestTrees.View(float3.zero, 64, 0f)); // the root's centre
            Assert.That(output.Length, Is.InRange(1, 64));
            AssertValidCut(output, "camera at the root centre");
        }

        void AssertValidCut(uint[] output, string context)
        {
            var selected = new HashSet<uint>(output);
            Assert.AreEqual(output.Length, selected.Count, $"{context}: a node was output twice");
            for (var leaf = 0; leaf < m_parent.Length; ++leaf)
            {
                if (m_tree.ChildCount[leaf] != 0)
                    continue;
                var covering = 0;
                for (var n = leaf; n >= 0; n = m_parent[n])
                    if (selected.Contains((uint)n))
                        ++covering;
                Assert.AreEqual(1, covering, $"{context}: leaf {leaf} is covered {covering} times");
            }
        }
    }
}
