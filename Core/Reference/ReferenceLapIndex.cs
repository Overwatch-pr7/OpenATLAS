using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Core.Reference
{
    // Task 3.2: Reference Lap In-Memory Index
    // Reads reference lap telemetry directly from Parquet columns into parallel arrays.
    // Provides nanosecond O(1) distance lookups and sub-meter linear interpolation.
    public class ReferenceLapIndex
    {
        public int[] Distance { get; }
        public float[] Speed { get; }
        public sbyte[] Throttle { get; }
        public sbyte[] Brake { get; }
        public int[] Rpm { get; }
        public sbyte[] Gear { get; }
        public float[] Time { get; }

        public int SampleCount => Distance.Length;
        public int TrackLengthMeters => Distance.Length > 0 ? Distance[^1] : 0;
        public float TotalLapTimeSeconds => Time.Length > 0 ? Time[^1] - Time[0] : 0f;

        public ReferenceLapIndex(
            int[] distance,
            float[] speed,
            sbyte[] throttle,
            sbyte[] brake,
            int[] rpm,
            sbyte[] gear,
            float[] time)
        {
            ArgumentNullException.ThrowIfNull(distance);
            ArgumentNullException.ThrowIfNull(speed);
            ArgumentNullException.ThrowIfNull(throttle);
            ArgumentNullException.ThrowIfNull(brake);
            ArgumentNullException.ThrowIfNull(rpm);
            ArgumentNullException.ThrowIfNull(gear);
            ArgumentNullException.ThrowIfNull(time);

            if (distance.Length != speed.Length ||
                distance.Length != throttle.Length ||
                distance.Length != brake.Length ||
                distance.Length != rpm.Length ||
                distance.Length != gear.Length ||
                distance.Length != time.Length)
            {
                throw new ArgumentException("All parallel telemetry arrays must have identical lengths.");
            }

            Distance = distance;
            Speed = speed;
            Throttle = throttle;
            Brake = brake;
            Rpm = rpm;
            Gear = gear;
            Time = time;
        }

        // Calculates the array index and fractional alpha for continuous distance lookups.
        private void GetInterpolationParams(float distanceMeters, out int index, out float alpha)
        {
            if (SampleCount == 0)
            {
                index = 0;
                alpha = 0f;
                return;
            }

            if (distanceMeters <= 0f)
            {
                index = 0;
                alpha = 0f;
                return;
            }

            if (distanceMeters >= TrackLengthMeters)
            {
                index = SampleCount - 1;
                alpha = 0f;
                return;
            }

            index = (int)MathF.Floor(distanceMeters);

            if (index >= SampleCount - 1)
            {
                index = SampleCount - 1;
                alpha = 0f;
            }
            else
            {
                alpha = distanceMeters - index;
            }
        }

        // Returns reference elapsed time at a specific track distance in seconds.
        public float GetTimeAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return Time[index];
            }

            return Time[index] + alpha * (Time[index + 1] - Time[index]);
        }

        // Returns reference speed at a specific track distance in km/h.
        public float GetSpeedAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return Speed[index];
            }

            return Speed[index] + alpha * (Speed[index + 1] - Speed[index]);
        }

        // Returns reference throttle (0 to 100%) at a specific track distance.
        public float GetThrottleAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return Throttle[index];
            }

            return Throttle[index] + alpha * (Throttle[index + 1] - Throttle[index]);
        }

        // Returns reference brake (0 or 100%) at a specific track distance.
        public float GetBrakeAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return Brake[index];
            }

            return Brake[index] + alpha * (Brake[index + 1] - Brake[index]);
        }

        // Returns reference gear at a specific track distance.
        public int GetGearAtDistance(float distanceMeters)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (alpha >= 0.5f && index < SampleCount - 1)
            {
                return Gear[index + 1];
            }

            return Gear[index];
        }

        // Returns reference engine RPM at a specific track distance.
        public int GetRpmAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return Rpm[index];
            }

            return (int)MathF.Round(Rpm[index] + alpha * (Rpm[index + 1] - Rpm[index]));
        }

        // Returns a full reference sample snapshot at a specific track distance with zero heap allocation.
        public ReferenceSample GetSampleAtDistance(float distanceMeters, bool interpolate = true)
        {
            GetInterpolationParams(distanceMeters, out int index, out float alpha);

            if (!interpolate || alpha == 0f || index >= SampleCount - 1)
            {
                return new ReferenceSample(
                    distanceMeters: Distance[index],
                    speedKmh: Speed[index],
                    throttle: Throttle[index],
                    brake: Brake[index],
                    gear: Gear[index],
                    rpms: Rpm[index],
                    timeSeconds: Time[index]
                );
            }

            int next = index + 1;
            return new ReferenceSample(
                distanceMeters: distanceMeters,
                speedKmh: Speed[index] + alpha * (Speed[next] - Speed[index]),
                throttle: Throttle[index] + alpha * (Throttle[next] - Throttle[index]),
                brake: Brake[index] + alpha * (Brake[next] - Brake[index]),
                gear: alpha >= 0.5f ? Gear[next] : Gear[index],
                rpms: (int)MathF.Round(Rpm[index] + alpha * (Rpm[next] - Rpm[index])),
                timeSeconds: Time[index] + alpha * (Time[next] - Time[index])
            );
        }

        // Loads a reference lap directly from a Parquet file on disk into parallel arrays.
        public static async Task<ReferenceLapIndex> FromParquetFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Parquet reference file not found at: {filePath}", filePath);
            }

            await using var stream = File.OpenRead(filePath);
            return await FromStreamAsync(stream, cancellationToken);
        }

        // Synchronous convenience wrapper for Parquet loading.
        public static ReferenceLapIndex FromParquetFile(string filePath)
        {
            return Task.Run(() => FromParquetFileAsync(filePath)).GetAwaiter().GetResult();
        }

        // Loads a reference lap directly from an open Stream into parallel arrays.
        public static async Task<ReferenceLapIndex> FromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);

            await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);

            int totalRows = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                totalRows += (int)rg.RowCount;
            }

            if (totalRows == 0)
            {
                throw new InvalidDataException("Parquet file contains 0 rows.");
            }

            var distanceField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("Distance", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'Distance' column in Parquet schema.");

            var speedField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("Speed", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'Speed' column in Parquet schema.");

            var throttleField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("Throttle", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'Throttle' column in Parquet schema.");

            var brakeField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("Brake", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'Brake' column in Parquet schema.");

            var rpmField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("RPM", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'RPM' column in Parquet schema.");

            var gearField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("nGear", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("Gear", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'nGear' / 'Gear' column in Parquet schema.");

            var timeField = reader.Schema.DataFields.FirstOrDefault(f => f.Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Missing required 'Time' column in Parquet schema.");

            int[] distance = new int[totalRows];
            float[] speed = new float[totalRows];
            sbyte[] throttle = new sbyte[totalRows];
            sbyte[] brake = new sbyte[totalRows];
            int[] rpm = new int[totalRows];
            sbyte[] gear = new sbyte[totalRows];
            float[] time = new float[totalRows];

            int offset = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);

                using (var raw = (RawColumnData<int>)await rg.ReadRawColumnDataBaseAsync(distanceField, cancellationToken))
                    raw.Values.CopyTo(distance.AsSpan(offset));

                using (var raw = (RawColumnData<float>)await rg.ReadRawColumnDataBaseAsync(speedField, cancellationToken))
                    raw.Values.CopyTo(speed.AsSpan(offset));

                using (var raw = (RawColumnData<sbyte>)await rg.ReadRawColumnDataBaseAsync(throttleField, cancellationToken))
                    raw.Values.CopyTo(throttle.AsSpan(offset));

                using (var raw = (RawColumnData<sbyte>)await rg.ReadRawColumnDataBaseAsync(brakeField, cancellationToken))
                    raw.Values.CopyTo(brake.AsSpan(offset));

                using (var raw = (RawColumnData<int>)await rg.ReadRawColumnDataBaseAsync(rpmField, cancellationToken))
                    raw.Values.CopyTo(rpm.AsSpan(offset));

                using (var raw = (RawColumnData<sbyte>)await rg.ReadRawColumnDataBaseAsync(gearField, cancellationToken))
                    raw.Values.CopyTo(gear.AsSpan(offset));

                using (var raw = (RawColumnData<float>)await rg.ReadRawColumnDataBaseAsync(timeField, cancellationToken))
                    raw.Values.CopyTo(time.AsSpan(offset));

                offset += (int)rg.RowCount;
            }

            return new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);
        }
    }
}
