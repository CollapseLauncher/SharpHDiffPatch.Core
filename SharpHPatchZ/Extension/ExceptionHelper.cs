using System;
#if NET8_0_OR_GREATER
using System.Collections.Generic;
#endif

namespace SharpHPatchZ.Extension;

file static class Const
{
#if NET8_0_OR_GREATER
    internal static readonly Dictionary<string, int> ExceptionToReturnCodeMap = new()
    {
        { HDiffHeaderSignatureEmptyOrUnreadable, 0b_00000000_00000000_00000000_00000010 },
        { HDiffHeaderMagicNotSupported,          0b_00000000_00000000_00000000_00000100 },
        { HDiffHeaderCompressionNotSupported,    0b_00000000_00000000_00000000_00001000 },
        { HDiffHeaderChecksumNotSupported,       0b_00000000_00000000_00000000_00010000 }
    };
#endif

    public const string HDiffHeaderSignatureEmptyOrUnreadable = "HDIFF_HeaderSignatureEmptyOrUnreadable";
    public const string HDiffHeaderMagicNotSupported          = "HDIFF_HeaderMagicNotSupported";
    public const string HDiffHeaderCompressionNotSupported    = "HDIFF_HeaderCompressionNotSupported";
    public const string HDiffHeaderChecksumNotSupported       = "HDIFF_HeaderChecksumNotSupported";
}

public static class ExceptionHelper
{
    public static void ThrowHDiffHeaderSignatureEmptyOrUnreadable()
        => throw new InvalidOperationException($"[{Const.HDiffHeaderSignatureEmptyOrUnreadable}] Header signature is empty or unreadable!");
    public static void ThrowHdiffHeaderMagicNotSupported(ReadOnlySpan<char> magic)
        => throw new NotSupportedException($"[{Const.HDiffHeaderMagicNotSupported}] Header magic: {magic.ToString()} is not supported!");
    public static void ThrowHdiffHeaderCompressionNotSupported(ReadOnlySpan<char> enumString)
        => throw new NotSupportedException($"[{Const.HDiffHeaderCompressionNotSupported}] HDIFF compression: {enumString.ToString()} is not supported!");
    public static void ThrowHdiffHeaderChecksumNotSupported(ReadOnlySpan<char> enumString)
        => throw new NotSupportedException($"[{Const.HDiffHeaderChecksumNotSupported}] HDIFF checksum: {enumString.ToString()} is not supported!");

#if NET8_0_OR_GREATER
    public static int TryGetReturnCodeFromError(Exception ex)
    {
        try
        {
            ReadOnlySpan<char> message = ex.Message;
            if (message.IsEmpty)
            {
                return -1;
            }

            Span<Range> splitRanges = stackalloc Range[2];
            int         splitLen    = message.GetSplits(splitRanges, ' ');

            ReadOnlySpan<char> splitFirst;
            if (splitLen == 0 ||
                (splitFirst = message[splitRanges[0]])[0] != '[' ||
                splitFirst[^1] != ']')
            {
                return -1;
            }

            splitFirst = splitFirst.Slice(1, message.Length - 2);
#if NET9_0_OR_GREATER
            return Const.ExceptionToReturnCodeMap.GetAlternateLookup<ReadOnlySpan<char>>()
                        .TryGetValue(splitFirst, out int returnCode) ? returnCode : -1;
#else
            return Const.ExceptionToReturnCodeMap.GetValueOrDefault(splitFirst.ToString(), -1);
#endif
        }
        catch
        {
            return -1;
        }
    }
#endif
}