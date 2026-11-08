using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Game state reported in the graphics page.
    public enum AcStatus
    {
        Off = 0,      // No session loaded (menus)
        Replay = 1,
        Live = 2,     // Driving
        Pause = 3
    }

    public enum AcSessionType
    {
        Unknown = -1,
        Practice = 0,
        Qualify = 1,
        Race = 2,
        Hotlap = 3,
        TimeAttack = 4,
        Drift = 5,
        Drag = 6
    }

    public enum AcFlagType
    {
        None = 0,
        Blue = 1,
        Yellow = 2,
        Black = 3,
        White = 4,
        Checkered = 5,
        Penalty = 6
    }

    // Assetto Corsa shared memory page "Local\acpmf_graphics" (SPageFileGraphic).
    // Updated roughly once per rendered frame. Holds session/lap state rather than car physics.
    // Field order and sizes must match AC exactly: every field after a mistake would read garbage.
    // Layout verified against two independent implementations (Crew Chief's ACSData.cs and
    // mdjarv/assettocorsasharedmemory) and locked down by Tests/Models/AcSharedMemoryLayoutTests.cs.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct AcGraphicsPacket
    {
        public int PacketId;                 // Increments every time AC writes this page
        public AcStatus Status;              // Off / Replay / Live / Pause
        public AcSessionType Session;        // Practice / Qualify / Race / Hotlap ...

        // Lap times as display text, e.g. "1:23:456"
        public AcText15 CurrentTime;
        public AcText15 LastTime;
        public AcText15 BestTime;
        public AcText15 Split;

        public int CompletedLaps;            // Increments when the car crosses the start/finish line
        public int Position;                 // Race position
        public int CurrentTimeMs;            // Current lap time in milliseconds (AC's own timer; stops when paused)
        public int LastTimeMs;               // Previous lap time in ms
        public int BestTimeMs;               // Best lap time in ms
        public float SessionTimeLeft;        // ms
        public float DistanceTraveled;       // Total metres driven this session
        public int IsInPit;                  // 1 when stopped in the pit box
        public int CurrentSectorIndex;
        public int LastSectorTime;           // ms
        public int NumberOfLaps;             // Session lap count (0 for timed sessions)
        public AcText33 TyreCompound;

        public float ReplayTimeMultiplier;
        public float NormalizedCarPosition;  // 0.0 to 1.0 around the lap; 0 = start/finish line
        public float CarCoordinateX, CarCoordinateY, CarCoordinateZ; // World position (m)

        public float PenaltyTime;
        public AcFlagType Flag;
        public int IdealLineOn;

        // since AC 1.5
        public int IsInPitLane;              // 1 anywhere inside the pit lane
        public float SurfaceGrip;

        // since AC 1.13
        public int MandatoryPitDone;
    }
}
