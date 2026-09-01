using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHPatchZ.IO.Reader;

internal sealed class BufferedRemainderStream : Stream
{
    private readonly Stream _stream;
    private readonly bool   _leaveOpen;

    private          byte[]? _buffer;
    private          int     _offset;
    private readonly int     _end;
    private          bool    _isDisposed;

    internal BufferedRemainderStream(
        Stream stream,
        byte[] buffer,
        int    offset,
        int    count,
        bool   leaveOpen)
    {
        _stream    = stream ?? throw new ArgumentNullException(nameof(stream));
        _buffer    = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _offset    = offset;
        _end       = checked(offset + count);
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead  => !_isDisposed;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateArguments(buffer, offset, count);
        ThrowIfDisposed();

        int bufferedRead = ReadBuffered(new Span<byte>(buffer, offset, count));
        return bufferedRead != 0 || count == 0
            ? bufferedRead
            : _stream.Read(buffer, offset, count);
    }

#if NET6_0_OR_GREATER
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        int bufferedRead = ReadBuffered(buffer);
        return bufferedRead != 0 || buffer.IsEmpty
            ? bufferedRead
            : _stream.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte>      buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        int bufferedRead = ReadBuffered(buffer.Span);
        return bufferedRead != 0 || buffer.IsEmpty
            ? bufferedRead
            : await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
#endif

    public override async Task<int> ReadAsync(
        byte[]            buffer,
        int               offset,
        int               count,
        CancellationToken cancellationToken)
    {
        ValidateArguments(buffer, offset, count);
        ThrowIfDisposed();

        int bufferedRead = ReadBuffered(new Span<byte>(buffer, offset, count));
        return bufferedRead != 0 || count == 0
            ? bufferedRead
            : await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    private int ReadBuffered(Span<byte> destination)
    {
        byte[]? source = _buffer;
        if (source is null)
        {
            return 0;
        }

        int count = Math.Min(destination.Length, _end - _offset);
        new ReadOnlySpan<byte>(source, _offset, count).CopyTo(destination);
        _offset += count;

        if (_offset == _end)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(source);
        }

        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (disposing && !_leaveOpen)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }

#if NET6_0_OR_GREATER
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
#endif

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(BufferedRemainderStream));
        }
    }

    private static void ValidateArguments(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (buffer.Length - offset < count) throw new ArgumentException("Offset and count exceed the buffer length.");
    }
}

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _stream;
    private readonly long   _length;
    private          long   _position;

    public long Remaining => _length - _position;

    public BoundedReadStream(Stream stream, long length)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _length = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int requested = (int)Math.Min(count, Remaining);
        int read      = _stream.Read(buffer, offset, requested);
        _position += read;
        return read;
    }

