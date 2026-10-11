using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Tests.Collections
{
    // Task 3.1: Unit Testing for Thread-Safe Circular Ring Buffer
    // Validates zero GC allocations, bounded capacity, FIFO overwriting, and thread safety.
    public class CircularBufferTests
    {
        [Fact]
        public void Constructor_InvalidCapacity_ThrowsException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(-5));
        }

        [Fact]
        public void InitialState_IsEmpty_ZeroCount()
        {
            var buffer = new CircularBuffer<int>(5);

            Assert.Equal(5, buffer.Capacity);
            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
            Assert.False(buffer.IsFull);
            Assert.False(buffer.TryPeek(out _));
            Assert.False(buffer.TryPeekLatest(out _));
            Assert.False(buffer.TryDequeue(out _));
        }

        [Fact]
        public void PushAndDequeue_FollowsFifoOrder()
        {
            var buffer = new CircularBuffer<int>(3);

            buffer.Push(10);
            buffer.Push(20);

            Assert.Equal(2, buffer.Count);
            Assert.True(buffer.TryPeek(out int peekOldest));
            Assert.Equal(10, peekOldest);

            Assert.True(buffer.TryPeekLatest(out int peekNewest));
            Assert.Equal(20, peekNewest);

            Assert.True(buffer.TryDequeue(out int val1));
            Assert.Equal(10, val1);
            Assert.Equal(1, buffer.Count);

            Assert.True(buffer.TryDequeue(out int val2));
            Assert.Equal(20, val2);
            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void OverwritingBehavior_WhenCapacityReached_EvictsOldestSample()
        {
            var buffer = new CircularBuffer<int>(3);

            buffer.Push(1);
            buffer.Push(2);
            buffer.Push(3);

            Assert.True(buffer.IsFull);
            Assert.Equal(3, buffer.Count);

            // Push 4: Overwrites 1
            buffer.Push(4);
            Assert.Equal(3, buffer.Count);
            Assert.True(buffer.IsFull);

            // Push 5: Overwrites 2
            buffer.Push(5);
            Assert.Equal(3, buffer.Count);

            // Elements in logical order should be: 3, 4, 5
            Assert.Equal(3, buffer[0]);
            Assert.Equal(4, buffer[1]);
            Assert.Equal(5, buffer[2]);

            Assert.True(buffer.TryPeek(out int oldest));
            Assert.Equal(3, oldest);

            Assert.True(buffer.TryPeekLatest(out int newest));
            Assert.Equal(5, newest);
        }

        [Fact]
        public void Indexer_OutOfBounds_ThrowsException()
        {
            var buffer = new CircularBuffer<int>(3);
            buffer.Push(10);

            Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
        }

        [Fact]
        public void CopyTo_Span_CorrectlyCopiesElementsInChronologicalOrder()
        {
            var buffer = new CircularBuffer<int>(4);
            buffer.Push(1);
            buffer.Push(2);
            buffer.Push(3);
            buffer.Push(4);
            buffer.Push(5); // Evicts 1, buffer contains [2, 3, 4, 5]
            buffer.Push(6); // Evicts 2, buffer contains [3, 4, 5, 6]

            Span<int> dest = stackalloc int[4];
            int copied = buffer.CopyTo(dest);

            Assert.Equal(4, copied);
            Assert.Equal(3, dest[0]);
            Assert.Equal(4, dest[1]);
            Assert.Equal(5, dest[2]);
            Assert.Equal(6, dest[3]);
        }

        [Fact]
        public void CopyLatest_Span_CopiesMostRecentElements()
        {
            var buffer = new CircularBuffer<int>(5);
            for (int i = 1; i <= 7; i++)
            {
                buffer.Push(i);
            }
            // Buffer has capacity 5, contains [3, 4, 5, 6, 7]

            Span<int> latestThree = stackalloc int[3];
            int copied = buffer.CopyLatest(latestThree);

            Assert.Equal(3, copied);
            Assert.Equal(5, latestThree[0]);
            Assert.Equal(6, latestThree[1]);
            Assert.Equal(7, latestThree[2]);
        }

        [Fact]
        public void Clear_ResetsCountAndState()
        {
            var buffer = new CircularBuffer<int>(3);
            buffer.Push(1);
            buffer.Push(2);

            buffer.Clear();

            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
            Assert.False(buffer.IsFull);
        }

        [Fact]
        public void ZeroAllocations_DuringPushAndCopyOperations()
        {
            // Verify that once initialized, hot loops allocate 0 bytes of GC memory.
            const int capacity = 1000;
            var buffer = new CircularBuffer<TelemetrySnapshot>(capacity);
            var preallocatedDest = new TelemetrySnapshot[100];

            // Warm up JIT and ensure all type metadata is loaded
            for (int i = 0; i < 50; i++)
            {
                buffer.Push(new TelemetrySnapshot(i * 0.016, i * 1.5f, 250f, 1f, 0f, 6, 11000, 0f));
                buffer.CopyLatest(preallocatedDest.AsSpan());
            }

            // Force GC collection and pause briefly for steady baseline
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long bytesAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            // Perform 10,000 pushes and 1,000 copy operations
            for (int i = 0; i < 10000; i++)
            {
                buffer.Push(new TelemetrySnapshot(i * 0.016, i * 1.5f, 250f, 1f, 0f, 6, 11000, 0f));
                if (i % 10 == 0)
                {
                    buffer.CopyLatest(preallocatedDest.AsSpan());
                }
            }

            long bytesAllocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAllocatedAfter - bytesAllocatedBefore;

            // Zero GC allocations target achieved!
            Assert.Equal(0, totalAllocated);
        }

        [Fact]
        public async Task MultiThreaded_ConcurrentProducerConsumer_ThreadSafe()
        {
            const int capacity = 500;
            var buffer = new CircularBuffer<TelemetrySnapshot>(capacity);
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));

            int totalWrites = 0;
            int totalReads = 0;

            // Producer Task (Simulates high-frequency physics loop)
            var producer = Task.Run(() =>
            {
                int counter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    counter++;
                    var snapshot = new TelemetrySnapshot(
                        timestampSeconds: counter * 0.003,
                        distanceMeters: counter * 0.5f,
                        speedKmh: 200f + (counter % 100),
                        throttle: 1.0f,
                        brake: 0.0f,
                        gear: 5,
                        rpms: 10500,
                        steerAngle: 0.0f
                    );
                    buffer.Push(snapshot);
                    Interlocked.Increment(ref totalWrites);
                }
            });

            // Consumer Task (Simulates 60Hz UI render loop pulling rolling snapshots)
            var consumer = Task.Run(() =>
            {
                var localWindow = new TelemetrySnapshot[60];
                while (!cts.Token.IsCancellationRequested)
                {
                    int copied = buffer.CopyLatest(localWindow.AsSpan());
                    if (copied > 0)
                    {
                        Interlocked.Increment(ref totalReads);
                    }
                    Thread.Sleep(1);
                }
            });

            await Task.WhenAll(producer, consumer);

            Assert.True(totalWrites > 1000, "Producer should have written thousands of samples.");
            Assert.True(totalReads > 20, "Consumer should have successfully read multiple snapshots.");
            Assert.Equal(capacity, buffer.Capacity);
            Assert.True(buffer.Count <= capacity);
        }
    }
}
