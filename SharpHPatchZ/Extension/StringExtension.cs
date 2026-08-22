using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpHPatchZ.Extension;

internal static class StringExtension
{
    public static int GetSplits(
        this ReadOnlySpan<char> span,
        Span<Range>             ranges,
        char                    separator,
        StringSplitOptions      splitOptions = StringSplitOptions.None)
    {
#if NET8_0_OR_GREATER
        return span.Split(ranges,
                          separator,
                          splitOptions);
#else
        if (ranges.Length == 0)
        {
            return 0;
        }

#if NET6_0_OR_GREATER
        bool isTrimEntries = splitOptions.HasFlag(StringSplitOptions.TrimEntries);
#else
        bool isTrimEntries = false;
#endif
        bool isRemoveEmpty = splitOptions.HasFlag(StringSplitOptions.RemoveEmptyEntries);

        int rangeCount     = 0;
        int startInclusive = 0;

        if (ranges.Length > 1)
        {
            while (true)
            {
                int separatorOffset = span[startInclusive..].IndexOf(separator);
                if (separatorOffset < 0)
                {
                    break;
                }

                int endExclusive          = startInclusive + separatorOffset;
                int untrimmedEndExclusive = endExclusive;

                if (isTrimEntries)
                {
                    TrimSplitEntry(span, ref startInclusive, ref endExclusive);
                }

                if (isRemoveEmpty &&
                    startInclusive == endExclusive)
                {
                    continue;
                }

                if (rangeCount >= ranges.Length - 1)
                {
                    break;
                }

                ranges[rangeCount++] = new Range(startInclusive, endExclusive);

                startInclusive = untrimmedEndExclusive + 1;
            }
        }

        int remainderEndExclusive = span.Length;
        if (isTrimEntries)
        {
            TrimSplitEntry(span, ref startInclusive, ref remainderEndExclusive);
        }
        if (startInclusive != remainderEndExclusive)
        {
            ranges[rangeCount++] = new Range(startInclusive, remainderEndExclusive);
        }

        return rangeCount;
#endif
    }

#if !NET8_0_OR_GREATER
    private static void TrimSplitEntry(
        ReadOnlySpan<char> source,
        ref int            startInclusive,
        ref int            endExclusive)
    {
        while (startInclusive < endExclusive && char.IsWhiteSpace(source[startInclusive]))
        {
            startInclusive++;
        }

        while (endExclusive > startInclusive && char.IsWhiteSpace(source[endExclusive - 1]))
        {
            endExclusive--;
        }
    }
#endif

#if NET6_0_OR_GREATER
    public static unsafe NativeStringEncoding GuessStringEncoding(
        void* stringP,
        nuint maxBytes)
    {
        byte* ptr = (byte*)stringP;

        if (ptr == null)
            throw new ArgumentNullException(nameof(ptr));

        if (maxBytes >= 2)
        {
            // UTF-16 LE BOM
            if (ptr[0] == 0xFF && ptr[1] == 0xFE)
                return NativeStringEncoding.Unicode;

            // UTF-8 BOM
            if (maxBytes >= 3 &&
                ptr[0] == 0xEF &&
                ptr[1] == 0xBB &&
                ptr[2] == 0xBF)
            {
                return NativeStringEncoding.Utf8;
            }
        }

        // Heuristic:
        // ASCII text encoded as UTF-16LE tends to have NUL bytes
        // in every odd byte.
        nuint pairs         = 0;
        nuint zeroHighBytes = 0;

        nuint checkLength = Math.Min(maxBytes, 64u);

        for (nuint i = 0; i + 1 < checkLength; i += 2)
        {
            byte lo = ptr[i];
            byte hi = ptr[i + 1];

            // UTF-16 terminator
            if (lo == 0 && hi == 0)
                break;

            pairs++;

            if (hi == 0)
                zeroHighBytes++;
        }

        if (pairs != 0 &&
            zeroHighBytes * 4 >= pairs * 3) // >= 75%
        {
            return NativeStringEncoding.Unicode;
        }

        return NativeStringEncoding.Utf8;
    }

    public static unsafe string? GetManagedStringAuto(void* ptr)
    {
        if (ptr == null) return null;

        NativeStringEncoding encoding = GuessStringEncoding(ptr, 8);
        return encoding switch
        {
            NativeStringEncoding.Unicode => MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)ptr).ToString(),
            NativeStringEncoding.Utf8 => Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)ptr)),
            _ => null
        };
    }
#endif
}

public enum NativeStringEncoding
{
    Utf8,
    Unicode
}
