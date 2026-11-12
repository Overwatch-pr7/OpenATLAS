using System;
using System.Collections.Generic;
using OpenATLAS.Core.Analysis;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>Whether a value in the corner table is better, worse or no different for Racer 1.</summary>
    public enum CompareResult { Neutral, Better, Worse }

    /// <summary>
    /// One corner in the corner analysis table: Racer 1 against Racer 2 for time through the corner,
    /// minimum speed, braking point and throttle pick-up, plus the one thing to work on.
    /// </summary>
    public sealed class CornerRowViewModel
    {
        // Differences smaller than these are treated as equal
        public const double BrakeToleranceMeters = 15;
        public const double ThrottleToleranceMeters = 15;
        public const double SpeedToleranceKmh = 5;
        public const double TimeToleranceSeconds = 0.05;

        public string Corner { get; }
        public string Position { get; }

        public string TimeDelta { get; } = "--";
        public CompareResult TimeResult { get; }

        /// <summary>Time gained (+) or lost (-) through the corner; null until both laps have it.</summary>
        public double? TimeDeltaSeconds { get; }

        public string SpeedYou { get; }
        public string SpeedRef { get; }
        public string SpeedDiff { get; } = "";
        public CompareResult SpeedResult { get; }

        public string BrakeYou { get; }
        public string BrakeRef { get; }
        public string BrakeDiff { get; } = "";
        public CompareResult BrakeResult { get; }

        public string ThrottleYou { get; }
        public string ThrottleRef { get; }
        public string ThrottleDiff { get; } = "";
        public CompareResult ThrottleResult { get; }

        public string Focus { get; } = "";
        public CompareResult FocusResult { get; }

        /// <summary>Set for the corners where the most time is lost, so they stand out.</summary>
        public bool IsBiggestLoss { get; set; }

        public CornerRowViewModel(CornerZone zone, CornerMetrics you, CornerMetrics reference)
        {
            Corner = zone.Label;
            Position = $"{zone.ApexMeters:N0} m";

            // Time through the corner zone, with the app's sign convention: negative = Racer 1 slower
            if (you.ZoneSeconds is double youTime && reference.ZoneSeconds is double refTime)
            {
                double delta = refTime - youTime;
                TimeDeltaSeconds = delta;
                TimeDelta = $"{(delta >= 0 ? "+" : "-")}{Math.Abs(delta):F3}s";
                TimeResult = Compare(delta, TimeToleranceSeconds);
            }

            // Minimum speed: more is better
            SpeedYou = Speed(you);
            SpeedRef = Speed(reference);
            if (you.MinSpeedKmh is double youSpeed && reference.MinSpeedKmh is double refSpeed)
            {
                double diff = youSpeed - refSpeed;
                SpeedDiff = $"{(diff >= 0 ? "+" : "-")}{Math.Abs(diff):F0}";
                SpeedResult = Compare(diff, SpeedToleranceKmh);
            }

            // Braking point: braking earlier than the reference costs time
            BrakeYou = Brake(you);
            BrakeRef = Brake(reference);
            double? brakeEarly = null;
            if (you.BrakePointMeters is double youBrake && reference.BrakePointMeters is double refBrake)
            {
                brakeEarly = refBrake - youBrake;
                BrakeDiff = EarlyLate(brakeEarly.Value);
                BrakeResult = brakeEarly > BrakeToleranceMeters ? CompareResult.Worse : CompareResult.Neutral;
            }
            else if (you.HasData && reference.HasData && you.BrakePointMeters.HasValue != reference.BrakePointMeters.HasValue)
            {
                bool youBrakedOnly = you.BrakePointMeters.HasValue;
                BrakeDiff = youBrakedOnly ? "EXTRA" : "NONE";
                BrakeResult = youBrakedOnly ? CompareResult.Worse : CompareResult.Neutral;
            }

            // Throttle pick-up: getting back on the throttle later than the reference costs time
            ThrottleYou = Throttle(you);
            ThrottleRef = Throttle(reference);
            double? throttleLate = null;
            if (you.ThrottlePickUpMeters is double youPick && reference.ThrottlePickUpMeters is double refPick)
            {
                throttleLate = youPick - refPick;
                ThrottleDiff = EarlyLate(-throttleLate.Value);
                ThrottleResult = throttleLate > ThrottleToleranceMeters ? CompareResult.Worse : CompareResult.Neutral;
            }
            else if (you.HasData && reference.HasData && reference.IsFlatOut && !you.IsFlatOut)
            {
                ThrottleDiff = "LIFTED";
                ThrottleResult = CompareResult.Worse;
            }

            (Focus, FocusResult) = PickFocus(you, reference, brakeEarly, throttleLate);
        }

        // The single biggest thing to work on in this corner, judged against the tolerances
        private (string, CompareResult) PickFocus(CornerMetrics you, CornerMetrics reference, double? brakeEarly, double? throttleLate)
        {
            if (!you.HasData || !reference.HasData) return ("", CompareResult.Neutral);

            if (TimeDeltaSeconds is double delta && delta > -TimeToleranceSeconds)
            {
                return delta >= TimeToleranceSeconds ? ("GAINING TIME", CompareResult.Better) : ("ON PACE", CompareResult.Neutral);
            }

            double speedLoss = (reference.MinSpeedKmh ?? 0) - (you.MinSpeedKmh ?? 0);
            var issues = new List<(double Score, string Text)>();

            if (brakeEarly > BrakeToleranceMeters) issues.Add((brakeEarly.Value / BrakeToleranceMeters, "BRAKE LATER"));
            if (brakeEarly < -BrakeToleranceMeters && speedLoss > SpeedToleranceKmh)
                issues.Add((speedLoss / SpeedToleranceKmh + 0.5, "TOO DEEP ON ENTRY"));
            else if (speedLoss > SpeedToleranceKmh) issues.Add((speedLoss / SpeedToleranceKmh, "CARRY MORE SPEED"));
            if (throttleLate > ThrottleToleranceMeters) issues.Add((throttleLate.Value / ThrottleToleranceMeters, "EARLIER THROTTLE"));
            if (reference.IsFlatOut && !you.IsFlatOut) issues.Add((2, "TAKE IT FLAT"));
            else if (you.BrakePointMeters.HasValue && !reference.BrakePointMeters.HasValue) issues.Add((2, "LIFT, DON'T BRAKE"));

            if (issues.Count == 0) return ("LINE / EXIT", CompareResult.Worse);

            issues.Sort((a, b) => b.Score.CompareTo(a.Score));
            return (issues[0].Text, CompareResult.Worse);
        }

        private static CompareResult Compare(double diff, double tolerance) =>
            diff >= tolerance ? CompareResult.Better : diff <= -tolerance ? CompareResult.Worse : CompareResult.Neutral;

        private static string EarlyLate(double metresEarly)
        {
            double m = Math.Abs(metresEarly);
            if (m < 1) return "SAME";
            return $"{m:F0} {(metresEarly > 0 ? "EARLY" : "LATE")}";
        }

        private static string Speed(CornerMetrics m) => m.MinSpeedKmh is double v ? $"{v:F0}" : "--";

        private static string Brake(CornerMetrics m) =>
            !m.HasData ? "--" : m.BrakePointMeters is double d ? $"{d:N0}" : "NONE";

        private static string Throttle(CornerMetrics m) =>
            !m.HasData ? "--" : m.IsFlatOut ? "FLAT" : m.ThrottlePickUpMeters is double d ? $"{d:N0}" : "--";
    }
}
