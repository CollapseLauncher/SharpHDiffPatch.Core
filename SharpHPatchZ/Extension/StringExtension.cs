using System;

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
}
