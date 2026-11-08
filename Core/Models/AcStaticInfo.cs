using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Assetto Corsa shared memory page "Local\acpmf_static" (SPageFileStatic).
    // Written once when a session loads: track, car, driver and car limits.
    // Field order and sizes must match AC exactly (see AcGraphicsPacket for how this was verified).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct AcStaticInfo
    {
        public AcText15 SmVersion;           // Shared memory version, e.g. "1.7"
        public AcText15 AcVersion;           // Game version

        // Session
        public int NumberOfSessions;
        public int NumCars;
        public AcText33 CarModel;            // Car folder name, e.g. "ks_ferrari_sf15t"
        public AcText33 Track;               // Track folder name, e.g. "monza"
        public AcText33 PlayerName;
        public AcText33 PlayerSurname;
        public AcText33 PlayerNick;
        public int SectorCount;

        // Car
        public float MaxTorque;
        public float MaxPower;
        public int MaxRpm;
        public float MaxFuel;
        public float SuspensionMaxTravelFL, SuspensionMaxTravelFR, SuspensionMaxTravelRL, SuspensionMaxTravelRR;
        public float TyreRadiusFL, TyreRadiusFR, TyreRadiusRL, TyreRadiusRR;

        // since AC 1.5
        public float MaxTurboBoost;
        public float Deprecated1;            // Air temp moved to the physics page in 1.6
        public float Deprecated2;            // Road temp moved to the physics page in 1.6
        public int PenaltiesEnabled;
        public float AidFuelRate;
        public float AidTireRate;
        public float AidMechanicalDamage;
        public int AidAllowTyreBlankets;
        public float AidStability;
        public int AidAutoClutch;
        public int AidAutoBlip;

        // since AC 1.7.1
        public int HasDrs;
        public int HasErs;
        public int HasKers;
        public float KersMaxJoules;
        public int EngineBrakeSettingsCount;
        public int ErsPowerControllerCount;

        // since AC 1.7.2
        public float TrackSplineLength;      // Track length in metres along AC's racing spline
        public AcText33 TrackConfiguration;  // Layout folder name, e.g. "gp" (empty for single-layout tracks)

        // since AC 1.10.2
        public float ErsMaxJoules;

        // since AC 1.13
        public int IsTimedRace;
        public int HasExtraLap;
        public AcText33 CarSkin;
        public int ReversedGridPositions;
        public int PitWindowStart;
        public int PitWindowEnd;
    }
}
