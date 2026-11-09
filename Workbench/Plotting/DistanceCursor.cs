using System;
using OpenATLAS.Core.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench.Plotting
{
    /// <summary>
    /// Distance cursor shared by the three trace panes (as in ATLAS / MoTeC).
    /// Hovering shows a dashed line that follows the mouse; a click pins it (solid line)
    /// so it stays put while the lap keeps playing; clicking again releases it.
    /// The header readouts show live vs reference values at the cursor's distance.
    /// Read-only: it never changes the recorded data.
    /// </summary>
    public sealed class DistanceCursor
    {
        private readonly TracePanes _panes;
        private readonly WorkbenchViewModel _viewModel;

        private double? _hoverDistance;
        private double? _pinnedDistance;

        public DistanceCursor(TracePanes panes, WorkbenchViewModel viewModel)
        {
            _panes = panes ?? throw new ArgumentNullException(nameof(panes));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public bool IsPinned => _pinnedDistance.HasValue;

        /// <summary>Distance the cursor is showing: the mouse position while hovering, else the pin.</summary>
        public double? ActiveDistance => _hoverDistance ?? _pinnedDistance;

        public void Hover(double distanceMeters)
        {
            _hoverDistance = Clamp(distanceMeters);
            Refresh();
        }

        /// <summary>Mouse left the plots: fall back to the pinned position, or hide.</summary>
        public void Leave()
        {
            _hoverDistance = null;
            Refresh();
        }

        /// <summary>Pins the cursor at the distance, or releases it if it is already pinned.</summary>
        public void Click(double distanceMeters)
        {
            _pinnedDistance = _pinnedDistance.HasValue ? null : Clamp(distanceMeters);
            Refresh();
        }

        /// <summary>Removes the cursor entirely (e.g. a different reference lap was loaded).</summary>
        public void Clear()
        {
            _hoverDistance = null;
            _pinnedDistance = null;
            Refresh();
        }

        /// <summary>
        /// Repositions the line and recomputes the readout. Called on every redraw so a
        /// pinned cursor's live values update as the lap progresses.
        /// </summary>
        public void Refresh()
        {
            ReferenceLapIndex? lap = _viewModel.ReferenceLap;
            if (ActiveDistance is not double distance || lap == null)
            {
                _panes.HideCursor();
                _viewModel.Cursor.Hide();
                return;
            }

            // Solid only when the line is actually sitting on the pin
            bool showingPin = _pinnedDistance.HasValue &&
                (_hoverDistance == null || Math.Abs(_hoverDistance.Value - _pinnedDistance.Value) < 0.5);

            _panes.ShowCursor(distance, showingPin);
            LiveCursorSample? live = _panes.TryGetLiveAt(distance, out LiveCursorSample sample) ? sample : null;
            _viewModel.Cursor.Show(distance, showingPin, lap.GetSampleAtDistance((float)distance), live);
        }

        private double Clamp(double distanceMeters)
        {
            double length = _viewModel.ReferenceLap?.TrackLengthMeters ?? 0;
            return Math.Clamp(distanceMeters, 0, Math.Max(0, length));
        }
    }
}
