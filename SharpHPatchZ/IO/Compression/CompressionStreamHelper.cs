// ReSharper disable IdentifierTypo
// ReSharper disable ConvertSwitchStatementToSwitchExpression
// ReSharper disable CommentTypo
// ReSharper disable InconsistentNaming

#if NET6_0_OR_GREATER
using System.Collections.Generic;
using ZstdNet;
#endif

using System;
using System.IO;
using System.IO.Compression;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Compression.BZip2;
using SharpHPatchZ.IO.Compression.Lzma;
#if NETSTANDARD2_0_OR_GREATER || NET6_0_OR_GREATER
using ZstdManagedDecompressor = ZstdSharp.Decompressor;
using ZstdManagedDecompressorParameter = ZstdSharp.Unsafe.ZSTD_dParameter;
using ZstdManagedStream = ZstdSharp.DecompressionStream;
#endif

#if !NETSTANDARD2_0_OR_GREATER
using ZstdNativeDecompressor = ZstdNet.DecompressionOptions;
using ZstdNativeDecompressorParameter = ZstdNet.ZSTD_dParameter;
using ZstdNativeStream = ZstdNet.DecompressionStream;
#endif

namespace SharpHPatchZ.IO.Compression;

internal interface IDecompressor
{
    Stream CreateDecompressionStream(
        Stream sourceStream,
        long   compressedSize,
        long   decompressedSize,
        bool   leaveOpen);
}

internal readonly struct HDiffDecompressor(HDiffCompression type) : IDecompressor
{
    public Stream CreateDecompressionStream(
        Stream sourceStream,
        long   compressedSize,
        long   decompressedSize,
        bool   leaveOpen)
        => DecompressStreamFactory.Create(type, sourceStream, leaveOpen, compressedSize, decompressedSize);
}

internal static class DecompressStreamFactory
{
    private delegate        Stream              ZstdStreamFallback(Stream stream, bool leaveOpen);
    private static          ZstdStreamFallback? _createZstdStreamFallback;
    private static readonly int                 ZstdWindowLogMax = Environment.Is64BitProcess ? 31 : 30;

    internal static Stream Create(HDiffCompression type,
                                  Stream           sourceStream,
                                  bool             leaveOpen,
                                  long             compressedSize   = -1,
                                  long             decompressedSize = -1)
    {
        if (compressedSize < -1)
            throw new ArgumentOutOfRangeException(nameof(compressedSize));

        if (decompressedSize < -1)
            throw new ArgumentOutOfRangeException(nameof(decompressedSize));

        if (type == HDiffCompression.Uncompressed || compressedSize == 0)
            return sourceStream;

        // Advance one byte padding for Zlib / Libdeflate
        if (type == HDiffCompression.Zlib)
            sourceStream.ReadByte();

        return type switch
        {
            HDiffCompression.Zstd => CreateZstdStream(sourceStream, leaveOpen),
            HDiffCompression.Zlib => new DeflateStream(sourceStream, CompressionMode.Decompress, leaveOpen),
            HDiffCompression.BZ2 => new BZip2InputStream(sourceStream, false, leaveOpen),
            HDiffCompression.PBZ2 => new BZip2InputStream(sourceStream, true, leaveOpen),
            HDiffCompression.Lzma or HDiffCompression.Lzma2 => CreateLzmaStream(type, sourceStream, compressedSize, decompressedSize, leaveOpen),
            _ => throw ExceptionHelper.ThrowHDiffHeaderCompressionNotSupported(type.ToString())
        };
    }

    private static Stream CreateZstdStream(Stream rawStream, bool leaveOpen)
    {
        if (_createZstdStreamFallback != null) return _createZstdStreamFallback(rawStream, leaveOpen);

#if !(NETSTANDARD2_0_OR_GREATER || NET461_OR_GREATER)
        if (DllUtils.IsLibraryExist(DllUtils.DllName))
            _createZstdStreamFallback = CreateZstdNativeStream;
        else
            _createZstdStreamFallback = CreateZstdManagedStream;
#else
            _createZstdStreamFallback = CreateZstdManagedStream;
#endif
        return _createZstdStreamFallback(rawStream, leaveOpen);
    }

    /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
     * always use 31 as the size_t, which is 8 bytes length.
     * 
     * Code Snippets (decompress_plugin_demo.h:963):
     *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
     */
#if !NETSTANDARD2_0_OR_GREATER
    private static Stream CreateZstdNativeStream(Stream rawStream, bool leaveOpen) =>
        new ZstdNativeStream(rawStream, new ZstdNativeDecompressor(null, new Dictionary<ZstdNativeDecompressorParameter, int>
        {
            { ZstdNativeDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax }
        }));
#endif

    private static Stream CreateZstdManagedStream(Stream rawStream, bool leaveOpen)
    {
        ZstdManagedDecompressor decompressor = new();
        decompressor.SetParameter(ZstdManagedDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax);
        return new ZstdManagedStream(rawStream, decompressor, 16 << 10, leaveOpen: leaveOpen);
    }

    private static Stream CreateLzmaStream(
        HDiffCompression type,
        Stream           rawStream,
        long             compressedSize,
        long             decompressedSize,
        bool             leaveOpen)
    {
        int property = rawStream.ReadByte();
        if (property < 0)
            throw ExceptionHelper.ThrowHDiffCompLZMAPropertyMissing();

        long payloadSize = compressedSize >= 0 ? compressedSize - 1 : -1;
        if (type == HDiffCompression.Lzma2)
        {
            if (property > 40)
                throw ExceptionHelper.ThrowHDiffCompLZMA2DictionaryInvalid(property);
            return payloadSize == 0
                ? throw ExceptionHelper.ThrowHDiffCompLZMA2NoCompressedPayload()
                : new LzmaInputStream([(byte)property], rawStream, payloadSize, decompressedSize, leaveOpen);
        }

        const int lzmaPropertySize = 5;
        if (property != lzmaPropertySize)
            throw ExceptionHelper.ThrowHDiffCompLZMADictionaryInvalidLength(lzmaPropertySize, property);

        const int rangeDecoderHeaderSize = 5;
        if (compressedSize is >= 0 and < 1 + lzmaPropertySize + rangeDecoderHeaderSize)
            throw ExceptionHelper.ThrowHDiffCompLZMASizeTooSmallForDictionaryRead();

        byte[] properties = new byte[lzmaPropertySize];
        rawStream.ReadExactly(properties, 0, properties.Length);
        payloadSize = compressedSize >= 0 ? compressedSize - 1 - properties.Length : -1;

        return new LzmaInputStream(properties, rawStream, payloadSize, decompressedSize, leaveOpen);
    }
}
