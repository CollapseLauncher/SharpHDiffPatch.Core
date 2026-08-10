using System;
using System.IO;
#if NET8_0_OR_GREATER
using System.Collections.Generic;
// ReSharper disable InconsistentNaming
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
        { HDiffHeaderChecksumNotSupported,       0b_00000000_00000000_00000000_00010000 },
        { HDiffHeaderEndOfFileOrData,            0b_00000000_00000000_00000000_00100000 },
        { HDiffPathIsEmptyOrInvalid,             0b_00000000_00000000_00000000_01000000 },
        { HDiffFILEDescriptorNull,               0b_00000000_00000000_00000000_10000000 },
        { HDiffIOException,                      0b_00000000_00000000_00000001_00000000 },
        { HDiffInfoIsNull,                       0b_00000000_00000000_00000010_00000000 }
};
#endif

    public const string HDiffHeaderSignatureEmptyOrUnreadable = "HDIFF_HeaderSignatureEmptyOrUnreadable";
    public const string HDiffHeaderMagicNotSupported          = "HDIFF_HeaderMagicNotSupported";
    public const string HDiffHeaderCompressionNotSupported    = "HDIFF_HeaderCompressionNotSupported";
    public const string HDiffHeaderChecksumNotSupported       = "HDIFF_HeaderChecksumNotSupported";
    public const string HDiffHeaderEndOfFileOrData            = "HDIFF_EndOfFileOrData";
    public const string HDiffPathIsEmptyOrInvalid             = "HDIFF_PathIsEmptyOrInvalid";
    public const string HDiffFILEDescriptorNull               = "HDIFF_FILEDescriptorNull";
    public const string HDiffIOException                      = "HDIFF_IOException";
    public const string HDiffInfoIsNull                       = "HDIFF_InfoIsNull";
}

public static class ExceptionHelper
{
    public static void ThrowHDiffHeaderSignatureEmptyOrUnreadable()
        => throw new InvalidOperationException($"[{Const.HDiffHeaderSignatureEmptyOrUnreadable}] Header signature is empty or unreadable!");
    public static void ThrowHDiffHeaderMagicNotSupported(ReadOnlySpan<char> magic)
        => throw new NotSupportedException($"[{Const.HDiffHeaderMagicNotSupported}] Header magic: {magic.ToString()} is not supported!");
    public static void ThrowHDiffHeaderCompressionNotSupported(ReadOnlySpan<char> enumString)
        => throw new NotSupportedException($"[{Const.HDiffHeaderCompressionNotSupported}] HDIFF compression: {enumString.ToString()} is not supported!");
    public static void ThrowHDiffHeaderChecksumNotSupported(ReadOnlySpan<char> enumString)
        => throw new NotSupportedException($"[{Const.HDiffHeaderChecksumNotSupported}] HDIFF checksum: {enumString.ToString()} is not supported!");
    public static void ThrowHDiffEndOfFileOrData(Exception innerException)
        => throw new EndOfStreamException($"[{Const.HDiffHeaderEndOfFileOrData}] HDiff Data has reached End-of-File or Data", innerException);
    public static void ThrowHDiffPathIsEmptyOrInvalid()
        => throw new InvalidOperationException($"[{Const.HDiffPathIsEmptyOrInvalid}] File or Directory path is empty or invalid");
    public static void ThrowHDiffFILEDescriptorNull()
        => throw new NullReferenceException($"[{Const.HDiffFILEDescriptorNull}] FILE descriptor cannot be null!");
    public static void ThrowHDiffIOException(Exception ex)
        => throw new IOException($"[{Const.HDiffIOException}] An IO Error has occurred with message: {ex.Message}", ex);
    public static void ThrowHDiffInfoIsNull()
        => throw new NullReferenceException($"[{Const.HDiffInfoIsNull}] HDiffInfo pointer is null!");

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