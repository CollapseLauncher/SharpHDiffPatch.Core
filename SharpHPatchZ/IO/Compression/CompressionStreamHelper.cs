// ReSharper disable IdentifierTypo
// ReSharper disable ConvertSwitchStatementToSwitchExpression
// ReSharper disable CommentTypo
// ReSharper disable InconsistentNaming

#if NET6_0_OR_GREATER && !NET11_0_OR_GREATER
using System.Collections.Generic;
using ZstdNet;
#endif

using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Compression.BZip2;
using SharpHPatchZ.IO.Compression.Lzma;
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

#if !NET11_0_OR_GREATER
using ZstdManagedDecompressor = ZstdSharp.Decompressor;
using ZstdManagedDecompressorParameter = ZstdSharp.Unsafe.ZSTD_dParameter;
using ZstdManagedStream = ZstdSharp.DecompressionStream;
#endif

#if NET6_0_OR_GREATER && !NET11_0_OR_GREATER
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
            return sourceStream.GetChunkedRead(decompressedSize, leaveOpen);

        Stream chunkedStream = sourceStream.GetChunkedRead(compressedSize, leaveOpen);

        // Advance one byte padding for Zlib / Libdeflate
        if (type == HDiffCompression.Zlib)
            chunkedStream.ReadByte();

        return type switch
        {
            HDiffCompression.Zstd => CreateZstdStream(chunkedStream, leaveOpen),
            HDiffCompression.Zlib => new DeflateStream(chunkedStream, CompressionMode.Decompress, leaveOpen),
            HDiffCompression.BZ2 => new BZip2InputStream(chunkedStream, false, leaveOpen),
            HDiffCompression.PBZ2 => new BZip2InputStream(chunkedStream, true, leaveOpen),
            HDiffCompression.Lzma or HDiffCompression.Lzma2 => CreateLzmaStream(type, chunkedStream, compressedSize, decompressedSize, leaveOpen),
            _ => throw ExceptionHelper.ThrowHDiffHeaderCompressionNotSupported(type.ToString())
        };
    }

    private static Stream CreateZstdStream(Stream rawStream, bool leaveOpen)
    {
        if (_createZstdStreamFallback != null) return _createZstdStreamFallback(rawStream, leaveOpen);

#if NET6_0_OR_GREATER && !NET11_0_OR_GREATER
        if (DllUtils.IsLibraryExist(DllUtils.DllName))
            _createZstdStreamFallback = CreateZstdNativeStream;
        else
            _createZstdStreamFallback = CreateZstdManagedStream;
#elif NET11_0_OR_GREATER
        _createZstdStreamFallback = CreateZstdNativeStream;
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
#if NET6_0_OR_GREATER && !NET11_0_OR_GREATER
    private static Stream CreateZstdNativeStream(Stream rawStream, bool leaveOpen) =>
        new ZstdNativeStream(rawStream, new ZstdNativeDecompressor(null, new Dictionary<ZstdNativeDecompressorParameter, int>
        {
            { ZstdNativeDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax }
        }), 0, leaveOpen);
#elif NET11_0_OR_GREATER
    private static Stream CreateZstdNativeStream(Stream rawStream, bool leaveOpen) =>
        new ZstandardStream(rawStream, new ZstandardDecompressionOptions
        {
            MaxWindowLog2 = ZstdWindowLogMax
        }, leaveOpen);
#endif

#if !NET11_0_OR_GREATER
    private static Stream CreateZstdManagedStream(Stream rawStream, bool leaveOpen)
    {
        ZstdManagedDecompressor decompressor = new();
        decompressor.SetParameter(ZstdManagedDecompressorParameter.ZSTD_d_windowLogMax, ZstdWindowLogMax);
        return new ZstdManagedStream(rawStream, decompressor, 16 << 10, leaveOpen: leaveOpen);
    }
#endif

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

    private static Stream GetChunkedRead(this Stream stream,
                                         long        size,
                                         bool        leaveOpen)
    {
        return size < 0 ? stream : new ChunkedReadStream(stream, size, leaveOpen);
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly Stream _underlyingStream;
        private readonly bool   _leaveOpen;
        private          long   _position;
        private          int    _disposed;

        internal ChunkedReadStream(Stream sourceStream,
                                   long   size,
                                   bool   leaveOpen)
        {
            _underlyingStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
            Length = size >= 0
                ? size
                : throw new ArgumentOutOfRangeException(nameof(size));
            _leaveOpen = leaveOpen;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (!_leaveOpen)
                _underlyingStream.Dispose();

            base.Dispose(disposing);
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            _underlyingStream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _underlyingStream.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            ValidateReadArguments(buffer, offset, count);

            int requested = (int)Math.Min(count, Length - _position);
            if (requested == 0)
                return 0;

            int read = _underlyingStream.Read(buffer, offset, requested);
            _position += read;
            return read;
        }

#if NET6_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            int requested = (int)Math.Min(buffer.Length, Length - _position);
            if (requested == 0)
                return 0;

            int read = _underlyingStream.Read(buffer[..requested]);
            _position += read;
            return read;
        }
#endif

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateReadArguments(buffer, offset, count);

            int requested = (int)Math.Min(count, Length - _position);
            if (requested == 0)
                return 0;

            int read = await _underlyingStream.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

#if NET6_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            int requested = (int)Math.Min(buffer.Length, Length - _position);
            if (requested == 0)
                return 0;

            int read = await _underlyingStream.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }
#endif

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

#if NET6_0_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            throw new NotSupportedException();
        }
#endif

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

#if NET6_0_OR_GREATER
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
#endif

        public override bool CanRead  => Volatile.Read(ref _disposed) == 0 && _underlyingStream.CanRead;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChunkedReadStream));
        }

        private static void ValidateReadArguments(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException("Offset and count exceed the buffer length.");
        }
    }
}
