using System;
using System.Collections.Generic;
using System.Linq;
using OpenATLAS.Core.Reference;
using ScottPlot;
using ScottPlot.Plottables;

namespace OpenATLAS.Workbench.Plotting
{
    /// <summary>
    /// Track map in the sidebar: the circuit outline from track.json, split into the three
    /// timing sectors, with the car, the distance cursor and pins marked on it.
    /// Everything is positioned by fraction of the lap (0 = start/finish line), so a car on a
    /// lap of slightly different length, or on an AC track, still lands on the right corner.
    /// </summary>
    public class TrackMap
    {
        private static readonly Color CanvasColor = Color.FromHex("#1E1E1E");
        private static readonly Color SectorColor = Color.FromHex("#5A5A5E");
        private static readonly Color ActiveSectorColor = Color.FromHex("#D4D4D4");
        private static readonly Color LabelColor = Color.FromHex("#858585");
        private static readonly Color LineColor = Color.FromHex("#FFFFFF");
        private static readonly Color CarColor = Color.FromHex("#00E5FF");
        private static readonly Color PinColor = Color.FromHex("#FF5252");

        // Turn numbers sit this far outside the track, as a share of the map's size
        private const double LabelOffsetShare = 0.045;

        private readonly List<Scatter> _sectorLines = [];
        private readonly List<Marker> _pinDots = [];
        private double[] _sectorEndFractions = [];

        private TrackLayout? _layout;
        private Marker? _car;
        private Marker? _hover;

        public Plot Plot { get; }

        public TrackMap(Plot plot)
        {
            Plot = plot ?? throw new ArgumentNullException(nameof(plot));
            Plot.FigureBackground.Color = CanvasColor;
            Plot.DataBackground.Color = CanvasColor;
            Plot.Axes.Frameless();
            Plot.HideGrid();
        }

        /// <summary>True when the loaded layout has an outline to draw.</summary>
        public bool HasMap => _layout?.HasOutline == true;

        /// <summary>Index of the highlighted sector, or -1 when the car is not on the map.</summary>
        public int ActiveSector { get; private set; } = -1;

        public int PinCount => _pinDots.Count;
        public Coordinates? CarPosition => _car is { IsVisible: true } ? _car.Location : null;
        public Coordinates? HoverPosition => _hover is { IsVisible: true } ? _hover.Location : null;

        /// <summary>Draws the outline for a Grand Prix; null or a layout without an outline clears the map.</summary>
        public void Load(TrackLayout? layout)
        {
            Plot.Clear();
            _sectorLines.Clear();
            _pinDots.Clear();
            _car = _hover = null;
            ActiveSector = -1;
            _layout = layout;

            if (layout == null || !layout.HasOutline) return;

            // Sector boundaries as lap fractions (sectors and outline come from the same lap)
            float length = layout.LengthMeters > 0 ? layout.LengthMeters : layout.OutlineLengthMeters;
            _sectorEndFractions = [.. layout.Sectors.Select(s => s.EndMeters / length)];

            double start = 0;
            foreach (double end in _sectorEndFractions)
            {
                _sectorLines.Add(AddSegment(start, end));
                start = end;
            }

            AddCornerLabels(layout, length);

            // Start/finish line
            var (sx, sy) = layout.PointAtFraction(0);
            Plot.Add.Marker(sx, sy, MarkerShape.FilledSquare, size: 7, color: LineColor);

            _hover = Plot.Add.Marker(0, 0, MarkerShape.OpenCircle, size: 11, color: ActiveSectorColor);
            _hover.IsVisible = false;
            _car = Plot.Add.Marker(0, 0, MarkerShape.FilledCircle, size: 10, color: CarColor);
            _car.IsVisible = false;

            Plot.Axes.AutoScale();
            Plot.Axes.Margins(0.08, 0.08);
            Plot.Axes.SquareUnits(); // a circuit must not be stretched
        }

        /// <summary>Moves the car dot and highlights its sector; null hides it.</summary>
        public void SetCar(double? lapFraction)
        {
            if (_car == null) return;

            if (lapFraction is not double fraction)
            {
                _car.IsVisible = false;
                HighlightSector(-1);
                return;
            }

            _car.Location = PointAt(fraction);
            _car.IsVisible = true;
            HighlightSector(SectorAt(fraction));
        }

