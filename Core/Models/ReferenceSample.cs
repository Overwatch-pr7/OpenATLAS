using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Task 3.2: Reference Lap Sample
    // An unmanaged sequential struct representing a single distance-indexed reference point.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct ReferenceSample
    {
        public readonly float DistanceMeters;
        public readonly float SpeedKmh;
        public readonly float Throttle;
        public readonly float Brake;
        public readonly int Gear;
        public readonly int Rpms;
        public readonly float TimeSeconds;

        public ReferenceSample(
            float distanceMeters,
            float speedKmh,
            float throttle,
            float brake,
            int gear,
            int rpms,
            float timeSeconds)
        {
            DistanceMeters = distanceMeters;
            SpeedKmh = speedKmh;
            Throttle = throttle;
            Brake = brake;
            Gear = gear;
            Rpms = rpms;
            TimeSeconds = timeSeconds;
        }
    }
}
