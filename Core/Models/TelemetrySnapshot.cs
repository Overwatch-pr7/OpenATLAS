using System;
using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Task 3.1: Normalized Telemetry Snapshot
    // Defined as an unmanaged sequential struct to ensure inline contiguous storage
    // in pre-allocated buffers, achieving 0 GC heap allocations.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TelemetrySnapshot
    {
        public double TimestampSeconds; // Elapsed session/lap time in seconds
        public float DistanceMeters;     // Track distance in meters (resampled / calculated)
        public float SpeedKmh;           // Vehicle speed in km/h
        public float Throttle;           // Throttle input (0.0 to 1.0)
        public float Brake;              // Brake input (0.0 to 1.0)
        public int Gear;                 // Current gear
        public int Rpms;                 // Engine RPM
        public float SteerAngle;         // Steering wheel angle in degrees/radians
        public float DeltaTime;          // Delta time vs reference lap in seconds (calculated by Delta Engine)

        public TelemetrySnapshot(
            double timestampSeconds,
            float distanceMeters,
            float speedKmh,
            float throttle,
            float brake,
            int gear,
            int rpms,
            float steerAngle = 0f,
            float deltaTime = 0f)
        {
            TimestampSeconds = timestampSeconds;
            DistanceMeters = distanceMeters;
            SpeedKmh = speedKmh;
            Throttle = throttle;
            Brake = brake;
            Gear = gear;
            Rpms = rpms;
            SteerAngle = steerAngle;
            DeltaTime = deltaTime;
        }

        // Convenience factory to convert from raw simulator physics packet
        public static TelemetrySnapshot FromPhysicsPacket(AcPhysicsPacket packet, float distanceMeters, double timestampSeconds, float deltaTime = 0f)
        {
            return new TelemetrySnapshot(
                timestampSeconds: timestampSeconds,
                distanceMeters: distanceMeters,
                speedKmh: packet.SpeedKmh,
                throttle: packet.Gas,
                brake: packet.Brake,
                gear: packet.Gear,
                rpms: packet.Rpms,
                steerAngle: packet.SteerAngle,
                deltaTime: deltaTime
            );
        }
    }
}
