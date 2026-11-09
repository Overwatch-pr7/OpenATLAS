using System;
using System.Collections.Generic;
using System.Linq;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// One row of the TRACK SECTOR ANALYSIS panel: where the sector is, how long the reference
    /// driver took through it, and the live time gained or lost in it on the current lap.
    /// </summary>
    public class SectorViewModel : ViewModelBase
    {
        private string _deltaString = "--";
        private bool _hasDelta;
        private bool _isAhead;
        private bool _isActive;

        public int Number { get; }
        public float StartMeters { get; }
        public float EndMeters { get; }

        /// <summary>"SECTOR 1", or "SECTOR 1: PRIMA VARIANTE &amp; ROGGIA" when track.json has corner names.</summary>
        public string Title { get; }

        /// <summary>Corners and distance range, e.g. "T1-T3 | 0 - 1,894 m".</summary>
        public string Detail { get; }

        /// <summary>Reference driver's time through this sector, e.g. "REF 26.680s".</summary>
        public string ReferenceTimeString { get; }

        public SectorViewModel(TrackSector sector, IReadOnlyList<TrackCorner> corners, float referenceSectorSeconds)
        {
            Number = sector.Number;
            StartMeters = sector.StartMeters;
            EndMeters = sector.EndMeters;

            var names = corners.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name.ToUpperInvariant()).ToList();
            Title = names.Count > 0 ? $"SECTOR {Number}: {string.Join(" & ", names)}" : $"SECTOR {Number}";

            string range = $"{StartMeters:N0} - {EndMeters:N0} m";
            Detail = corners.Count switch
            {
                0 => range,
                1 => $"{corners[0].Label} | {range}",
                _ => $"{corners[0].Label}-{corners[^1].Label} | {range}"
            };

            ReferenceTimeString = $"REF {referenceSectorSeconds:F3}s";
        }

        /// <summary>Live time gained (+) or lost (-) in this sector; "--" until the car reaches it.</summary>
        public string DeltaString
        {
            get => _deltaString;
            private set => SetProperty(ref _deltaString, value);
        }

        public bool HasDelta
        {
            get => _hasDelta;
            private set => SetProperty(ref _hasDelta, value);
        }

        public bool IsAhead
        {
            get => _isAhead;
            private set => SetProperty(ref _isAhead, value);
        }

        /// <summary>True while the car is in this sector.</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        public bool ContainsDistance(float distanceMeters) => distanceMeters >= StartMeters && distanceMeters < EndMeters;

        public void SetDelta(float deltaSeconds)
        {
            IsAhead = deltaSeconds >= 0f;
            DeltaString = $"{(IsAhead ? "+" : "-")}{MathF.Abs(deltaSeconds):F3}s";
            HasDelta = true;
        }

        public void ClearDelta()
        {
            HasDelta = false;
            IsAhead = false;
            IsActive = false;
            DeltaString = "--";
        }
    }
}
