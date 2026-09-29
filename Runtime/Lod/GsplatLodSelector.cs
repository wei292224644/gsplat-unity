// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;

namespace Gsplat
{
    /// <summary>
    /// One LoD traversal in flight at a time (spec §6). <see cref="TrySchedule"/> starts one when
    /// idle and the view changed; <see cref="TryComplete"/> publishes it once done. Neither blocks —
    /// only <see cref="RunNow"/> does, and it is meant for bind time only (D10). The GPU keeps
    /// drawing the previous cut until a new one is published.
    /// </summary>
    public sealed class GsplatLodSelector : IDisposable
    {
        readonly GsplatLodTree m_tree;
        readonly GsplatLodScratch m_scratch;
        JobHandle m_handle;
        bool m_running;
        bool m_hasView;
        GsplatLodView m_lastView;
        int m_scheduledFrame;

        public int Budget => m_scratch.Budget;

        /// <summary>Frames between the last scheduled traversal and its publication.</summary>
        public int LastLatencyFrames { get; private set; }

        public GsplatLodSelector(GsplatLodTree tree, int budget)
        {
            m_tree = tree;
            m_scratch = new GsplatLodScratch(budget);
        }

        public NativeArray<uint> RunNow(in GsplatLodView view)
        {
            CompletePending();
            GsplatLodTraversal.CreateJob(m_tree, view, m_scratch).Run();
            m_lastView = view;
            m_hasView = true;
            LastLatencyFrames = 0;
            return Result();
        }

        public bool TrySchedule(in GsplatLodView view, int frame)
        {
            if (m_running || (m_hasView && view.Equals(m_lastView)))
                return false;
            m_handle = GsplatLodTraversal.CreateJob(m_tree, view, m_scratch).Schedule();
            JobHandle.ScheduleBatchedJobs();
            m_running = true;
            m_lastView = view;
            m_hasView = true;
            m_scheduledFrame = frame;
            return true;
        }

        /// <summary>Publishes the traversal in flight if it has finished; the array is valid until the next schedule.</summary>
        public bool TryComplete(int frame, out NativeArray<uint> indices)
        {
            indices = default;
            if (!m_running || !m_handle.IsCompleted)
                return false;
            m_handle.Complete();
            m_running = false;
            LastLatencyFrames = frame - m_scheduledFrame;
            indices = Result();
            return true;
        }

        /// <summary>
        /// Times one synchronous traversal of <paramref name="view"/> in scratch memory of its own, so
        /// the traversal in flight is untouched. For the bench; stalls the calling thread.
        /// </summary>
        public double MeasureMilliseconds(in GsplatLodView view)
        {
            using var scratch = new GsplatLodScratch(view.Budget);
            var job = GsplatLodTraversal.CreateJob(m_tree, view, scratch);
            var watch = Stopwatch.StartNew();
            job.Run();
            return watch.Elapsed.TotalMilliseconds;
        }

        NativeArray<uint> Result() => m_scratch.Output.GetSubArray(0, m_scratch.Count[0]);

        void CompletePending()
        {
            if (!m_running)
                return;
            m_handle.Complete();
            m_running = false;
        }

        public void Dispose()
        {
            CompletePending();
            m_scratch.Dispose();
        }
    }
}
