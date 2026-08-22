using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SharpHPatchZ.Header;

// ReSharper disable InconsistentNaming

namespace SharpHPatchZ.Extension;

file static class Const
{
    internal static readonly Dictionary<string, int> ExceptionToReturnCodeMap = new()
    {
        // Not Supported
        { HDiffHeaderMagicNotSupported,                0x10 },
        { HDiffHeaderCompressionNotSupported,          0x11 },
        { HDiffHeaderChecksumNotSupported,             0x12 },
        { HDiffPatchFactoryNotSupported,               0x13 },

        // Memory Allocation / Descriptor
        { HDiffInfoNotAllocated,                       0x30 },
        { HDiffInfoDirectoryPatchMetadataNotAllocated, 0x31 },
        { HDiffInfoPatchMetadataNotAllocated,          0x32 },
        { HDiffFILEDescriptorNull,                     0x33 },
        { HDiffArgumentNull,                           0x34 },

        // IO / File / Paths
        { HDiffHeaderSignatureEmptyOrUnreadable,       0x50 },
        { HDiffHeaderEndOfFileOrData,                  0x51 },
        { HDiffPathIsEmptyOrInvalid,                   0x52 },
        { HDiffIOException,                            0x53 },
        { HDiffPatchInputPathNotExist,                 0x54 },
        { HDiffPatchInputSizeMismatched,               0x55 },
        { HDiffPatchPathNotADirectory,                 0x56 },
        { HDiffPatchPathNotAFile,                      0x57 },
        { HDiffPatchInputFilesMismatched,              0x58 },

        // Decompression Initialization
        { HDiffCompLZMAPropertyMissing,                0xA0 },
        { HDiffCompLZMA2DictionaryInvalid,             0xA1 },
        { HDiffCompLZMA2NoCompressedPayload,           0xA2 },
        { HDiffCompLZMADictionaryInvalidLength,        0xA3 },
        { HDiffCompLZMASizeTooSmallForDictionaryRead,  0xA4 }
    };

    public const string HDiffHeaderMagicNotSupported       = "HDIFF_HeaderMagicNotSupported";
    public const string HDiffHeaderCompressionNotSupported = "HDIFF_HeaderCompressionNotSupported";
    public const string HDiffHeaderChecksumNotSupported    = "HDIFF_HeaderChecksumNotSupported";
    public const string HDiffPatchFactoryNotSupported      = "HDIFF_PatchFactoryNotSupported";

    public const string HDiffInfoNotAllocated                       = "HDIFF_InfoNotAllocated";
    public const string HDiffInfoDirectoryPatchMetadataNotAllocated = "HDIFF_InfoDirectoryPatchMetadataNotAllocated";
    public const string HDiffInfoPatchMetadataNotAllocated          = "HDIFF_InfoPatchMetadataNotAllocated";
    public const string HDiffFILEDescriptorNull                     = "HDIFF_FILEDescriptorNull";
    public const string HDiffArgumentNull                           = "HDIFF_ArgumentNull";

    public const string HDiffHeaderSignatureEmptyOrUnreadable = "HDIFF_HeaderSignatureEmptyOrUnreadable";
    public const string HDiffHeaderEndOfFileOrData            = "HDIFF_EndOfFileOrData";
    public const string HDiffPathIsEmptyOrInvalid             = "HDIFF_PathIsEmptyOrInvalid";
    public const string HDiffIOException                      = "HDIFF_IOException";
    public const string HDiffPatchInputPathNotExist           = "HDIFF_PatchInputPathNotExist";
    public const string HDiffPatchInputSizeMismatched         = "HDIFF_PatchInputSizeMismatched";
    public const string HDiffPatchPathNotADirectory           = "HDIFF_PatchPathNotADirectory";
    public const string HDiffPatchPathNotAFile                = "HDIFF_PatchPathNotAFile";
    public const string HDiffPatchInputFilesMismatched        = "HDIFF_PatchInputFilesMismatched";

    public const string HDiffCompLZMAPropertyMissing              = "HDIFF_CompLZMAPropertyMissing";
    public const string HDiffCompLZMA2DictionaryInvalid           = "HDIFF_CompLZMA2DictionaryInvalid";
    public const string HDiffCompLZMA2NoCompressedPayload         = "HDIFF_CompLZMA2NoCompressedPayload";
    public const string HDiffCompLZMADictionaryInvalidLength      = "HDIFF_CompLZMADictionaryInvalidLength";
    public const string HDiffCompLZMASizeTooSmallForDictionaryRead = "HDIFF_CompLZMASizeToSmallForDictionaryRead";
}

