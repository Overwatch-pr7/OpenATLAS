using System;
using Xunit;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Models;
using OpenATLAS.Workbench.Plotting;
using OpenATLAS.Workbench.ViewModels;
using ScottPlot;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.3 Unit Tests
    /// Verifies the render loop copies the ring buffer into the trace panes and readouts,
    /// and skips frames when no new data has arrived.
    /// </summary>
    public class LiveRenderLoopTests
    {
        private static LiveDeltaSnapshot Sample(float distance, float delta = 0f) =>
            new(distance, 200f, 210f, 100f, 100f, 0f, 0f, 6, 6, 10000, 10000, 1f, 1f, delta);

        // Builds a loop over a fresh buffer; redraws are counted instead of rendered
        private static (LiveRenderLoop loop, CircularBuffer<LiveDeltaSnapshot> buffer, TracePanes panes,
            WorkbenchViewModel vm, Func<int> redraws) Create()
        {
            var buffer = new CircularBuffer<LiveDeltaSnapshot>(100);
            var panes = new TracePanes(new Plot(), new Plot(), new Plot(), 100);
            var vm = new WorkbenchViewModel(referenceLap: null);
            int redrawCount = 0;
            var loop = new LiveRenderLoop(buffer, panes, vm, () => redrawCount++);
            return (loop, buffer, panes, vm, () => redrawCount);
        }

        [Fact]
        public void FrameInterval_Is60Hz()
        {
            Assert.Equal(1.0 / 60.0, LiveRenderLoop.FrameInterval.TotalSeconds, 4);
        }

        [Fact]
        public void Tick_CopiesBufferIntoPanesAndViewModel()
        {
            var (loop, buffer, panes, vm, redraws) = Create();
            buffer.Push(Sample(10f, 0.1f));
            buffer.Push(Sample(20f, -0.2f));

            Assert.True(loop.Tick());

            Assert.Equal(2, panes.LiveCount);
            Assert.Equal(20f, vm.CurrentDistanceMeters);   // newest sample drives the readouts
            Assert.Equal(-0.2f, vm.CurrentDeltaSeconds);
            Assert.Equal(1, redraws());
            vm.Dispose();
        }

        [Fact]
        public void Tick_SkipsRedrawWhenNothingChanged()
        {
            var (loop, buffer, _, vm, redraws) = Create();
            buffer.Push(Sample(10f));

            Assert.True(loop.Tick());
            Assert.False(loop.Tick());
            Assert.Equal(1, redraws());

            buffer.Push(Sample(20f));
            Assert.True(loop.Tick());
            Assert.Equal(2, redraws());
            vm.Dispose();
        }

        [Fact]
        public void Tick_ClearsLiveTracesWhenBufferCleared()
        {
            var (loop, buffer, panes, vm, _) = Create();
            buffer.Push(Sample(10f));
            loop.Tick();

            buffer.Clear();
            Assert.True(loop.Tick());
            Assert.Equal(0, panes.LiveCount);
            vm.Dispose();
        }
    }
}