        /// <summary>White ring at the distance cursor while the mouse is over a trace pane.</summary>
        public void SetHover(double? lapFraction)
        {
            if (_hover == null) return;
            _hover.IsVisible = lapFraction.HasValue;
            if (lapFraction is double fraction) _hover.Location = PointAt(fraction);
        }

        /// <summary>Red dot per distance-cursor pin. Dots are only created/removed when the count changes.</summary>
        public void SetPins(IReadOnlyList<double> lapFractions)
        {
            if (!HasMap) return;

            while (_pinDots.Count > lapFractions.Count)
            {
                Plot.Remove(_pinDots[^1]);
                _pinDots.RemoveAt(_pinDots.Count - 1);
            }
            while (_pinDots.Count < lapFractions.Count)
            {
                _pinDots.Add(Plot.Add.Marker(0, 0, MarkerShape.FilledCircle, size: 8, color: PinColor));
            }

            for (int i = 0; i < lapFractions.Count; i++) _pinDots[i].Location = PointAt(lapFractions[i]);

            // The car stays drawn above the pins
            if (_car != null) Plot.MoveToTop(_car);
        }

        private Coordinates PointAt(double fraction)
        {
            var (x, y) = _layout!.PointAtFraction(fraction);
            return new Coordinates(x, y);
        }

        private int SectorAt(double fraction)
        {
            fraction -= Math.Floor(fraction);
            for (int i = 0; i < _sectorEndFractions.Length; i++)
            {
                if (fraction < _sectorEndFractions[i]) return i;
            }
            return _sectorEndFractions.Length - 1;
        }

        private void HighlightSector(int sector)
        {
            if (sector == ActiveSector) return;
            ActiveSector = sector;
            for (int i = 0; i < _sectorLines.Count; i++)
            {
                _sectorLines[i].Color = i == sector ? ActiveSectorColor : SectorColor;
            }
        }

        // One sector as a polyline through the outline points between two lap fractions
        private Scatter AddSegment(double startFraction, double endFraction)
        {
            const int pointsPerSegment = 200;
            var xs = new double[pointsPerSegment + 1];
            var ys = new double[pointsPerSegment + 1];
            for (int i = 0; i <= pointsPerSegment; i++)
            {
                double f = startFraction + (endFraction - startFraction) * i / pointsPerSegment;
                // Clamp just below 1 so the final point is the finish, not wrapped back to the start
                var (x, y) = _layout!.PointAtFraction(Math.Min(f, 0.999999));
                xs[i] = x;
                ys[i] = y;
            }

            var line = Plot.Add.ScatterLine(xs, ys, SectorColor);
            line.LineWidth = 3;
            return line;
        }

        // Turn numbers beside their corners. track.json stores label positions (from FastF1's corner
        // angles); older files without them fall back to pushing labels outwards from the centre.
        private void AddCornerLabels(TrackLayout layout, float length)
        {
            double cx = layout.Outline.Average(p => p[1]);
            double cy = layout.Outline.Average(p => p[2]);
            double size = Math.Max(
                layout.Outline.Max(p => p[1]) - layout.Outline.Min(p => p[1]),
                layout.Outline.Max(p => p[2]) - layout.Outline.Min(p => p[2]));
            double offset = size * LabelOffsetShare;

            foreach (TrackCorner corner in layout.Corners)
            {
                double x, y;
                if (corner.LabelX is float labelX && corner.LabelY is float labelY)
                {
                    (x, y) = (labelX, labelY);
                }
                else
                {
                    (x, y) = layout.PointAtFraction(corner.DistanceMeters / length);
                    double dx = x - cx, dy = y - cy;
                    double norm = Math.Sqrt(dx * dx + dy * dy);
                    if (norm > 0)
                    {
                        x += dx / norm * offset;
                        y += dy / norm * offset;
                    }
                }

                var label = Plot.Add.Text($"{corner.Number}{corner.Letter}", x, y);
                label.LabelFontColor = LabelColor;
                label.LabelFontSize = 9;
                label.LabelFontName = "Consolas";
                label.LabelAlignment = Alignment.MiddleCenter;
            }
        }
    }
}
