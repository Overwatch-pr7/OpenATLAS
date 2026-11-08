using System;
using System.Runtime.CompilerServices;

namespace OpenATLAS.Core.Models
{
    // Assetto Corsa stores text in shared memory as fixed-size UTF-16 arrays (C's wchar_t[15] / wchar_t[33]).
    // These inline arrays reproduce that layout byte-for-byte while keeping the page structs free of
    // managed references, so a whole page can still be read straight from memory with zero allocations.

    [InlineArray(15)]
    public struct AcText15
    {
        private char _first;

        // Converts to a string, stopping at the first null terminator. Allocates, so call it for display only.
        public override readonly string ToString() => AcText.Decode(this);
    }

    [InlineArray(33)]
    public struct AcText33
    {
        private char _first;

        public override readonly string ToString() => AcText.Decode(this);
    }

    internal static class AcText
    {
        public static string Decode(ReadOnlySpan<char> chars)
        {
            int end = chars.IndexOf('\0');
            return new string(end >= 0 ? chars[..end] : chars);
        }
    }
}
