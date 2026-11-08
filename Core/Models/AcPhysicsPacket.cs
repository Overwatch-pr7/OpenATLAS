using System;
using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Task 2.1: Telemetry Struct Definition
    // Assetto Corsa shared memory page "Local\acpmf_physics" (SPageFilePhysics).
    // Updated every physics step and describes the player's car.
    // The struct mirrors AC's C layout so the raw bytes can be copied straight into it without overhead.
    // It stops at IsAIControlled: the reference implementations disagree on the fields after that,
    // and reading only a prefix of the page is safe.

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct AcPhysicsPacket
    {
        public int PacketId;           // Increments every physics step
        public float Gas;              // Throttle pedal input (0.0 to 1.0)
        public float Brake;            // Brake pedal input (0.0 to 1.0)
        public float Fuel;             // Fuel in liters
        public int Gear;               // RAW AC gear: 0 = Reverse, 1 = Neutral, 2 = 1st ... (use DisplayGear)
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

        public int NumberOfTyresOut;   // Wheels off track (4 = whole car off)
        public int PitLimiterOn;
        public float Abs;

        // Later additions to the page
        public float KersCharge;
        public float KersInput;
        public int AutoShifterOn;
        public float RideHeightFront, RideHeightRear;
        public float TurboBoost;
        public float Ballast;
        public float AirDensity;

        public float AirTemp;          // °C
        public float RoadTemp;         // °C
        public float LocalAngularVelocityX, LocalAngularVelocityY, LocalAngularVelocityZ;
        public float FinalFF;          // Force feedback output

        public float PerformanceMeter; // AC's own delta vs your best lap (seconds)
        public int EngineBrake;
        public int ErsRecoveryLevel;
        public int ErsPowerLevel;
        public int ErsHeatCharging;
        public int ErsIsCharging;
        public float KersCurrentKJ;
        public int DrsAvailable;
        public int DrsEnabled;
        public float BrakeTempFL, BrakeTempFR, BrakeTempRL, BrakeTempRR;

        public float Clutch;
        public float TyreTempInnerFL, TyreTempInnerFR, TyreTempInnerRL, TyreTempInnerRR;
        public float TyreTempMiddleFL, TyreTempMiddleFR, TyreTempMiddleRL, TyreTempMiddleRR;
        public float TyreTempOuterFL, TyreTempOuterFR, TyreTempOuterRL, TyreTempOuterRR;

        public int IsAIControlled;

        // Gear in the usual convention: -1 = Reverse, 0 = Neutral, 1 = 1st ...
        // (matches the FastF1 reference laps, where 0 also means neutral)
        public readonly int DisplayGear => Gear - 1;
    }
}
