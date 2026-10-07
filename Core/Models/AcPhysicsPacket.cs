using System;
using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Task 2.1: Telemetry Struct Definition
    // We define this explicitly so we can safely cast the raw bytes received from 
    // the simulator's UDP socket (or shared memory) directly into this object without overhead.
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct AcPhysicsPacket
    {
        // Speed & Position
        public float SpeedKmh;
        public float DistanceTraveled; // Distance from the start/finish line in meters
        
        // Driver Inputs
        public float Gas;              // Throttle pedal input (0.0 to 1.0)
        public float Brake;            // Brake pedal input (0.0 to 1.0)
        public float SteerAngle;       // Steering wheel angle
        
        // Engine & Transmission
        public int Gear;               // Current gear (0 = N, 1+ = Forward, -1 = Reverse)
        public int Rpms;               // Engine RPM
        
        // Note: The game broadcasts a full 256-byte packet. 
        // When marshaling, the CLR will seamlessly map the first 28 bytes to our fields above
        // and ignore the remaining bytes (e.g. suspension travel, tire temps) since we don't need them yet.
    }
}