public static class ExceptionHelper
{
    private static Exception? LastException;

    public static NotSupportedException ThrowHDiffHeaderMagicNotSupported(ReadOnlySpan<char> magic)
        => new($"[{Const.HDiffHeaderMagicNotSupported}] Header magic: {magic.ToString()} is not supported!");
    public static NotSupportedException ThrowHDiffHeaderCompressionNotSupported(ReadOnlySpan<char> enumString)
        => new($"[{Const.HDiffHeaderCompressionNotSupported}] HDIFF compression: {enumString.ToString()} is not supported!");
    public static NotSupportedException ThrowHDiffHeaderChecksumNotSupported(ReadOnlySpan<char> enumString)
        => new($"[{Const.HDiffHeaderChecksumNotSupported}] HDIFF checksum: {enumString.ToString()} is not supported!");
    public static NotSupportedException ThrowHDiffPatchFactoryNotSupported(HDiffMagic type)
        => new($"[{Const.HDiffPatchFactoryNotSupported}] No factory supported for type: {type}!");

    public static NullReferenceException ThrowHDiffInfoNotAllocated()
        => new($"[{Const.HDiffInfoNotAllocated}] HDiffInfo pointer is not allocated!");
    public static NullReferenceException ThrowHDiffInfoDirectoryPatchMetadataNotAllocated()
        => new($"[{Const.HDiffInfoDirectoryPatchMetadataNotAllocated}] HDiffInfo directory patch metadata is not allocated!");
    public static NullReferenceException ThrowHDiffInfoPatchMetadataNotAllocated()
        => new($"[{Const.HDiffInfoPatchMetadataNotAllocated}] HDiffInfo patch metadata is not allocated!");
    public static NullReferenceException ThrowHDiffFILEDescriptorNull()
        => new($"[{Const.HDiffFILEDescriptorNull}] FILE descriptor cannot be null!");
    public static NullReferenceException ThrowHDiffArgumentNull(string nameOfArg)
        => new($"[{Const.HDiffArgumentNull}] Argument: {nameOfArg} cannot be null!");

    public static InvalidOperationException ThrowHDiffHeaderSignatureEmptyOrUnreadable()
        => new($"[{Const.HDiffHeaderSignatureEmptyOrUnreadable}] Header signature is empty or unreadable!");
    public static EndOfStreamException ThrowHDiffEndOfFileOrData(Exception innerException)
        => new($"[{Const.HDiffHeaderEndOfFileOrData}] HDiff Data has reached End-of-File or Data", innerException);
    public static InvalidOperationException ThrowHDiffPathIsEmptyOrInvalid()
        => new($"[{Const.HDiffPathIsEmptyOrInvalid}] File or Directory path is empty or invalid");
    public static IOException ThrowHDiffIOException(Exception ex)
        => new($"[{Const.HDiffIOException}] An IO Error has occurred with message: {ex.Message}", ex);
    public static FileNotFoundException ThrowHDiffPatchInputPathNotExist(string filePath)
        => new($"[{Const.HDiffPatchInputPathNotExist}] Input path does not exist: {filePath}", filePath);
    public static InvalidOperationException ThrowHDiffPatchInputSizeMismatched(string filePath, long existingSize, long expectingSize)
        => new($"[{Const.HDiffPatchInputSizeMismatched}] Input file size does not match: {filePath} (Expecting: {expectingSize} bytes, but got: {existingSize} instead).");
    public static InvalidOperationException ThrowHDiffPatchPathNotADirectory(string path)
        => new($"[{Const.HDiffPatchPathNotADirectory}] Path is not a directory!: {path}");
    public static InvalidOperationException ThrowHDiffPatchPathNotAFile(string path)
        => new($"[{Const.HDiffPatchPathNotAFile}] Path is not a file!: {path}");
    public static InvalidOperationException ThrowHDiffPatchInputFilesMismatched(long existingSize, long expectingSize)
        => new($"[{Const.HDiffPatchInputFilesMismatched}] Input file size mismatched! Expecting: {expectingSize} bytes but got: {expectingSize} bytes instead.");

