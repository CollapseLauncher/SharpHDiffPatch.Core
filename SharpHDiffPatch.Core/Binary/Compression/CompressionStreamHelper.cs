// ReSharper disable IdentifierTypo
// ReSharper disable ConvertSwitchStatementToSwitchExpression
// ReSharper disable CommentTypo
// ReSharper disable InconsistentNaming

using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SharpHDiffPatch.Core.Binary.Compression.BZip2;
using SharpHDiffPatch.Core.Binary.Compression.Lzma;
using SharpHDiffPatch.Core.Binary.Streams;

#if NET6_0_OR_GREATER
using System.Collections.Generic;
using ZstdNet;
#endif

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

namespace SharpHDiffPatch.Core.Binary.Compression
{
    public enum HDiffCompressionMode
    {
        nocomp,
        zstd,
        lzma,
        lzma2,
        zlib,
        bz2,
        pbz2
    }

    internal static class CompressionStreamHelper
    {
        private delegate Stream ZstdStreamFallback(Stream stream);
        private static ZstdStreamFallback _createZstdStreamFallback;
        private static readonly int ZstdWindowLogMax = Environment.Is64BitProcess ? 31 : 30;

        internal static void GetDecompressStreamPlugin(
            HDiffCompressionMode type,
            Stream               sourceStream,
            out Stream           decompStream,
            long                 length,
            long                 compLength,
            out long             outLength,
            bool                 isBuffered)
        {
            long toPosition = sourceStream.Position;
            outLength = compLength > 0 ? compLength : length;
            long toCompLength = sourceStream.Position + outLength;

            HDiffPatch.Event.PushLog($"[PatchCore::GetDecompressStreamPlugin] Assigning stream of compression: {type} at start pos: {toPosition} to end pos: {toCompLength}", Verbosity.Verbose);
            Stream rawStream;
            if (isBuffered)
                rawStream = new ChunkStream(sourceStream, toPosition, toCompLength);
            else
            {
                sourceStream.Position = toPosition;
                rawStream             = sourceStream;
            }

            if (type != HDiffCompressionMode.nocomp && compLength == 0)
            {
                decompStream = rawStream;
                return;
            }

            decompStream = type switch
            {
                HDiffCompressionMode.nocomp => rawStream,
                HDiffCompressionMode.zstd => CreateZstdStream(rawStream),
                HDiffCompressionMode.zlib => new DeflateStream(rawStream, CompressionMode.Decompress, true),
                HDiffCompressionMode.bz2 => new BZip2InputStream(rawStream, false, true),
                HDiffCompressionMode.pbz2 => new BZip2InputStream(rawStream, true, true),
                HDiffCompressionMode.lzma or HDiffCompressionMode.lzma2 => CreateLzmaStream(rawStream),
                _ => throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: {type} is not supported")
            };
        }

        private static Stream CreateZstdStream(Stream rawStream)
        {
            if (_createZstdStreamFallback != null) return _createZstdStreamFallback(rawStream);

#if !(NETSTANDARD2_0_OR_GREATER || NET461_OR_GREATER)
            if (DllUtils.IsLibraryExist(DllUtils.DllName))
                _createZstdStreamFallback = CreateZstdNativeStream;
            else
                _createZstdStreamFallback = CreateZstdManagedStream;
#else
                _createZstdStreamFallback = CreateZstdManagedStream;
#endif
            return _createZstdStreamFallback(rawStream);
        }

        /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
         * always use 31 as the size_t, which is 8 bytes length.
         * 
         * Code Snippets (decompress_plugin_demo.h:963):
         *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
         */
#if !NETSTANDARD2_0_OR_GREATER
        private static Stream CreateZstdNativeStream(Stream rawStream) =>
            new ZstdNativeStream(rawStream, new ZstdNativeDecompressor(null, new Dictionary<ZstdNativeDecompressorParameter, int>
            {
                { ZstdNativeDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax }
            }));
#endif

        private static Stream CreateZstdManagedStream(Stream rawStream)
        {
            ZstdManagedDecompressor decompressor = new();
            decompressor.SetParameter(ZstdManagedDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax);
            return new ZstdManagedStream(rawStream, decompressor, 16 << 10);
        }

        private static Stream CreateLzmaStream(Stream rawStream)
        {
            int propLen = rawStream.ReadByte();
            if (propLen != 5) return new LzmaInputStream([(byte)propLen], rawStream, true); // Get LZMA2 if propLen != 5

            // Get LZMA if propLen == 5
            byte[] props = new byte[propLen];
            _ = rawStream.Read(props, 0, propLen);
            int dicSize = MemoryMarshal.Read<int>(props.AsSpan(1));
            HDiffPatch.Event.PushLog($"[PatchCore::CreateLzmaStream] Assigning LZMA stream with dictionary size: {dicSize}", Verbosity.Verbose);
            return new LzmaInputStream(props, rawStream, -1, -1, rawStream, false, true);
        }
    }
}
