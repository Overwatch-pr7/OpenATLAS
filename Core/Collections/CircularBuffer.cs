using System;
using System.Runtime.CompilerServices;

namespace OpenATLAS.Core.Collections
{
    // Task 3.1: Thread-Safe Circular Ring Buffer
    // Pre-allocates backing storage upfront to ensure zero GC allocations during steady-state ingestion.
    // Provides synchronized thread-safe reads and writes to eliminate UI stutter and prevent memory explosions.
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private readonly object _syncLock = new();

        private int _start;
        private int _count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
            }

            _capacity = capacity;
            _buffer = new T[capacity];
            _start = 0;
            _count = 0;
        }

        public int Capacity => _capacity;

        public int Count
        {
            get
            {
                lock (_syncLock)
                {
                    return _count;
                }
            }
        }

        public bool IsFull
        {
            get
            {
                lock (_syncLock)
                {
                    return _count == _capacity;
                }
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_syncLock)
                {
                    return _count == 0;
                }
            }
        }

        // Pushes a new item into the circular buffer.
        // If the buffer is full, the oldest item is overwritten automatically.
        // Zero GC allocation.
        public void Push(T item)
        {
            lock (_syncLock)
            {
                if (_count < _capacity)
                {
                    int writeIndex = (_start + _count) % _capacity;
                    _buffer[writeIndex] = item;
                    _count++;
                }
                else
                {
                    _buffer[_start] = item;
                    _start = (_start + 1) % _capacity;
                }
            }
        }

        // Tries to dequeue the oldest item from the buffer (FIFO).
        // Zero GC allocation.
        public bool TryDequeue(out T item)
        {
            lock (_syncLock)
            {
                if (_count == 0)
                {
                    item = default!;
                    return false;
                }

                item = _buffer[_start];

                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    _buffer[_start] = default!;
                }

                _start = (_start + 1) % _capacity;
                _count--;
                return true;
            }
        }

        // Peeks at the oldest element without removing it.
        public bool TryPeek(out T item)
        {
            lock (_syncLock)
            {
                if (_count == 0)
                {
                    item = default!;
                    return false;
                }

                item = _buffer[_start];
                return true;
            }
        }

        // Peeks at the newest (most recently pushed) element without removing it.
        public bool TryPeekLatest(out T item)
        {
            lock (_syncLock)
            {
                if (_count == 0)
                {
                    item = default!;
                    return false;
                }

                int latestIndex = (_start + _count - 1) % _capacity;
                item = _buffer[latestIndex];
                return true;
            }
        }

        // Direct indexer from 0 (oldest) to Count - 1 (newest).
        public T this[int index]
        {
            get
            {
                lock (_syncLock)
                {
                    if (index < 0 || index >= _count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index), $"Index must be between 0 and {_count - 1}.");
                    }

                    int actualIndex = (_start + index) % _capacity;
                    return _buffer[actualIndex];
                }
            }
        }

        // Copies items in chronological order (oldest to newest) into a destination Span.
        // Caller provides the destination buffer, guaranteeing zero GC allocations.
        // Returns the number of elements copied.
        public int CopyTo(Span<T> destination)
        {
            lock (_syncLock)
            {
                int itemsToCopy = Math.Min(destination.Length, _count);
                if (itemsToCopy == 0)
                {
                    return 0;
                }

                int len1 = Math.Min(itemsToCopy, _capacity - _start);
                _buffer.AsSpan(_start, len1).CopyTo(destination[..len1]);

                int len2 = itemsToCopy - len1;
                if (len2 > 0)
                {
                    _buffer.AsSpan(0, len2).CopyTo(destination.Slice(len1, len2));
                }

                return itemsToCopy;
            }
        }

        // Overload to copy items into a pre-allocated array.
        public int CopyTo(T[] destination, int destinationIndex = 0)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (destinationIndex < 0 || destinationIndex > destination.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationIndex));
            }

            return CopyTo(destination.AsSpan(destinationIndex));
        }

        // Copies the newest items (up to destination.Length) in chronological order.
        // Allows high-frequency UI render loops to fetch the latest rolling window with zero heap allocations.
        public int CopyLatest(Span<T> destination)
        {
            lock (_syncLock)
            {
                int itemsToCopy = Math.Min(destination.Length, _count);
                if (itemsToCopy == 0)
                {
                    return 0;
                }

                int startLogical = _count - itemsToCopy;
                int startPhysical = (_start + startLogical) % _capacity;

                int len1 = Math.Min(itemsToCopy, _capacity - startPhysical);
                _buffer.AsSpan(startPhysical, len1).CopyTo(destination[..len1]);

                int len2 = itemsToCopy - len1;
                if (len2 > 0)
                {
                    _buffer.AsSpan(0, len2).CopyTo(destination.Slice(len1, len2));
                }

                return itemsToCopy;
            }
        }

        // Overload to copy newest items into a pre-allocated array.
        public int CopyLatest(T[] destination, int destinationIndex = 0)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (destinationIndex < 0 || destinationIndex > destination.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationIndex));
            }

            return CopyLatest(destination.AsSpan(destinationIndex));
        }

        // Copies all elements to a new array. Allocates memory; intended for diagnostics/testing.
        public T[] ToArray()
        {
            lock (_syncLock)
            {
                var array = new T[_count];
                CopyTo(array.AsSpan());
                return array;
            }
        }

        // Clears all elements from the buffer.
        public void Clear()
        {
            lock (_syncLock)
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    Array.Clear(_buffer, 0, _capacity);
                }

                _start = 0;
                _count = 0;
            }
        }
    }
}
