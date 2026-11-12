using System;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;
using OpenATLAS.Workbench.Plotting;
using ScottPlot;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.2 Unit Tests
    /// Verifies trace pane setup, reference trace loading, and live scatter buffer updates.
    /// </summary>
    public class TracePanesTests
    {
        private static ReferenceLapIndex CreateMockReferenceLap(int length = 500)
        {
            var distance = new int[length];
            var speed = new float[length];
            var throttle = new sbyte[length];
            var brake = new sbyte[length];
            var rpm = new int[length];
            var gear = new sbyte[length];
            var time = new float[length];

            for (int i = 0; i < length; i++)
            {
                distance[i] = i;
                speed[i] = 250f;
                throttle[i] = 100;
                rpm[i] = 11000;
                gear[i] = 7;
                time[i] = i * 0.02f;
            }

            return new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);
        }

        private static LiveDeltaSnapshot Sample(float distance, float delta = 0f) =>
            new(distance, 200f, 210f, 100f, 100f, 0f, 0f, 6, 6, 10000, 10000, 1f, 1f, delta);

        private static TracePanes CreatePanes(int capacity = 100) =>
            new(new Plot(), new Plot(), new Plot(), capacity);

        [Fact]
        public void Constructor_LiveTracesStartHidden()
        {
            var panes = CreatePanes();

            Assert.Equal(0, panes.LiveCount);
            Assert.Equal(100, panes.LiveCapacity);
            // Every pane has a hidden distance cursor line, plus:
            // Speed: 1 live line; Pedals: throttle + brake; Delta: zero line + live line
            Assert.Equal(2, panes.SpeedPlot.GetPlottables().Count());
            Assert.Equal(3, panes.PedalPlot.GetPlottables().Count());
            Assert.Equal(3, panes.DeltaPlot.GetPlottables().Count());
            Assert.False(panes.IsCursorVisible);
        }

        [Fact]
        public void LoadReference_AddsReferenceTracesAndFitsDistanceAxis()
        {
            var panes = CreatePanes();
            var lap = CreateMockReferenceLap();

            panes.LoadReference(lap);

            Assert.Equal(3, panes.SpeedPlot.GetPlottables().Count());
            Assert.Equal(5, panes.PedalPlot.GetPlottables().Count());

            foreach (var plot in new[] { panes.SpeedPlot, panes.PedalPlot, panes.DeltaPlot })
            {
                var limits = plot.Axes.GetLimits();
                Assert.Equal(0, limits.Left);
                Assert.Equal(lap.TrackLengthMeters, limits.Right);
            }
        }

        [Fact]
        public void LoadReference_CalledTwice_ReplacesPreviousTraces()
        {
            var panes = CreatePanes();

            panes.LoadReference(CreateMockReferenceLap());
            panes.LoadReference(CreateMockReferenceLap(800));

            Assert.Equal(3, panes.SpeedPlot.GetPlottables().Count());
            Assert.Equal(5, panes.PedalPlot.GetPlottables().Count());
        }

        [Fact]
        public void UpdateLive_CopiesSamplesIntoBuffers()
        {
            var panes = CreatePanes();
            var samples = new[] { Sample(10f), Sample(20f), Sample(30f) };

            panes.UpdateLive(samples);

            Assert.Equal(3, panes.LiveCount);
        }

        [Fact]
        public void UpdateLive_OnlyKeepsSamplesSinceLastLapWrap()
        {
            var panes = CreatePanes();
            var samples = new[] { Sample(5700f), Sample(5790f), Sample(2f), Sample(15f) };

            panes.UpdateLive(samples);

            Assert.Equal(2, panes.LiveCount);
        }

        [Fact]
        public void UpdateLive_TruncatesToCapacity()
        {
            var panes = CreatePanes(capacity: 4);
            var samples = new LiveDeltaSnapshot[10];
            for (int i = 0; i < samples.Length; i++) samples[i] = Sample(i * 10f);

            panes.UpdateLive(samples);

            Assert.Equal(4, panes.LiveCount);
        }

        [Fact]
        public void DeltaAxis_GrowsToFitTheLap_KeepingZeroInTheMiddle()
        {
            var panes = CreatePanes();

            panes.UpdateLive(new[] { Sample(10f, -0.4f), Sample(20f, 0.5f) });
            Assert.Equal(1, panes.DeltaAxisLimit);   // small deltas: the default +/-1 s

            panes.UpdateLive(new[] { Sample(10f, -0.4f), Sample(20f, -46.8f) });
            Assert.Equal(60, panes.DeltaAxisLimit);  // -46.8 s fits in +/-60 s
            var limits = panes.DeltaPlot.Axes.GetLimits();
            Assert.Equal(-60, limits.Bottom);
            Assert.Equal(60, limits.Top);
        }

        [Fact]
        public void DeltaAxis_ShrinksBackOnANewLap()
        {
            var panes = CreatePanes();
            panes.UpdateLive(new[] { Sample(5000f, -3f), Sample(5700f, -4f) });
            Assert.Equal(5, panes.DeltaAxisLimit);

            panes.UpdateLive(new[] { Sample(5000f, -3f), Sample(5700f, -4f), Sample(10f, 0.1f) });
            Assert.Equal(1, panes.DeltaAxisLimit);

            panes.UpdateLive(new[] { Sample(10f, -3f) });
            panes.ClearLive();
            Assert.Equal(1, panes.DeltaAxisLimit);
        }

        [Fact]
        public void ClearLive_ResetsCount()
        {
            var panes = CreatePanes();
            panes.UpdateLive(new[] { Sample(10f), Sample(20f) });

            panes.ClearLive();

            Assert.Equal(0, panes.LiveCount);
        }

        [Fact]
        public void Render_WithReferenceAndLiveData_DoesNotThrow()
        {
            var panes = CreatePanes();
            panes.LoadReference(CreateMockReferenceLap());
            panes.UpdateLive(new[] { Sample(10f, 0.1f), Sample(20f, -0.2f) });

            foreach (var plot in new[] { panes.SpeedPlot, panes.PedalPlot, panes.DeltaPlot })
            {
                var image = plot.GetImage(400, 200);
                Assert.Equal(400, image.Width);
            }
        }
    }
}
