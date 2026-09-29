// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Gsplat.Tests
{
    public class GsplatLodSelectorTests
    {
        GsplatLodTree m_tree;

        [SetUp] public void SetUp() => m_tree = LodTestTrees.Complete(4, 4, out _);
        [TearDown] public void TearDown() => m_tree.Dispose();

        static GsplatLodView View(float z) => LodTestTrees.View(new float3(0f, 0f, z), 64);

        [Test]
        public void RunNowPublishesSynchronously()
        {
            using var selector = new GsplatLodSelector(m_tree, 64);
            Assert.That(selector.RunNow(View(-10f)).Length, Is.InRange(1, 64));
        }

        [Test]
        public void AScheduledTraversalPublishesOnceComplete()
        {
            using var selector = new GsplatLodSelector(m_tree, 64);
            Assert.IsTrue(selector.TrySchedule(View(-10f), 1));
            Assert.That(WaitFor(selector, 3).Length, Is.InRange(1, 64));
            Assert.AreEqual(2, selector.LastLatencyFrames);
        }

        [Test]
        public void AStillViewIsNotScheduledAgain()
        {
            using var selector = new GsplatLodSelector(m_tree, 64);
            Assert.IsTrue(selector.TrySchedule(View(-10f), 1));
            WaitFor(selector, 1);
            Assert.IsFalse(selector.TrySchedule(View(-10f), 2), "same view must not reschedule");
            Assert.IsTrue(selector.TrySchedule(View(-11f), 3), "a moved view must reschedule");
            WaitFor(selector, 3);
        }

        [Test]
        public void SchedulingWhileATraversalIsInFlightIsRefused()
        {
            using var selector = new GsplatLodSelector(m_tree, 64);
            Assert.IsTrue(selector.TrySchedule(View(-10f), 1));
            Assert.IsFalse(selector.TrySchedule(View(-20f), 1));
            WaitFor(selector, 1);
        }

        [Test]
        public void DisposeWithATraversalInFlightIsSafe()
        {
            var selector = new GsplatLodSelector(m_tree, 64);
            selector.TrySchedule(View(-10f), 1);
            Assert.DoesNotThrow(selector.Dispose);
        }

        [Test]
        public void MeasureDoesNotDisturbTheTraversalInFlight()
        {
            using var selector = new GsplatLodSelector(m_tree, 64);
            selector.TrySchedule(View(-10f), 1);
            Assert.GreaterOrEqual(selector.MeasureMilliseconds(View(-5f)), 0.0);
            Assert.That(WaitFor(selector, 2).Length, Is.InRange(1, 64));
        }

        static NativeArray<uint> WaitFor(GsplatLodSelector selector, int frame)
        {
            var watch = Stopwatch.StartNew();
            NativeArray<uint> indices;
            while (!selector.TryComplete(frame, out indices))
            {
                if (watch.Elapsed.TotalSeconds > 5)
                    Assert.Fail("traversal never completed");
                Thread.Yield();
            }

            return indices;
        }
    }
}
