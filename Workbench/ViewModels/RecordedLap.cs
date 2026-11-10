using System;
using System.Collections.Generic;
using OpenATLAS.Core.Import;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// A complete live AC lap captured while REC was on, held in memory until the user saves it
    /// (SAVE / File > Save Recorded Laps) or discards it.
    /// </summary>
    public sealed record RecordedLap(int Number, TimeSpan LapTime, string GrandPrix, DateTime RecordedAt,
                                     IReadOnlyList<RawLapSample> Samples)
    {
        public string Label => $"LAP {Number}   {LapTime:m\\:ss\\.fff}   {GrandPrix}";

        /// <summary>Suggested file name, e.g. "AC 2026-11-10 14.30 1.21.345".</summary>
        public string DefaultName => $"AC {RecordedAt:yyyy-MM-dd HH.mm} {LapTime:m\\.ss\\.fff}";
    }
}