    public static InvalidDataException ThrowHDiffCompLZMAPropertyMissing()
        => new($"[{Const.HDiffCompLZMAPropertyMissing}] The LZMA stream is missing its properties.");
    public static InvalidDataException ThrowHDiffCompLZMA2DictionaryInvalid(int property)
        => new($"[{Const.HDiffCompLZMA2DictionaryInvalid}] The LZMA2 dictionary property must be at most 40, but was {property}.");
    public static InvalidDataException ThrowHDiffCompLZMA2NoCompressedPayload()
        => new($"[{Const.HDiffCompLZMA2NoCompressedPayload}] The LZMA2 stream has no compressed payload.");
    public static InvalidDataException ThrowHDiffCompLZMADictionaryInvalidLength(int lzmaPropertySize, int property)
        => new($"[{Const.HDiffCompLZMADictionaryInvalidLength}] The LZMA property length must be {lzmaPropertySize}, but was {property}.");
    public static InvalidDataException ThrowHDiffCompLZMASizeTooSmallForDictionaryRead()
        => new($"[{Const.HDiffCompLZMASizeTooSmallForDictionaryRead}] The LZMA compressed size is too small to contain its headers and payload.");


    public static int TryGetReturnCodeFromError(Exception? ex)
    {
        if (ex == null)
        {
            return 0;
        }

        try
        {
            Interlocked.Exchange(ref LastException, ex);

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
#elif !NET6_0_OR_GREATER && NETSTANDARD
            return splitFirst.ToString() is var splitFirstAsStr &&
                Const.ExceptionToReturnCodeMap.TryGetValue(splitFirstAsStr, out int value) ? value : -1;
#else
            return Const.ExceptionToReturnCodeMap.GetValueOrDefault(splitFirst.ToString(), -1);
#endif
        }
        catch
        {
            return -1;
        }
    }

#if NET8_0_OR_GREATER
    public static int TryGetLastErrorMessageUtf8(Span<byte> spanByte, LastErrorMessageType lastErrorMessageType)
    {
        Exception? thisException = Volatile.Read(ref LastException);
        if (thisException == null ||
            spanByte.Length == 0)
        {
            return 0;
        }

        spanByte[^1] = 0;
        spanByte     = spanByte[..^1]; // Slice and reserve last one byte for the null-terminator.

        StringBuilder builder = new();
        if (lastErrorMessageType.HasFlag(LastErrorMessageType.Message))
        {
            builder.AppendLine(thisException.Message);
        }

        if (lastErrorMessageType.HasFlag(LastErrorMessageType.StackTrace))
        {
            builder.AppendLine(thisException.StackTrace);
        }

        int written = 0;
        foreach (ReadOnlyMemory<char> chunk in builder.GetChunks())
        {
            if (!Encoding.UTF8.TryGetBytes(chunk.Span, spanByte, out int thisWritten))
            {
                return -1;
            }

            written  += thisWritten;
            spanByte =  spanByte[thisWritten..];
        }

        if (!spanByte.IsEmpty) spanByte[0] = 0; // Append null-terminator on last.
        return written;
    }

    public static int TryGetLastErrorMessageUnicode(Span<char> spanChar, LastErrorMessageType lastErrorMessageType)
    {
        Exception? thisException = Volatile.Read(ref LastException);
        if (thisException == null ||
            spanChar.Length == 0)
        {
            return 0;
        }

        spanChar[^1] = '\0';
        spanChar     = spanChar[..^1]; // Slice and reserve last one byte for the null-terminator.

        StringBuilder builder = new();
        if (lastErrorMessageType.HasFlag(LastErrorMessageType.Message))
        {
            builder.AppendLine(thisException.Message);
        }

        if (lastErrorMessageType.HasFlag(LastErrorMessageType.StackTrace))
        {
            builder.AppendLine(thisException.StackTrace);
        }

        int written = 0;
        foreach (ReadOnlyMemory<char> chunk in builder.GetChunks())
        {
            if (!chunk.Span.TryCopyTo(spanChar))
            {
                return -1;
            }

            int thisWritten = chunk.Length;
            written  += thisWritten;
            spanChar =  spanChar[thisWritten..];
        }

        if (!spanChar.IsEmpty) spanChar[0] = '\0'; // Append null-terminator on last.
        return written;
    }

    [Flags]
    public enum LastErrorMessageType
    {
        Message              = 1,
        StackTrace           = 2,
        MessageAndStackTrace = Message | StackTrace
    }
#endif
}