#if NET6_0_OR_GREATER
    public override int Read(Span<byte> buffer)
    {
        int requested = (int)Math.Min(buffer.Length, Remaining);
        int read      = _stream.Read(buffer[..requested]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte>      buffer,
        CancellationToken cancellationToken = default)
    {
        int requested = (int)Math.Min(buffer.Length, Remaining);
        int read = await _stream.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }
#endif

    public override async Task<int> ReadAsync(
        byte[]            buffer,
        int               offset,
        int               count,
        CancellationToken cancellationToken)
    {
        int requested = (int)Math.Min(count, Remaining);
        int read = await _stream.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class DecompressionTransitionStream : Stream
{
    private readonly Stream                  _decompressedStream;
    private readonly BoundedReadStream       _compressedStream;
    private readonly BufferedRemainderStream _remainderStream;

    private long _decompressedRemaining;
    private bool _isDecoding = true;
    private bool _isDisposed;

    public DecompressionTransitionStream(
        Stream                  decompressedStream,
        BoundedReadStream       compressedStream,
        BufferedRemainderStream remainderStream,
        long                    decompressedLength)
    {
        _decompressedStream    = decompressedStream ?? throw new ArgumentNullException(nameof(decompressedStream));
        _compressedStream      = compressedStream ?? throw new ArgumentNullException(nameof(compressedStream));
        _remainderStream       = remainderStream ?? throw new ArgumentNullException(nameof(remainderStream));
        _decompressedRemaining = decompressedLength >= 0
            ? decompressedLength
            : throw new ArgumentOutOfRangeException(nameof(decompressedLength));
    }

    public override bool CanRead  => !_isDisposed;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (count == 0) return 0;

        if (_isDecoding)
        {
            if (_decompressedRemaining != 0)
            {
                int requested = (int)Math.Min(count, _decompressedRemaining);
                int read      = _decompressedStream.Read(buffer, offset, requested);
                if (read == 0) ThrowUnexpectedDecompressedEof();

                _decompressedRemaining -= read;
                return read;
            }

            FinishDecoding();
        }

        return _remainderStream.Read(buffer, offset, count);
    }

#if NET6_0_OR_GREATER
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;

        if (_isDecoding)
        {
            if (_decompressedRemaining != 0)
            {
                int requested = (int)Math.Min(buffer.Length, _decompressedRemaining);
                int read      = _decompressedStream.Read(buffer[..requested]);
                if (read == 0) ThrowUnexpectedDecompressedEof();

                _decompressedRemaining -= read;
                return read;
            }

            FinishDecoding();
        }

        return _remainderStream.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte>      buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;

        if (_isDecoding)
        {
            if (_decompressedRemaining != 0)
            {
                int requested = (int)Math.Min(buffer.Length, _decompressedRemaining);
                int read = await _decompressedStream.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
                if (read == 0) ThrowUnexpectedDecompressedEof();

                _decompressedRemaining -= read;
                return read;
            }

            await FinishDecodingAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _remainderStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
#endif

    public override async Task<int> ReadAsync(
        byte[]            buffer,
        int               offset,
        int               count,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (count == 0) return 0;

        if (_isDecoding)
        {
            if (_decompressedRemaining != 0)
            {
                int requested = (int)Math.Min(count, _decompressedRemaining);
                int read = await _decompressedStream.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false);
                if (read == 0) ThrowUnexpectedDecompressedEof();

                _decompressedRemaining -= read;
                return read;
            }

            await FinishDecodingAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _remainderStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    private void FinishDecoding()
    {
        if (_decompressedStream.ReadByte() != -1)
        {
            throw new InvalidDataException("The decompressed segment is longer than its declared length.");
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (_compressedStream.Remaining != 0)
            {
                int read = _compressedStream.Read(scratch, 0, (int)Math.Min(scratch.Length, _compressedStream.Remaining));
                if (read == 0) throw new EndOfStreamException("The compressed segment ended before its declared length.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        _decompressedStream.Dispose();
        _isDecoding = false;
    }

    private async ValueTask FinishDecodingAsync(CancellationToken token)
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int extra = await _decompressedStream.ReadAsync(scratch, 0, 1, token).ConfigureAwait(false);
            if (extra != 0)
            {
                throw new InvalidDataException("The decompressed segment is longer than its declared length.");
            }

            while (_compressedStream.Remaining != 0)
            {
                int read = await _compressedStream.ReadAsync(
                    scratch,
                    0,
                    (int)Math.Min(scratch.Length, _compressedStream.Remaining),
                    token).ConfigureAwait(false);

                if (read == 0) throw new EndOfStreamException("The compressed segment ended before its declared length.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

#if NET6_0_OR_GREATER
        await _decompressedStream.DisposeAsync().ConfigureAwait(false);
#else
        _decompressedStream.Dispose();
#endif
        _isDecoding = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (disposing)
        {
            _decompressedStream.Dispose();
            _compressedStream.Dispose();
            _remainderStream.Dispose();
        }

        base.Dispose(disposing);
    }

#if NET6_0_OR_GREATER
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _decompressedStream.DisposeAsync().ConfigureAwait(false);
        _compressedStream.Dispose();
        await _remainderStream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
#endif

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(DecompressionTransitionStream));
        }
    }

    private static void ThrowUnexpectedDecompressedEof()
        => throw new EndOfStreamException("The decompressed segment ended before its declared length.");
}
