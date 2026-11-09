using System;
using System.Collections.Generic;
using OpenATLAS.Core.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench.Plotting
{
    /// <summary>
    /// Distance cursor shared by the three trace panes (as in ATLAS / MoTeC).
    /// Hovering shows a dashed line that follows the mouse. A click drops a pin (solid line
    /// with a red dot on the trace) that stays put while the lap keeps playing. Near a pin the
    /// cursor snaps onto it, so pins are easy to find again; clicking a snapped pin removes it.
    /// The header readouts show live vs reference values at the hovered distance, or at the
    /// most recent pin when the mouse is away. Read-only: it never changes the recorded data.
    /// </summary>
    public sealed class DistanceCursor
    {
        private readonly TracePanes _panes;
        private readonly WorkbenchViewModel _viewModel;

        private readonly List<double> _pins = [];
        private double? _hoverDistance;

        public DistanceCursor(TracePanes panes, WorkbenchViewModel viewModel)
        {
            _panes = panes ?? throw new ArgumentNullException(nameof(panes));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        /// <summary>Pinned distances in the order they were placed.</summary>
        public IReadOnlyList<double> Pins => _pins;

        /// <summary>True while the mouse has snapped onto a pin (a click would remove it).</summary>
        public bool IsHoverOnPin => _hoverDistance is double hover && _pins.Contains(hover);

        /// <summary>Distance shown in the readout: the mouse while hovering, else the latest pin.</summary>
        public double? ActiveDistance => _hoverDistance ?? (_pins.Count > 0 ? _pins[^1] : null);

        /// <param name="snapRadiusMeters">A pin closer than this captures the cursor.</param>
        public void Hover(double distanceMeters, double snapRadiusMeters = 0)
        {
            double distance = Clamp(distanceMeters);
            int pin = NearestPin(distance, snapRadiusMeters);
            _hoverDistance = pin >= 0 ? _pins[pin] : distance;
            Refresh();
        }

        /// <summary>Mouse left the plots: only the pins remain.</summary>
        public void Leave()
        {
            _hoverDistance = null;
            Refresh();
        }

        /// <summary>Removes the pin under the cursor (within the snap radius), or drops a new one.</summary>
        public void Click(double distanceMeters, double snapRadiusMeters = 0)
        {
            double distance = Clamp(distanceMeters);
            int pin = NearestPin(distance, snapRadiusMeters);

            if (pin >= 0)
            {
                _pins.RemoveAt(pin);
                _hoverDistance = distance; // keep following the mouse, now unsnapped
            }
            else
            {
                _pins.Add(distance);
                _hoverDistance = distance; // the cursor now sits on the new pin
            }

            Refresh();
        }

        /// <summary>Removes the cursor and every pin (lap reset or a different reference lap).</summary>
        public void Clear()
        {
            _hoverDistance = null;
            _pins.Clear();
            Refresh();
        }

        /// <summary>
        /// Repositions the lines and dots and recomputes the readout. Called on every redraw
        /// so pin dots and readouts follow the live trace as the lap progresses.
        /// </summary>
        public void Refresh()
        {
            _panes.SetPins(_pins);

            ReferenceLapIndex? lap = _viewModel.ReferenceLap;
            if (ActiveDistance is not double distance || lap == null)
            {
                _panes.HideCursor();
                _viewModel.Cursor.Hide();
                return;
            }

            bool onPin = _pins.Contains(distance);

            // The dashed hover line only exists while the mouse is over a plot; pins draw their own lines
            if (_hoverDistance.HasValue) _panes.ShowCursor(distance, onPin);
            else _panes.HideCursor();

            LiveCursorSample? live = _panes.TryGetLiveAt(distance, out LiveCursorSample sample) ? sample : null;
            _viewModel.Cursor.Show(distance, onPin, lap.GetSampleAtDistance((float)distance), live);
        }

        private int NearestPin(double distance, double radius)
        {
            int nearest = -1;
            double best = double.MaxValue;
            for (int i = 0; i < _pins.Count; i++)
            {
                double gap = Math.Abs(_pins[i] - distance);
                if (gap <= radius && gap < best)
                {
                    best = gap;
                    nearest = i;
                }
            }
            return nearest;
        }

        private double Clamp(double distanceMeters)
        {
            double length = _viewModel.ReferenceLap?.TrackLengthMeters ?? 0;
            return Math.Clamp(distanceMeters, 0, Math.Max(0, length));
        }
    }
}
