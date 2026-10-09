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
        public int PacketId;
        public float Gas;              // Throttle pedal input (0.0 to 1.0)
        public float Brake;            // Brake pedal input (0.0 to 1.0)
        public float Fuel;             // Fuel in liters
        public int Gear;               // Current gear (0 = N, 1+ = Forward, -1 = Reverse)
        public int Rpms;               // Engine RPM
        public float SteerAngle;       // Steering wheel angle
        public float SpeedKmh;         // Vehicle speed in km/h
        
        // 3D Vectors
        public float VelocityX, VelocityY, VelocityZ;
        public float AccGX, AccGY, AccGZ;
        
        // Arrays are unrolled to primitive fields to guarantee the struct remains a pure 
        // unmanaged value type. This is the secret to 0-allocation memory reading in C#!
        // Order: Front-Left (FL), Front-Right (FR), Rear-Left (RL), Rear-Right (RR)
        public float WheelSlipFL, WheelSlipFR, WheelSlipRL, WheelSlipRR;
        public float WheelLoadFL, WheelLoadFR, WheelLoadRL, WheelLoadRR;
        public float WheelsPressureFL, WheelsPressureFR, WheelsPressureRL, WheelsPressureRR;
        public float WheelAngularSpeedFL, WheelAngularSpeedFR, WheelAngularSpeedRL, WheelAngularSpeedRR;
        public float TyreWearFL, TyreWearFR, TyreWearRL, TyreWearRR;
        public float TyreDirtyLevelFL, TyreDirtyLevelFR, TyreDirtyLevelRL, TyreDirtyLevelRR;
        public float TyreCoreTemperatureFL, TyreCoreTemperatureFR, TyreCoreTemperatureRL, TyreCoreTemperatureRR;
        public float CamberRADFL, CamberRADFR, CamberRADRL, CamberRADRR;
        public float SuspensionTravelFL, SuspensionTravelFR, SuspensionTravelRL, SuspensionTravelRR;
        
        // Extras
        public float Drs;
        public float Tc;
        public float Heading;
        public float Pitch;
        public float Roll;
        public float CgHeight;
        
        // Damage (Front, Rear, Left, Right, Center)
        public float CarDamageFront, CarDamageRear, CarDamageLeft, CarDamageRight, CarDamageCenter;
        
        public int NumberOfTyresOut;
        public int PitLimiterOn;
        public float Abs;
    }
}
