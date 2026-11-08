using System.Runtime.CompilerServices;
using Xunit;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Tests.Models
{
    /// <summary>
    /// Locks the Assetto Corsa page structs to AC's C memory layout.
    /// Expected byte offsets were worked out by hand from AC's header (ints/floats are 4 bytes,
    /// wchar_t[15] is 30 bytes, wchar_t[33] is 66 bytes, next field aligned to 4).
    /// If any field is added, removed or resized by mistake, these tests fail instead of the
    /// app silently reading garbage from the game.
    /// </summary>
    public class AcSharedMemoryLayoutTests
    {
        // Byte offset of a field inside its struct
        private static int OffsetOf<TStruct, TField>(ref TStruct s, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<TStruct, byte>(ref s), ref Unsafe.As<TField, byte>(ref field));

        [Fact]
        public void Physics_LayoutMatchesAc()
        {
            var p = new AcPhysicsPacket();
            Assert.Equal(16, OffsetOf(ref p, ref p.Gear));
            Assert.Equal(28, OffsetOf(ref p, ref p.SpeedKmh));
            Assert.Equal(56, OffsetOf(ref p, ref p.WheelSlipFL));
            Assert.Equal(200, OffsetOf(ref p, ref p.Drs));
            Assert.Equal(244, OffsetOf(ref p, ref p.NumberOfTyresOut));
            Assert.Equal(252, OffsetOf(ref p, ref p.Abs));
            Assert.Equal(288, OffsetOf(ref p, ref p.AirTemp));
            Assert.Equal(312, OffsetOf(ref p, ref p.PerformanceMeter));
            Assert.Equal(348, OffsetOf(ref p, ref p.BrakeTempFL));
            Assert.Equal(364, OffsetOf(ref p, ref p.Clutch));
            Assert.Equal(416, OffsetOf(ref p, ref p.IsAIControlled));
            Assert.Equal(420, Unsafe.SizeOf<AcPhysicsPacket>());
        }

        [Fact]
        public void Graphics_LayoutMatchesAc()
        {
            var g = new AcGraphicsPacket();
            Assert.Equal(4, OffsetOf(ref g, ref g.Status));
            Assert.Equal(12, OffsetOf(ref g, ref g.CurrentTime));
            Assert.Equal(102, OffsetOf(ref g, ref g.Split));
            Assert.Equal(132, OffsetOf(ref g, ref g.CompletedLaps));
            Assert.Equal(140, OffsetOf(ref g, ref g.CurrentTimeMs));
            Assert.Equal(160, OffsetOf(ref g, ref g.IsInPit));
            Assert.Equal(176, OffsetOf(ref g, ref g.TyreCompound));
            Assert.Equal(244, OffsetOf(ref g, ref g.ReplayTimeMultiplier)); // 176 + 66 = 242, aligned to 244
            Assert.Equal(248, OffsetOf(ref g, ref g.NormalizedCarPosition));
            Assert.Equal(276, OffsetOf(ref g, ref g.IsInPitLane));
            Assert.Equal(284, OffsetOf(ref g, ref g.MandatoryPitDone));
            Assert.Equal(288, Unsafe.SizeOf<AcGraphicsPacket>());
        }

        [Fact]
        public void Static_LayoutMatchesAc()
        {
            var s = new AcStaticInfo();
            Assert.Equal(30, OffsetOf(ref s, ref s.AcVersion));
            Assert.Equal(68, OffsetOf(ref s, ref s.CarModel));
            Assert.Equal(134, OffsetOf(ref s, ref s.Track));
            Assert.Equal(400, OffsetOf(ref s, ref s.SectorCount));   // 332 + 66 = 398, aligned to 400
            Assert.Equal(412, OffsetOf(ref s, ref s.MaxRpm));
            Assert.Equal(520, OffsetOf(ref s, ref s.TrackSplineLength));
            Assert.Equal(524, OffsetOf(ref s, ref s.TrackConfiguration));
            Assert.Equal(592, OffsetOf(ref s, ref s.ErsMaxJoules));  // 524 + 66 = 590, aligned to 592
            Assert.Equal(604, OffsetOf(ref s, ref s.CarSkin));
            Assert.Equal(680, OffsetOf(ref s, ref s.PitWindowEnd));
            Assert.Equal(684, Unsafe.SizeOf<AcStaticInfo>());
        }

        [Fact]
        public void AcText_StopsAtNullTerminator()
        {
            var text = new AcText33();
            "monza".AsSpan().CopyTo(text);
            Assert.Equal("monza", text.ToString());
            Assert.Equal(string.Empty, new AcText15().ToString());
        }

        [Fact]
        public void DisplayGear_ConvertsAcGearNumbering()
        {
            Assert.Equal(-1, new AcPhysicsPacket { Gear = 0 }.DisplayGear); // Reverse
            Assert.Equal(0, new AcPhysicsPacket { Gear = 1 }.DisplayGear);  // Neutral
            Assert.Equal(1, new AcPhysicsPacket { Gear = 2 }.DisplayGear);  // 1st
        }
    }
}
