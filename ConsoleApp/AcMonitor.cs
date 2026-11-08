using System;
using System.Diagnostics;
using System.Threading;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Network;

namespace OpenATLAS.ConsoleApp
{
    // Assetto Corsa Shared Memory Monitor
    // A live dashboard of everything OpenATLAS reads from AC's three shared memory pages.
    // Use it to confirm the app can see your game: run it, then drive in AC and watch the values change.
    //   dotnet run --project ConsoleApp -- --ac-monitor
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // Shared memory is Windows-only, like AC
    internal static class AcMonitor
    {
        private const int RefreshMs = 250; // Redraw 4 times a second so it stays readable

        public static void Run()
        {
            using var listener = new SharedMemoryListener();

            // Latest pages from the listener thread, guarded by a lock so we never print half an update
            object gate = new();
            AcPhysicsPacket physics = default;
            AcGraphicsPacket graphics = default;
            int samples = 0;

            listener.OnTelemetryUpdated += (p, g) =>
            {
                lock (gate)
                {
                    physics = p;
                    graphics = g;
                    samples++;
                }
            };
            listener.Start();

            Console.Clear();
            Console.CursorVisible = false;
            var rateClock = Stopwatch.StartNew();
            int samplesAtLastRate = 0;
            double sampleRate = 0;

            try
            {
                while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter))
                {
                    AcPhysicsPacket p;
                    AcGraphicsPacket g;
                    int count;
                    lock (gate) { p = physics; g = graphics; count = samples; }

                    // Samples per second, averaged over the last second
                    if (rateClock.ElapsedMilliseconds >= 1000)
                    {
                        sampleRate = (count - samplesAtLastRate) * 1000.0 / rateClock.ElapsedMilliseconds;
                        samplesAtLastRate = count;
                        rateClock.Restart();
                    }

                    Draw(listener.IsConnected, listener.StaticInfo, p, g, sampleRate);
                    Thread.Sleep(RefreshMs);
                }
            }
            finally
            {
                Console.CursorVisible = true;
                listener.Stop();
            }
        }

        private static void Draw(bool connected, AcStaticInfo s, AcPhysicsPacket p, AcGraphicsPacket g, double sampleRate)
        {
            Console.SetCursorPosition(0, 0);

            Line("=== OpenATLAS - Assetto Corsa Shared Memory Monitor ===   [ENTER] to exit");
            Line(connected
                ? $"Connection : CONNECTED   ({sampleRate:F0} new samples/s)"
                : "Connection : WAITING FOR AC (start the game and load a session)");
            Line("");

            // Static page: written once per session
            Line("--- acpmf_static (session) -------------------------------");
            Line($"AC version   : {s.AcVersion}   (shared memory {s.SmVersion})");
            Line($"Track        : {s.Track}   layout: {Show(s.TrackConfiguration.ToString())}");
            Line($"Track length : {s.TrackSplineLength:F1} m");
            Line($"Car          : {s.CarModel}   skin: {s.CarSkin}");
            Line($"Driver       : {s.PlayerName} {s.PlayerSurname}");
            Line($"Sectors      : {s.SectorCount}   Max RPM: {s.MaxRpm}");
            Line("");

            // Graphics page: lap and session state
            float lapDistance = g.NormalizedCarPosition * s.TrackSplineLength;
            Line("--- acpmf_graphics (lap & session) -----------------------");
            Line($"Status       : {g.Status,-7}   Session: {g.Session}   Flag: {g.Flag}");
            Line($"Laps done    : {g.CompletedLaps}   Position: {g.Position}   Sector: {g.CurrentSectorIndex + 1}");
            Line($"Lap time     : {FormatMs(g.CurrentTimeMs)}   last {FormatMs(g.LastTimeMs)}   best {FormatMs(g.BestTimeMs)}");
            Line($"Lap position : {g.NormalizedCarPosition:F4} of lap  = {lapDistance,7:F1} m");
            Line($"In pit box   : {YesNo(g.IsInPit)}   In pit lane: {YesNo(g.IsInPitLane)}");
            Line($"Tyres        : {Show(g.TyreCompound.ToString())}   Grip: {g.SurfaceGrip:F3}");
            Line($"Session dist : {g.DistanceTraveled:F0} m   Packet #{g.PacketId}");
            Line("");

            // Physics page: the car
            Line("--- acpmf_physics (car) ----------------------------------");
            Line($"Speed        : {p.SpeedKmh,6:F1} km/h   Gear: {GearText(p.DisplayGear)}   RPM: {p.Rpms}");
            Line($"Throttle     : {p.Gas * 100,5:F1} %   Brake: {p.Brake * 100,5:F1} %   Clutch: {p.Clutch * 100,5:F1} %");
            Line($"Steering     : {p.SteerAngle,6:F3}   Tyres off track: {p.NumberOfTyresOut}");
            Line($"AC delta     : {p.PerformanceMeter,6:F3} s vs your best (AC's own)");
            Line($"DRS          : available {YesNo(p.DrsAvailable)}  open {YesNo(p.DrsEnabled)}   Pit limiter: {YesNo(p.PitLimiterOn)}");
            Line($"Air / road   : {p.AirTemp:F1} / {p.RoadTemp:F1} C   Fuel: {p.Fuel:F1} L");
            Line($"Tyre temp    : FL {p.TyreCoreTemperatureFL:F0}  FR {p.TyreCoreTemperatureFR:F0}  RL {p.TyreCoreTemperatureRL:F0}  RR {p.TyreCoreTemperatureRR:F0} C");
            Line($"Packet       : #{p.PacketId}");
        }

        // Writes one line and clears the rest of it, so shorter text doesn't leave old characters behind
        private static void Line(string text)
        {
            int width = Math.Max(Console.WindowWidth - 1, 20);
            Console.WriteLine(text.Length >= width ? text[..width] : text.PadRight(width));
        }

        private static string FormatMs(int ms) =>
            ms <= 0 ? "--:--.---" : TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff");

        private static string YesNo(int flag) => flag != 0 ? "YES" : "no";

        private static string Show(string text) => string.IsNullOrEmpty(text) ? "-" : text;

        private static string GearText(int gear) => gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };
    }
}
