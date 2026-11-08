using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Core.Network
{
    // Task 2.2 & 2.3: Shared Memory Listener & Marshaling (Assetto Corsa)
    // Assetto Corsa publishes three shared memory pages while it runs:
    //   Local\acpmf_physics  - car physics, rewritten every physics step   -> AcPhysicsPacket
    //   Local\acpmf_graphics - lap/session state, rewritten every frame   -> AcGraphicsPacket
    //   Local\acpmf_static   - track/car/driver, written when a session loads -> AcStaticInfo
    //
    // This listener polls them on a background thread at ~60 Hz:
    //   * Waits (retrying every second) until AC is running, so it can be started before the game.
    //   * Reads each page straight into its struct (zero allocations per read).
    //   * Only raises OnTelemetryUpdated when AC has actually written new data (PacketId changed),
    //     so the same sample is never processed twice.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class SharedMemoryListener : IDisposable
    {
        // AC's page names are this prefix + "physics" / "graphics" / "static"
        public const string AcPageNamePrefix = "Local\\acpmf_";

        private const int PollIntervalMs = 16;        // ~60 Hz (needs the 1 ms timer resolution below)
        private const int ReconnectIntervalMs = 1000; // How often to look for AC while it isn't running
        private const int StaticRefreshPolls = 60;    // Re-read the static page about once a second

        private readonly string _physicsPageName;
        private readonly string _graphicsPageName;
        private readonly string _staticPageName;

        private readonly object _staticLock = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private AcStaticInfo _staticInfo;
        private volatile bool _isConnected;

        /// <param name="pageNamePrefix">Defaults to AC's names. Tests pass their own prefix to fake the game.</param>
        public SharedMemoryListener(string pageNamePrefix = AcPageNamePrefix)
        {
            _physicsPageName = pageNamePrefix + "physics";
            _graphicsPageName = pageNamePrefix + "graphics";
            _staticPageName = pageNamePrefix + "static";
        }

        // Raised on the background thread with the newest physics + graphics pages
        public event Action<AcPhysicsPacket, AcGraphicsPacket>? OnTelemetryUpdated;

        // Raised on the background thread when AC's shared memory is found (true),
        // and with false when the listener stops
        public event Action<bool>? OnConnectionChanged;

        public bool IsConnected => _isConnected;

        // Latest copy of the static page (track, car, driver). Default until connected.
        public AcStaticInfo StaticInfo
        {
            get { lock (_staticLock) { return _staticInfo; } }
        }

        public void Start()
        {
            if (_loopTask != null) return;

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loopTask = Task.Factory.StartNew(() => ListenLoop(token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ListenLoop(CancellationToken token)
        {
            // Windows rounds Thread.Sleep up to its ~15.6 ms clock tick, so Sleep(16) really sleeps
            // ~31 ms (measured: 32 polls/s). Asking for 1 ms timer resolution while we run fixes that
            // (measured: 60 polls/s). It is released again when the loop ends.
            TimerResolution.Begin();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (TryOpenPages(out var physics, out var graphics, out var stat))
                    {
                        using (physics) using (graphics) using (stat)
                        {
                            SetConnected(true);
                            PollPages(physics!, graphics!, stat!, token);
                        }
                        SetConnected(false);
                    }

                    // AC not running yet: wait and try again
                    token.WaitHandle.WaitOne(ReconnectIntervalMs);
                }
            }
            finally
            {
                TimerResolution.End();
            }
        }

        // Reads the pages until cancelled. Each read copies the bytes into a struct on the stack.
        private void PollPages(
            MemoryMappedViewAccessor physicsPage,
            MemoryMappedViewAccessor graphicsPage,
            MemoryMappedViewAccessor staticPage,
            CancellationToken token)
        {
            int lastPhysicsId = int.MinValue;
            int lastGraphicsId = int.MinValue;
            int pollCount = 0;

            while (!token.IsCancellationRequested)
            {
                if (pollCount++ % StaticRefreshPolls == 0)
                {
                    staticPage.Read(0, out AcStaticInfo stat);
                    lock (_staticLock) { _staticInfo = stat; }
                }

                physicsPage.Read(0, out AcPhysicsPacket physics);
                graphicsPage.Read(0, out AcGraphicsPacket graphics);

                // Skip if AC hasn't written anything new since the last poll (e.g. game paused)
                if (physics.PacketId != lastPhysicsId || graphics.PacketId != lastGraphicsId)
                {
                    lastPhysicsId = physics.PacketId;
                    lastGraphicsId = graphics.PacketId;
                    OnTelemetryUpdated?.Invoke(physics, graphics);
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        // Opens all three pages. Returns false (and opens nothing) if AC isn't running.
        private bool TryOpenPages(
            out MemoryMappedViewAccessor? physics,
            out MemoryMappedViewAccessor? graphics,
            out MemoryMappedViewAccessor? stat)
        {
            physics = graphics = stat = null;
            try
            {
                physics = OpenPage(_physicsPageName);
                graphics = OpenPage(_graphicsPageName);
                stat = OpenPage(_staticPageName);
                return true;
            }
            catch (FileNotFoundException)
            {
                // A page doesn't exist: AC isn't running (or no session has been loaded yet)
                physics?.Dispose();
                graphics?.Dispose();
                stat?.Dispose();
                physics = graphics = stat = null;
                return false;
            }
        }

        private static MemoryMappedViewAccessor OpenPage(string name)
        {
            // The accessor keeps the mapping alive, so the MemoryMappedFile handle can be closed right away
            using var file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }

        private void SetConnected(bool connected)
        {
            if (_isConnected == connected) return;
            _isConnected = connected;
            OnConnectionChanged?.Invoke(connected);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
            _loopTask = null;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _cts = null;
        }

        // Windows multimedia timer: raises the system timer resolution for this process.
        private static class TimerResolution
        {
            [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint periodMs);
            [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint periodMs);

            public static void Begin() => timeBeginPeriod(1);
            public static void End() => timeEndPeriod(1);
        }
    }
}
