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
using System.Runtime.InteropServices;
using SharpHDiffPatch.New.Header;
using SharpHDiffPatch.New.IO.Compression.BZip2;
using SharpHDiffPatch.New.IO.Compression.Lzma;
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

namespace SharpHDiffPatch.New.IO.Compression;

internal static class DecompressStreamFactory
{
    private delegate        Stream              ZstdStreamFallback(Stream stream, bool leaveOpen);
    private static          ZstdStreamFallback? _createZstdStreamFallback;
    private static readonly int                 ZstdWindowLogMax = Environment.Is64BitProcess ? 31 : 30;

    internal static Stream CreateStream(
        HDiffCompression type,
        Stream           sourceStream,
        bool             leaveOpen)
    {
        return type switch
        {
            HDiffCompression.Uncompressed => sourceStream,
            HDiffCompression.Zstd => CreateZstdStream(sourceStream, leaveOpen),
            HDiffCompression.Zlib => new DeflateStream(sourceStream, CompressionMode.Decompress, leaveOpen),
            HDiffCompression.BZ2 => new BZip2InputStream(sourceStream, false, leaveOpen),
            HDiffCompression.PBZ2 => new BZip2InputStream(sourceStream, true, leaveOpen),
            HDiffCompression.Lzma or HDiffCompression.Lzma2 => CreateLzmaStream(sourceStream, leaveOpen),
            _ => throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: {type} is not supported")
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

    private static Stream CreateLzmaStream(Stream rawStream, bool leaveOpen)
    {
        int propLen = rawStream.ReadByte();
        if (propLen != 5) return new LzmaInputStream([(byte)propLen], rawStream, leaveOpen); // Get LZMA2 if propLen != 5

        // Get LZMA if propLen == 5
        byte[] props = new byte[propLen];
        _ = rawStream.Read(props, 0, propLen);
        int dicSize = MemoryMarshal.Read<int>(props.AsSpan(1));
        return new LzmaInputStream(props, rawStream, -1, -1, rawStream, false, leaveOpen);
    }
}
