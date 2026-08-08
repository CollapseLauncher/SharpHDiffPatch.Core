using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHDiffPatch.New.IO.Reader;

internal sealed class BittableStreamReader
    : IDisposable
#if NET6_0_OR_GREATER
    , IAsyncDisposable
#endif
{
    private const int DefaultBufferSize  = 64 << 10;
    private const int MaxPackedUIntBytes = 11;

    private int _offset;
    private int _bufferedLength;

    private readonly byte[] _backedBuffer;
    public readonly  Stream BackedStream;

    private readonly bool _leaveOpen;
    private          bool _isDisposed;

    public int  TagBitCount;
    public byte Tag;
    public byte PreviousByte;

    public long Offset => _offset;

    public BittableStreamReader(Stream stream, int bufferSize = -1, bool leaveOpen = false)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        if (bufferSize <= 0)
        {
            bufferSize = DefaultBufferSize;
        }

        // A complete packed UInt64 must fit in the buffer so decoding can run
        // against memory even when the value crosses a stream-read boundary.
        bufferSize = Math.Max(bufferSize, MaxPackedUIntBytes);

        BackedStream  = stream;
        _backedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        _leaveOpen    = leaveOpen;
    }

    public byte ReadByte()
    {
        EnsureBuffered(1);

        PreviousByte = _backedBuffer[_offset++];
        TagBitCount  = 0;
        Tag          = 0;
        return PreviousByte;
    }

    public async ValueTask<byte> ReadByteAsync(CancellationToken token = default)
    {
        await EnsureBufferedAsync(1, token).ConfigureAwait(false);

        PreviousByte = _backedBuffer[_offset++];
        TagBitCount  = 0;
        Tag          = 0;
        return PreviousByte;
    }

    public void ReadBytes(byte[] buffer)
        => ReadBytes(buffer, 0, buffer?.Length ?? throw new ArgumentNullException(nameof(buffer)));

    public void ReadBytes(byte[] buffer, int offset, int count)
        => ReadBytes(new Span<byte>(buffer, offset, count));

    public void ReadBytes(Memory<byte> buffer)
        => ReadBytes(buffer.Span);

    public void ReadBytes(Span<byte> buffer)
    {
        ResetTag();

        while (!buffer.IsEmpty)
        {
            EnsureBuffered(1);

            int copyLength = Math.Min(buffer.Length, _bufferedLength - _offset);
            new ReadOnlySpan<byte>(_backedBuffer, _offset, copyLength).CopyTo(buffer);

            _offset += copyLength;
            buffer   = buffer[copyLength..];
        }
    }

    public ValueTask ReadBytesAsync(byte[] buffer, CancellationToken token = default)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        return ReadBytesAsync(new Memory<byte>(buffer), token);
    }

    public ValueTask ReadBytesAsync(
        byte[]            buffer,
        int               offset,
        int               count,
        CancellationToken token = default)
        => ReadBytesAsync(new Memory<byte>(buffer, offset, count), token);

    public async ValueTask ReadBytesAsync(
        Memory<byte>      buffer,
        CancellationToken token = default)
    {
        ResetTag();

        while (!buffer.IsEmpty)
        {
            await EnsureBufferedAsync(1, token).ConfigureAwait(false);

            int copyLength = Math.Min(buffer.Length, _bufferedLength - _offset);
            new ReadOnlyMemory<byte>(_backedBuffer, _offset, copyLength).CopyTo(buffer);

            _offset += copyLength;
            buffer   = buffer[copyLength..];
        }
    }

    public string ReadStringToNull() => ReadStringToNull(DefaultBufferSize);

    public string ReadStringToNull(int maxByteCount)
    {
        if (maxByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxByteCount));
        }

        ResetTag();

        byte[] stringBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, Math.Min(maxByteCount, 256)));
        int    stringLength = 0;
        try
        {
            while (true)
            {
                try
                {
                    EnsureBuffered(1);
                }
                catch (EndOfStreamException exception)
                {
                    throw new EndOfStreamException("The stream ended before the null terminator was found.", exception);
                }

                int available       = _bufferedLength - _offset;
                int terminatorIndex = Array.IndexOf(_backedBuffer, (byte)0, _offset, available);
                int bytesToCopy     = terminatorIndex >= 0 ? terminatorIndex - _offset : available;

                AppendStringBytes(ref stringBuffer,
                                  ref stringLength,
                                  _backedBuffer,
                                  _offset,
                                  bytesToCopy,
                                  maxByteCount);

                _offset += bytesToCopy;
                if (terminatorIndex < 0) continue;

                ++_offset;
                return Encoding.UTF8.GetString(stringBuffer, 0, stringLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stringBuffer);
        }
    }

    public ValueTask<string> ReadStringToNullAsync(CancellationToken token)
        => ReadStringToNullAsync(DefaultBufferSize, token);

    public async ValueTask<string> ReadStringToNullAsync(
        int               maxByteCount,
        CancellationToken token = default)
    {
        if (maxByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxByteCount));
        }

        ResetTag();

        byte[] stringBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, Math.Min(maxByteCount, 256)));
        int    stringLength = 0;
        try
        {
            while (true)
            {
                try
                {
                    await EnsureBufferedAsync(1, token).ConfigureAwait(false);
                }
                catch (EndOfStreamException exception)
                {
                    throw new EndOfStreamException("The stream ended before the null terminator was found.", exception);
                }

                int available       = _bufferedLength - _offset;
                int terminatorIndex = Array.IndexOf(_backedBuffer, (byte)0, _offset, available);
                int bytesToCopy     = terminatorIndex >= 0 ? terminatorIndex - _offset : available;

                AppendStringBytes(
                    ref stringBuffer,
                    ref stringLength,
                    _backedBuffer,
                    _offset,
                    bytesToCopy,
                    maxByteCount);

                _offset += bytesToCopy;
                if (terminatorIndex < 0) continue;
                ++_offset;
                return Encoding.UTF8.GetString(stringBuffer, 0, stringLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stringBuffer);
        }
    }

    public int ReadInt7Bit()
        => ReadInt7Bit(0);

    public int ReadInt7Bit(int tagBit)
    {
        byte code = ReadFirstCode(tagBit);
        return ReadInt7BitCore(code, tagBit);
    }

    public int ReadInt7Bit(int tagBit, byte previousByte)
    {
        SetTagState(tagBit, previousByte);
        return ReadInt7BitCore(previousByte, tagBit);
    }

    public ValueTask<int> ReadInt7BitAsync(CancellationToken token)
        => ReadInt7BitAsync(0, token);

    public async ValueTask<int> ReadInt7BitAsync(
        int               tagBit,
        CancellationToken token  = default)
    {
        byte code = await ReadFirstCodeAsync(tagBit, token);
        return await ReadInt7BitCoreAsync(code, tagBit, token);
    }

    public ValueTask<int> ReadInt7BitAsync(
        int               tagBit,
        byte              previousByte,
        CancellationToken token = default)
    {
        SetTagState(tagBit, previousByte);
        return ReadInt7BitCoreAsync(previousByte, tagBit, token);
    }

    public long ReadLong7Bit()
        => ReadLong7Bit(0);

    public long ReadLong7Bit(int tagBit)
    {
        byte code = ReadFirstCode(tagBit);
        return ReadLong7BitCore(code, tagBit);
    }

    public long ReadLong7Bit(int tagBit, byte previousByte)
    {
        SetTagState(tagBit, previousByte);
        return ReadLong7BitCore(previousByte, tagBit);
    }

    public ValueTask<long> ReadLong7BitAsync(CancellationToken token = default)
        => ReadLong7BitAsync(0, token);

    public async ValueTask<long> ReadLong7BitAsync(
        int               tagBit,
        CancellationToken token = default)
    {
        byte code = await ReadFirstCodeAsync(tagBit, token);
        return await ReadLong7BitCoreAsync(code, tagBit, token);
    }

    public ValueTask<long> ReadLong7BitAsync(
        int               tagBit,
        byte              previousByte,
        CancellationToken token = default)
    {
        SetTagState(tagBit, previousByte);
        return ReadLong7BitCoreAsync(previousByte, tagBit, token);
    }

    public void ResetTag()
    {
        ThrowIfDisposed();

        TagBitCount  = 0;
        Tag          = 0;
        PreviousByte = 0;
    }

    private static void AppendStringBytes(
        ref byte[] destination,
        ref int    destinationLength,
        byte[]     source,
        int        sourceOffset,
        int        count,
        int        maxByteCount)
    {
        if (count > maxByteCount - destinationLength)
        {
            throw new InvalidDataException($"The null-terminated string exceeds the maximum length of {maxByteCount} bytes.");
        }

        int requiredLength = destinationLength + count;
        if (requiredLength > destination.Length)
        {
            int newLength = destination.Length > int.MaxValue / 2
                ? int.MaxValue
                : destination.Length * 2;

            byte[] replacement = ArrayPool<byte>.Shared.Rent(Math.Max(requiredLength, newLength));
            Buffer.BlockCopy(destination, 0, replacement, 0, destinationLength);
            ArrayPool<byte>.Shared.Return(destination);
            destination = replacement;
        }

        Buffer.BlockCopy(source, sourceOffset, destination, destinationLength, count);
        destinationLength = requiredLength;
    }

    private int ReadInt7BitCore(byte code, int tagBit)
    {
        int value = code & ((1 << (7 - tagBit)) - 1);
        if ((code & (1 << (7 - tagBit))) == 0)
        {
            return value;
        }

        EnsureBuffered(MaxPackedUIntBytes - 1);

        do
        {
            if (value >> (sizeof(int) * 8 - 7) != 0)
            {
                return 0;
            }

            code  = ReadBufferedByte();
            value = (value << 7) | (code & ((1 << 7) - 1));
        }
        while ((code & (1 << 7)) != 0);

        return value;
    }

    private async ValueTask<int> ReadInt7BitCoreAsync(
        byte              code,
        int               tagBit,
        CancellationToken token)
    {
        int value = code & ((1 << (7 - tagBit)) - 1);
        if ((code & (1 << (7 - tagBit))) == 0)
        {
            return value;
        }

        await EnsureBufferedAsync(MaxPackedUIntBytes - 1, token);

        do
        {
            if (value >> (sizeof(int) * 8 - 7) != 0)
            {
                return 0;
            }

            code  = ReadBufferedByte();
            value = (value << 7) | (code & ((1 << 7) - 1));
        }
        while ((code & (1 << 7)) != 0);

        return value;
    }

    private long ReadLong7BitCore(byte code, int tagBit)
    {
        long value = code & ((1 << (7 - tagBit)) - 1);
        if ((code & (1 << (7 - tagBit))) == 0)
        {
            return value;
        }

        EnsureBuffered(MaxPackedUIntBytes - 1);

        do
        {
            if (value >> (sizeof(long) * 8 - 7) != 0)
            {
                return 0;
            }

            code  = ReadBufferedByte();
            value = (value << 7) | (code & (((long)1 << 7) - 1));
        }
        while ((code & (1 << 7)) != 0);

        return value;
    }

    private async ValueTask<long> ReadLong7BitCoreAsync(
        byte              code,
        int               tagBit,
        CancellationToken token)
    {
        long value = code & ((1 << (7 - tagBit)) - 1);
        if ((code & (1 << (7 - tagBit))) == 0)
        {
            return value;
        }

        await EnsureBufferedAsync(MaxPackedUIntBytes - 1, token);

        do
        {
            if (value >> (sizeof(long) * 8 - 7) != 0)
            {
                return 0;
            }

            code  = ReadBufferedByte();
            value = (value << 7) | (code & (((long)1 << 7) - 1));
        }
        while ((code & (1 << 7)) != 0);

        return value;
    }

    private byte ReadFirstCode(int tagBit)
    {
        ValidateTagBit(tagBit);
        EnsureBuffered(MaxPackedUIntBytes);

        byte code = ReadBufferedByte();
        SetTagState(tagBit, code);
        return code;
    }

    private async ValueTask<byte> ReadFirstCodeAsync(
        int               tagBit,
        CancellationToken token)
    {
        ValidateTagBit(tagBit);
        await EnsureBufferedAsync(MaxPackedUIntBytes, token);

        byte code = ReadBufferedByte();
        SetTagState(tagBit, code);
        return code;
    }

    private void SetTagState(int tagBit, byte previousByte)
    {
        ValidateTagBit(tagBit);
        ThrowIfDisposed();

        TagBitCount  = tagBit;
        Tag          = tagBit == 0 ? (byte)0 : (byte)(previousByte >> (8 - tagBit));
        PreviousByte = previousByte;
    }

    private byte ReadBufferedByte() => _offset >= _bufferedLength
        ? throw new EndOfStreamException("The stream ended in the middle of a 7-bit encoded integer.")
        : _backedBuffer[_offset++];

    private void EnsureBuffered(int minimumByteCount)
    {
        ThrowIfDisposed();

        int available = _bufferedLength - _offset;
        if (available >= minimumByteCount)
        {
            return;
        }

        if (available > 0)
        {
            Buffer.BlockCopy(_backedBuffer, _offset, _backedBuffer, 0, available);
        }

        _offset         = 0;
        _bufferedLength = available;
        while (_bufferedLength < minimumByteCount)
        {
            int read = BackedStream.Read(_backedBuffer,
                                         _bufferedLength,
                                         _backedBuffer.Length - _bufferedLength);

            if (read == 0)
            {
                break;
            }
            _bufferedLength += read;
        }

        if (_bufferedLength == 0)
        {
            throw new EndOfStreamException("Unable to read beyond the end of the stream.");
        }
    }

    private async ValueTask EnsureBufferedAsync(
        int               minimumByteCount,
        CancellationToken token)
    {
        ThrowIfDisposed();

        int available = _bufferedLength - _offset;
        if (available >= minimumByteCount)
        {
            return;
        }

        if (available > 0)
        {
            Buffer.BlockCopy(_backedBuffer, _offset, _backedBuffer, 0, available);
        }

        _offset         = 0;
        _bufferedLength = available;

        while (_bufferedLength < minimumByteCount)
        {
#if NET6_0_OR_GREATER
            int read = await BackedStream.ReadAsync(_backedBuffer.AsMemory(_bufferedLength, _backedBuffer.Length - _bufferedLength),
                                                     token).ConfigureAwait(false);
#else
            int read = await BackedStream.ReadAsync(_backedBuffer,
                                                    _bufferedLength,
                                                    _backedBuffer.Length - _bufferedLength,
                                                    token).ConfigureAwait(false);
#endif

            if (read == 0)
            {
                break;
            }
            _bufferedLength += read;
        }

        if (_bufferedLength == 0)
        {
            throw new EndOfStreamException("Unable to read beyond the end of the stream.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(BittableStreamReader));
        }
    }

    private static void ValidateTagBit(int tagBit)
    {
        if ((uint)tagBit > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(tagBit), tagBit, "The tag-bit count must be between 0 and 7.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ArrayPool<byte>.Shared.Return(_backedBuffer);

        if (!_leaveOpen)
        {
            BackedStream.Dispose();
        }
    }

#if NET6_0_OR_GREATER
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        ArrayPool<byte>.Shared.Return(_backedBuffer);

        return _leaveOpen ? ValueTask.CompletedTask : BackedStream.DisposeAsync();
    }
#endif
}
