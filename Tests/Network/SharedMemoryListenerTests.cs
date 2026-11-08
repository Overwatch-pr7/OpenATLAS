using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Network;

namespace OpenATLAS.Tests.Network
{
    /// <summary>
    /// End-to-end listener tests without the game: each test creates its own fake
    /// "physics" / "graphics" / "static" pages, exactly as Assetto Corsa would.
    /// </summary>
    public class SharedMemoryListenerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        // Fake AC: three named memory pages under a unique prefix
        private sealed class FakeAc : IDisposable
        {
            public readonly string Prefix = $"OpenATLAS_Test_{Guid.NewGuid():N}_";
            private readonly MemoryMappedFile _physics, _graphics, _static;

            public FakeAc()
            {
                _physics = MemoryMappedFile.CreateNew(Prefix + "physics", 4096);
                _graphics = MemoryMappedFile.CreateNew(Prefix + "graphics", 4096);
                _static = MemoryMappedFile.CreateNew(Prefix + "static", 4096);
            }

            public void Write(AcPhysicsPacket p, AcGraphicsPacket g, AcStaticInfo s)
            {
                using (var a = _physics.CreateViewAccessor()) a.Write(0, ref p);
                using (var a = _graphics.CreateViewAccessor()) a.Write(0, ref g);
                using (var a = _static.CreateViewAccessor()) a.Write(0, ref s);
            }

            public void Dispose()
            {
                _physics.Dispose();
                _graphics.Dispose();
                _static.Dispose();
            }
        }

        private static (AcPhysicsPacket, AcGraphicsPacket, AcStaticInfo) SamplePages(int packetId)
        {
            var physics = new AcPhysicsPacket { PacketId = packetId, SpeedKmh = 287.5f, Gear = 7, Gas = 1f };
            var graphics = new AcGraphicsPacket
            {
                PacketId = packetId,
                Status = AcStatus.Live,
                CompletedLaps = 2,
                CurrentTimeMs = 41_234,
                NormalizedCarPosition = 0.5f,
                IsInPitLane = 0
            };
            var stat = new AcStaticInfo { TrackSplineLength = 5758f };
            "monza".AsSpan().CopyTo(stat.Track);
            "ks_ferrari_sf15t".AsSpan().CopyTo(stat.CarModel);
            return (physics, graphics, stat);
        }

        [Fact]
        public void ReadsAllThreePagesFromTheGame()
        {
            using var ac = new FakeAc();
            var (p, g, s) = SamplePages(packetId: 1);
            ac.Write(p, g, s);

            using var received = new ManualResetEventSlim();
            AcPhysicsPacket gotPhysics = default;
            AcGraphicsPacket gotGraphics = default;

            using var listener = new SharedMemoryListener(ac.Prefix);
            listener.OnTelemetryUpdated += (physics, graphics) =>
            {
                gotPhysics = physics;
                gotGraphics = graphics;
                received.Set();
            };
            listener.Start();

            Assert.True(received.Wait(Timeout), "Listener never raised OnTelemetryUpdated.");
            Assert.True(listener.IsConnected);

            Assert.Equal(287.5f, gotPhysics.SpeedKmh);
            Assert.Equal(6, gotPhysics.DisplayGear);
            Assert.Equal(AcStatus.Live, gotGraphics.Status);
            Assert.Equal(2, gotGraphics.CompletedLaps);
            Assert.Equal(41_234, gotGraphics.CurrentTimeMs);
            Assert.Equal(0.5f, gotGraphics.NormalizedCarPosition);

            Assert.Equal("monza", listener.StaticInfo.Track.ToString());
            Assert.Equal("ks_ferrari_sf15t", listener.StaticInfo.CarModel.ToString());
            Assert.Equal(5758f, listener.StaticInfo.TrackSplineLength);
        }

        [Fact]
        public void SkipsSamplesTheGameHasNotUpdated()
        {
            using var ac = new FakeAc();
            var (p, g, s) = SamplePages(packetId: 1);
            ac.Write(p, g, s);

            int events = 0;
            using var listener = new SharedMemoryListener(ac.Prefix);
            listener.OnTelemetryUpdated += (_, _) => Interlocked.Increment(ref events);
            listener.Start();

            // Same packet ids for ~10 polls: only the first read counts
            Thread.Sleep(200);
            Assert.Equal(1, Volatile.Read(ref events));

            // The game writes a new sample
            var (p2, g2, _) = SamplePages(packetId: 2);
            ac.Write(p2, g2, s);
            Thread.Sleep(200);
            Assert.Equal(2, Volatile.Read(ref events));
        }

        [Fact]
        public void WaitsForTheGameWhenItIsNotRunning()
        {
            using var listener = new SharedMemoryListener($"OpenATLAS_Missing_{Guid.NewGuid():N}_");
            listener.Start();

            Thread.Sleep(100);
            Assert.False(listener.IsConnected); // No exception, just waiting
        }
    }
}
