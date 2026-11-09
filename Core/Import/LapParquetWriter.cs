using System;
using System.IO;
using System.Threading.Tasks;
using OpenATLAS.Core.Reference;
using Parquet;
using Parquet.Schema;

namespace OpenATLAS.Core.Import
{
    // Writes a lap in the same Parquet layout extract_reference.py produces (same column names,
    // types and non-nullable schema), so user laps and F1 reference laps are interchangeable.
    public static class LapParquetWriter
    {
        private static readonly DataField<int> DistanceField = new("Distance");
        private static readonly DataField<float> SpeedField = new("Speed");
        private static readonly DataField<sbyte> ThrottleField = new("Throttle");
        private static readonly DataField<sbyte> BrakeField = new("Brake");
        private static readonly DataField<int> RpmField = new("RPM");
        private static readonly DataField<sbyte> GearField = new("nGear");
        private static readonly DataField<float> TimeField = new("Time");

        private static readonly ParquetSchema Schema =
            new(DistanceField, SpeedField, ThrottleField, BrakeField, RpmField, GearField, TimeField);

        public static async Task WriteAsync(ReferenceLapIndex lap, string filePath)
        {
            ArgumentNullException.ThrowIfNull(lap);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

            await using var stream = File.Create(filePath);
            await using var writer = await ParquetWriter.CreateAsync(Schema, stream);
            using ParquetRowGroupWriter group = writer.CreateRowGroup();

            await group.WriteAsync<int>(DistanceField, lap.Distance);
            await group.WriteAsync<float>(SpeedField, lap.Speed);
            await group.WriteAsync<sbyte>(ThrottleField, lap.Throttle);
            await group.WriteAsync<sbyte>(BrakeField, lap.Brake);
            await group.WriteAsync<int>(RpmField, lap.Rpm);
            await group.WriteAsync<sbyte>(GearField, lap.Gear);
            await group.WriteAsync<float>(TimeField, lap.Time);
        }

        public static void Write(ReferenceLapIndex lap, string filePath) =>
            Task.Run(() => WriteAsync(lap, filePath)).GetAwaiter().GetResult();
    }
}
