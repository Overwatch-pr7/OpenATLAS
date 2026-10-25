using System;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;
using ScottPlot;
using ScottPlot.Plottables;

namespace OpenATLAS.Workbench.Plotting
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.2: Charting Integration (ScottPlot)
    /// Owns the three trace panes (Speed, Pedals, Delta) that share a distance X-axis.
    /// Reference traces are drawn once; live traces use pre-allocated scatter buffers
    /// that the render loop refills via <see cref="UpdateLive"/>.
    /// </summary>
    public class TracePanes
    {
        // Trackside palette (mirrors Themes/TracksideTheme.xaml)
        private static readonly Color CanvasColor = Color.FromHex("#1E1E1E");
        private static readonly Color GridColor = Color.FromHex("#2D2D30");
        private static readonly Color AxisColor = Color.FromHex("#858585");
        private static readonly Color LiveColor = Color.FromHex("#00E5FF");
        private static readonly Color ReferenceColor = Color.FromHex("#FFE600");
        private static readonly Color ThrottleColor = Color.FromHex("#00E676");
        private static readonly Color BrakeColor = Color.FromHex("#FF5252");

        private readonly double[] _liveDistance;
        private readonly double[] _liveSpeed;
        private readonly double[] _liveThrottle;
        private readonly double[] _liveBrake;
        private readonly double[] _liveDelta;

        private readonly Scatter _liveSpeedLine;
        private readonly Scatter _liveThrottleLine;
        private readonly Scatter _liveBrakeLine;
        private readonly Scatter _liveDeltaLine;

        private Scatter? _refSpeedLine;
        private Scatter? _refThrottleLine;
        private Scatter? _refBrakeLine;

        public Plot SpeedPlot { get; }
        public Plot PedalPlot { get; }
        public Plot DeltaPlot { get; }

        public int LiveCapacity => _liveDistance.Length;

        public TracePanes(Plot speedPlot, Plot pedalPlot, Plot deltaPlot, int liveCapacity)
        {
            ArgumentNullException.ThrowIfNull(speedPlot);
            ArgumentNullException.ThrowIfNull(pedalPlot);
            ArgumentNullException.ThrowIfNull(deltaPlot);
            ArgumentOutOfRangeException.ThrowIfLessThan(liveCapacity, 1);

            SpeedPlot = speedPlot;
            PedalPlot = pedalPlot;
            DeltaPlot = deltaPlot;

            _liveDistance = new double[liveCapacity];
            _liveSpeed = new double[liveCapacity];
            _liveThrottle = new double[liveCapacity];
            _liveBrake = new double[liveCapacity];
            _liveDelta = new double[liveCapacity];

            ApplyStyle(SpeedPlot, "km/h");
            ApplyStyle(PedalPlot, "%");
            ApplyStyle(DeltaPlot, "Δt (s)");
            DeltaPlot.Axes.Bottom.Label.Text = "Distance (m)";

            // Zero-delta reference line
            var zero = DeltaPlot.Add.HorizontalLine(0, width: 1, color: AxisColor, pattern: LinePattern.Dashed);
            zero.LabelText = string.Empty;

            _liveSpeedLine = AddLiveLine(SpeedPlot, _liveSpeed, LiveColor);
            _liveThrottleLine = AddLiveLine(PedalPlot, _liveThrottle, ThrottleColor);
            _liveBrakeLine = AddLiveLine(PedalPlot, _liveBrake, BrakeColor);
            _liveDeltaLine = AddLiveLine(DeltaPlot, _liveDelta, LiveColor);

            SpeedPlot.Axes.SetLimitsY(0, 360);
            PedalPlot.Axes.SetLimitsY(-5, 105);
            DeltaPlot.Axes.SetLimitsY(-1, 1);
            SetDistanceLimits(0, 5800);
        }

        /// <summary>
        /// Draws the static reference traces (speed, throttle, brake) and fits the distance axis to the lap.
        /// </summary>
        public void LoadReference(ReferenceLapIndex lap)
        {
            ArgumentNullException.ThrowIfNull(lap);

            if (_refSpeedLine != null) SpeedPlot.Remove(_refSpeedLine);
            if (_refThrottleLine != null) PedalPlot.Remove(_refThrottleLine);
            if (_refBrakeLine != null) PedalPlot.Remove(_refBrakeLine);

            int n = lap.SampleCount;
            var xs = new double[n];
            var speed = new double[n];
            var throttle = new double[n];
            var brake = new double[n];

            for (int i = 0; i < n; i++)
            {
                xs[i] = lap.Distance[i];
                speed[i] = lap.Speed[i];
                throttle[i] = lap.Throttle[i];
                brake[i] = lap.Brake[i];
            }

            _refSpeedLine = AddReferenceLine(SpeedPlot, xs, speed, ReferenceColor);
            _refThrottleLine = AddReferenceLine(PedalPlot, xs, throttle, ReferenceColor);
            _refBrakeLine = AddReferenceLine(PedalPlot, xs, brake, ReferenceColor);
            _refBrakeLine.LinePattern = LinePattern.Dotted;

            // Keep live traces drawn on top of the reference
            SpeedPlot.MoveToTop(_liveSpeedLine);
            PedalPlot.MoveToTop(_liveThrottleLine);
            PedalPlot.MoveToTop(_liveBrakeLine);

            SetDistanceLimits(0, lap.TrackLengthMeters);
        }

        /// <summary>
        /// Copies the latest live samples into the pre-allocated scatter buffers.
        /// Only the samples since the last start/finish crossing are shown, so the
        /// trace never draws a line from the end of the lap back to 0m.
        /// Does not refresh the controls; the caller decides when to render.
        /// </summary>
        public void UpdateLive(ReadOnlySpan<LiveDeltaSnapshot> samples)
        {
            if (samples.Length > LiveCapacity)
            {
                samples = samples[^LiveCapacity..];
            }

            int start = 0;
            for (int i = 1; i < samples.Length; i++)
            {
                if (samples[i].Distance < samples[i - 1].Distance) start = i;
            }

            int count = 0;
            for (int i = start; i < samples.Length; i++, count++)
            {
                ref readonly LiveDeltaSnapshot s = ref samples[i];
                _liveDistance[count] = s.Distance;
                _liveSpeed[count] = s.SimSpeed;
                _liveThrottle[count] = s.SimThrottle;
                _liveBrake[count] = s.SimBrake;
                _liveDelta[count] = s.DeltaTime;
            }

            SetLiveCount(count);
        }

        public void ClearLive() => SetLiveCount(0);

        public int LiveCount { get; private set; }

        private void SetLiveCount(int count)
        {
            LiveCount = count;
            foreach (var line in new[] { _liveSpeedLine, _liveThrottleLine, _liveBrakeLine, _liveDeltaLine })
            {
                line.IsVisible = count > 0;
                line.MaxRenderIndex = Math.Max(0, count - 1);
            }
        }

        private void SetDistanceLimits(double min, double max)
        {
            SpeedPlot.Axes.SetLimitsX(min, max);
            PedalPlot.Axes.SetLimitsX(min, max);
            DeltaPlot.Axes.SetLimitsX(min, max);
        }

        private Scatter AddLiveLine(Plot plot, double[] ys, Color color)
        {
            var line = plot.Add.ScatterLine(_liveDistance, ys, color);
            line.LineWidth = 1.5f;
            line.IsVisible = false;
            line.MaxRenderIndex = 0;
            return line;
        }

        private static Scatter AddReferenceLine(Plot plot, double[] xs, double[] ys, Color color)
        {
            var line = plot.Add.ScatterLine(xs, ys, color);
            line.LineWidth = 1f;
            return line;
        }

        private static void ApplyStyle(Plot plot, string yLabel)
        {
            plot.FigureBackground.Color = CanvasColor;
            plot.DataBackground.Color = CanvasColor;
            plot.Axes.Color(AxisColor);
            plot.Axes.FrameColor(GridColor);
            plot.Grid.MajorLineColor = GridColor;
            plot.Axes.Left.Label.Text = yLabel;
            plot.Axes.Left.Label.FontSize = 11;
            plot.Axes.Bottom.Label.FontSize = 11;
            plot.Axes.Left.TickLabelStyle.FontName = "Consolas";
            plot.Axes.Bottom.TickLabelStyle.FontName = "Consolas";

            // Fixed-width left axis so all three panes line up vertically
            plot.Axes.Left.MinimumSize = 50;
            plot.Axes.Left.MaximumSize = 50;
        }
    }
}
