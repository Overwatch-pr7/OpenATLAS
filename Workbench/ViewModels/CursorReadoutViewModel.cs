using OpenATLAS.Core.Models;
using OpenATLAS.Workbench.Plotting;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// Text shown in the trace pane headers for the distance cursor: the live and reference
    /// values at the cursor's distance. All strings are empty while the cursor is hidden.
    /// </summary>
    public class CursorReadoutViewModel : ViewModelBase
    {
        public const string NoLiveData = "--";

        private bool _isVisible;
        private bool _isPinned;
        private string _distanceText = "";
        private string _liveSpeedText = "";
        private string _refSpeedText = "";
        private string _livePedalText = "";
        private string _refPedalText = "";
        private string _deltaText = "";

        public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
        public bool IsPinned { get => _isPinned; private set => SetProperty(ref _isPinned, value); }

        /// <summary>e.g. "@ 1,250 m", with " PINNED" when the cursor is fixed in place.</summary>
        public string DistanceText { get => _distanceText; private set => SetProperty(ref _distanceText, value); }
        public string LiveSpeedText { get => _liveSpeedText; private set => SetProperty(ref _liveSpeedText, value); }
        public string RefSpeedText { get => _refSpeedText; private set => SetProperty(ref _refSpeedText, value); }
        public string LivePedalText { get => _livePedalText; private set => SetProperty(ref _livePedalText, value); }
        public string RefPedalText { get => _refPedalText; private set => SetProperty(ref _refPedalText, value); }
        public string DeltaText { get => _deltaText; private set => SetProperty(ref _deltaText, value); }

        /// <param name="live">Null when the current lap has not reached this distance yet.</param>
        public void Show(double distanceMeters, bool pinned, ReferenceSample reference, LiveCursorSample? live)
        {
            IsVisible = true;
            IsPinned = pinned;
            DistanceText = pinned ? $"@ {distanceMeters:N0} m PINNED" : $"@ {distanceMeters:N0} m";

            RefSpeedText = $"REF {reference.SpeedKmh:F1} km/h";
            RefPedalText = $"REF THR {reference.Throttle:F0}% BRK {reference.Brake:F0}%";

            if (live is LiveCursorSample l)
            {
                LiveSpeedText = $"LIVE {l.Speed:F1}";
                LivePedalText = $"LIVE THR {l.Throttle:F0}% BRK {l.Brake:F0}%";
                DeltaText = $"Δt {(l.DeltaSeconds >= 0 ? "+" : "-")}{System.Math.Abs(l.DeltaSeconds):F3}s";
            }
            else
            {
                LiveSpeedText = $"LIVE {NoLiveData}";
                LivePedalText = $"LIVE {NoLiveData}";
                DeltaText = $"Δt {NoLiveData}";
            }
        }

        public void Hide()
        {
            IsVisible = false;
            IsPinned = false;
            DistanceText = LiveSpeedText = RefSpeedText = LivePedalText = RefPedalText = DeltaText = "";
        }
    }
}
