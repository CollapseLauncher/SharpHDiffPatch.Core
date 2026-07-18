// ReSharper disable CommentTypo

/*
 * Original Code by lassevk
 * https://raw.githubusercontent.com/lassevk/Streams/master/Streams/CombinedStream.cs
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHDiffPatch.Core.Binary.Streams;

public class CombinedStreamSegment<T>
    where T : Stream
{
    public T    Stream { get; set; }
    public long Length { get; set; }
}

/// <summary>
/// This class is a <see cref="Stream"/> descendant that manages multiple underlying
/// streams which are considered to be chained together to one large stream. Only reading
/// and seeking is allowed, writing will throw exceptions.
/// </summary>
public sealed class CombinedStream<T> : Stream
    where T : Stream
{
    private readonly T[]    _underlyingStreams;
    private readonly long[] _streamEnds;

    private readonly bool _leaveOpen;
    private readonly bool _canWrite;

    private long _position;
    private int  _index;
    private bool _disposed;

    /// <summary>
    /// Constructs a new <see cref="CombinedStream{T}"/> on top of the specified array
    /// of streams.
    /// </summary>
    /// <param name="underlyingStreams">
    /// An array of <see cref="Stream"/> objects that will be chained together and
    /// considered to be one big stream.
    /// </param>
    /// <param name="leaveOpen">Keep all underlying streams opened while disposing this current stream.</param>
    public CombinedStream(T[]  underlyingStreams,
                          bool leaveOpen = false)
    {
        if (underlyingStreams == null)
            throw new ArgumentNullException(nameof(underlyingStreams), $"[{nameof(CombinedStream<T>)}()] underlyingStreams");

        if (underlyingStreams.Length == 0)
        {
            throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] At least one stream is required.",
                                        nameof(underlyingStreams));
        }

        _underlyingStreams = underlyingStreams;
        _streamEnds        = new long[underlyingStreams.Length];
        _leaveOpen         = leaveOpen;

        bool canWrite    = true;
        long totalLength = 0;

        for (int i = 0; i < _underlyingStreams.Length; i++)
        {
            Stream stream = _underlyingStreams[i];

            if (stream == null)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] The array contains a null stream.",
                                            nameof(underlyingStreams));
            }

            if (!stream.CanRead)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] Every underlying stream must be readable.",
                                            nameof(underlyingStreams));
            }

            if (!stream.CanSeek)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] Every underlying stream must be seekable.",
                                            nameof(underlyingStreams));
            }

            canWrite &= stream.CanWrite;

            totalLength    = checked(totalLength + stream.Length);
            _streamEnds[i] = totalLength;
        }

        Length    = totalLength;
        _canWrite = canWrite;

#if SHOWMOREDEBUGINFO
        HDiffPatch.Event.PushLog($"[{nameof(CombinedStream<T>)}()] Total length of the CombinedStream: {totalLength} bytes with total of {underlyingStreams.Length} streams", Verbosity.Debug);
#endif
    }

    /// <summary>
    /// Constructs a new <see cref="CombinedStream{T}"/> on top of the specified array
    /// of streams.
    /// </summary>
    /// <param name="underlyingStreams">
    /// An array of <see cref="Stream"/> objects that will be chained together and
    /// considered to be one big stream.
    /// </param>
    /// <param name="leaveOpen">Keep all underlying streams opened while disposing this current stream.</param>
    public CombinedStream(CombinedStreamSegment<T>[] underlyingStreams,
                          bool                    leaveOpen = false)
    {
        if (underlyingStreams == null)
            throw new ArgumentNullException(nameof(underlyingStreams), $"[{nameof(CombinedStream<T>)}()] underlyingStreams");

        if (underlyingStreams.Length == 0)
        {
            throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] At least one stream is required.",
                                        nameof(underlyingStreams));
        }

        _underlyingStreams = new T[underlyingStreams.Length];
        _streamEnds        = new long[underlyingStreams.Length];
        _leaveOpen         = leaveOpen;

        bool canWrite = true;
        long totalLength = 0;

        for (int i = 0; i < underlyingStreams.Length; i++)
        {
            CombinedStreamSegment<T> segment = underlyingStreams[i];

            if (segment == null)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] The array contains a null segment.", nameof(underlyingStreams));
            }

            T stream = segment.Stream;

            if (stream == null)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] A segment contains a null stream.", nameof(underlyingStreams));
            }

            if (segment.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(underlyingStreams), $"[{nameof(CombinedStream<T>)}()] Segment length cannot be negative.");
            }

            if (!stream.CanRead)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] Every underlying stream must be readable.", nameof(underlyingStreams));
            }

            if (!stream.CanSeek)
            {
                throw new ArgumentException($"[{nameof(CombinedStream<T>)}()] Every underlying stream must be seekable.", nameof(underlyingStreams));
            }

            canWrite &= stream.CanWrite;

            _underlyingStreams[i] = stream;

            totalLength = checked(totalLength + segment.Length);
            _streamEnds[i] = totalLength;
        }

        Length = totalLength;
        _canWrite = canWrite;

#if SHOWMOREDEBUGINFO
        HDiffPatch.Event.PushLog($"[{nameof(CombinedStream<T>)}()] Total length of the CombinedStream: {totalLength} bytes with total of {underlyingStreams.Length} streams", Verbosity.Debug);
#endif
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && _canWrite;

    /// <inheritdoc/>
    public override void Flush()
    {
        foreach (T stream in _underlyingStreams)
            stream.Flush();
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_canWrite)
            return;

        foreach (T stream in _underlyingStreams)
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            if (disposing && !_leaveOpen)
            {
                foreach (T stream in _underlyingStreams)
                    stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override long Length { get; }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();

            if ((ulong)value > (ulong)Length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _position = value;
            _index    = FindStreamIndex(_streamEnds, value, Length, _index);
        }
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException($"[{nameof(CombinedStream<T>)}::SetLength] The method or operation is not supported by CombinedStream.");

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (buffer.IsEmpty || _position == Length)
            return 0;

        int totalRead = 0;

        while (!buffer.IsEmpty && _position < Length)
        {
            SkipExhaustedStreams();

            Stream stream        = _underlyingStreams[_index];
            long   streamStart   = GetStreamStart(_streamEnds, _index);
            long   localPosition = _position - streamStart;
            long   available     = _streamEnds[_index] - _position;

            if (available <= 0)
            {
                if (!MoveNextStream())
                    break;

                continue;
            }

            int requested = (int)Math.Min(buffer.Length, available);

            int read;
#if NET6_0_OR_GREATER
            if (stream is FileStream { SafeFileHandle: not null } fileStream)
            {
                read = RandomAccess.Read(fileStream.SafeFileHandle,
                                         buffer[..requested],
                                         localPosition);
                goto Advance;
            }
#endif

            if (stream.Position != localPosition)
                stream.Position = localPosition;

            read = stream.Read(buffer[..requested]);

            if (read == 0)
            {
                if (_position < _streamEnds[_index])
                {
                    throw new EndOfStreamException($"[{nameof(CombinedStream<T>)}::ReadCore] An underlying stream ended before its expected length.");
                }

                if (!MoveNextStream())
                    break;

                continue;
            }

        Advance:
            totalRead += read;
            _position += read;
            buffer    =  buffer[read..];
        }

        return totalRead;
    }
#endif

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();

        return ReadCore(buffer, offset, count);
    }

    private int ReadCore(byte[] buffer, int offset, int count)
    {
        if (count == 0 || _position == Length)
            return 0;

        int totalRead = 0;

        while (count != 0 && _position < Length)
        {
            SkipExhaustedStreams();

            Stream stream        = _underlyingStreams[_index];
            long   streamStart   = GetStreamStart(_streamEnds, _index);
            long   localPosition = _position - streamStart;
            long   available     = _streamEnds[_index] - _position;

            if (available <= 0)
            {
                if (!MoveNextStream())
                    break;

                continue;
            }

            int requested = (int)Math.Min(count, available);

            int read;
#if NET6_0_OR_GREATER
            if (stream is FileStream { SafeFileHandle: not null } fileStream)
            {
                read = RandomAccess.Read(fileStream.SafeFileHandle,
                                         buffer.AsSpan(offset, requested),
                                         localPosition);
                goto Advance;
            }
#endif
            if (stream.Position != localPosition)
                stream.Position = localPosition;

            read = stream.Read(buffer, offset, requested);

            if (read == 0)
            {
                // The FileStream became shorter than the length captured
                // by this CombinedStream, or another owner changed it.
                if (_position < _streamEnds[_index])
                {
                    throw new EndOfStreamException($"[{nameof(CombinedStream<T>)}::ReadCore] An underlying stream ended before its expected length.");
                }

                if (!MoveNextStream())
                    break;

                continue;
            }

        Advance:
            totalRead += read;
            offset    += read;
            count     -= read;
            _position += read;
        }

        return totalRead;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End     => checked(Length + offset),
            _                  => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = position;
        return position;
    }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        EnsureWritable(buffer.Length);

        while (!buffer.IsEmpty)
        {
            SkipExhaustedStreams();

            Stream stream        = _underlyingStreams[_index];
            long   streamStart   = GetStreamStart(_streamEnds, _index);
            long   localPosition = _position - streamStart;
            long   available     = _streamEnds[_index] - _position;

            if (available <= 0)
            {
                if (!MoveNextStream())
                {
                    throw new EndOfStreamException($"[{nameof(CombinedStream<T>)}::Write] The write exceeds the combined stream length.");
                }

                continue;
            }

            int writable = (int)Math.Min(buffer.Length, available);

#if NET6_0_OR_GREATER
            if (stream is FileStream { SafeFileHandle: not null } fileStream)
            {
                RandomAccess.Write(fileStream.SafeFileHandle,
                                   buffer[..writable],
                                   localPosition);
                goto Advance;
            }
#endif

            if (stream.Position != localPosition)
                stream.Position = localPosition;

            stream.Write(buffer[..writable]);

        Advance:
            _position += writable;
            buffer    =  buffer[writable..];
        }
    }
#endif

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();
        EnsureWritable(count);

        WriteCore(buffer, offset, count);
    }

    private void WriteCore(byte[] buffer, int offset, int count)
    {
        while (count != 0)
        {
            SkipExhaustedStreams();

            Stream stream        = _underlyingStreams[_index];
            long   streamStart   = GetStreamStart(_streamEnds, _index);
            long   localPosition = _position - streamStart;
            long   available     = _streamEnds[_index] - _position;

            if (available <= 0)
            {
                if (!MoveNextStream())
                {
                    throw new EndOfStreamException($"[{nameof(CombinedStream<T>)}::WriteCore] The write exceeds the combined stream length.");
                }

                continue;
            }

            int writable = (int)Math.Min(count, available);

#if NET6_0_OR_GREATER
            if (stream is FileStream { SafeFileHandle: not null } fileStream)
            {
                RandomAccess.Write(fileStream.SafeFileHandle,
                                   buffer.AsSpan(offset, writable),
                                   localPosition);
                goto Advance;
            }
#endif

            if (stream.Position != localPosition)
                stream.Position = localPosition;

            stream.Write(buffer, offset, writable);

        Advance:
            offset    += writable;
            count     -= writable;
            _position += writable;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CombinedStream<T>));
    }

#if !NETSTANDARD2_1_OR_GREATER && !NETCOREAPP2_1_OR_GREATER
    private static void ValidateBufferArguments(
        byte[] buffer,
        int    offset,
        int    count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (buffer.Length - offset < count)
            throw new ArgumentException($"[{nameof(CombinedStream<T>)}::ValidateBufferArguments] Offset and count exceed the buffer length.");
    }
#endif

    private void SkipExhaustedStreams()
    {
        while (_index < _underlyingStreams.Length - 1 &&
               _position >= _streamEnds[_index])
        {
            _index++;
        }
    }

    private bool MoveNextStream()
    {
        if (_index >= _underlyingStreams.Length - 1)
            return false;

        _index++;
        return true;
    }

    private void EnsureWritable(int count)
    {
        if (!_canWrite)
            throw new NotSupportedException($"[{nameof(CombinedStream<T>)}::EnsureWritable] The stream is not writable.");

        if (count > Length - _position)
        {
            throw new EndOfStreamException($"[{nameof(CombinedStream<T>)}::EnsureWritable] The write exceeds the combined stream's fixed length.");
        }
    }

    private static long GetStreamStart(long[] streamEnds, int index) => index == 0 ? 0 : streamEnds[index - 1];

    private static int FindStreamIndex(long[] streamEnds, long position, long length, int index)
    {
        int lastIndex = streamEnds.Length - 1;

        // Position == Length represents EOF. Keep the final stream selected,
        // including when it is a zero-length trailing stream.
        if (position == length)
            return lastIndex;

        long currentStart = index == 0 ? 0 : streamEnds[index - 1];
        long currentEnd   = streamEnds[index];

        // Most common case: the new position remains in the current segment.
        if (position >= currentStart && position < currentEnd)
            return index;

        return position >= currentEnd
            ? FindForward(streamEnds, position, index, lastIndex)
            : FindBackward(streamEnds, position, index);
    }

    private static int FindForward(long[] streamEnds, long position, int currentIndex, int lastIndex)
    {
        int nextIndex = currentIndex + 1;

        // Common when reading or seeking across one boundary.
        if (nextIndex <= lastIndex &&
            position < streamEnds[nextIndex])
        {
            return nextIndex;
        }

        int low  = nextIndex;
        int high = nextIndex;
        int step = 1;

        // Find an upper bound exponentially rather than searching the
        // entire remaining range immediately.
        while (high < lastIndex && position >= streamEnds[high])
        {
            low = high + 1;

            int remaining = lastIndex - high;
            int increment = step < remaining ? step : remaining;

            high += increment;

            if (step <= int.MaxValue / 2)
                step <<= 1;
        }

        return FindFirstEndGreaterThan(streamEnds, position, low, high);
    }

    private static int FindBackward(long[] streamEnds, long position, int currentIndex)
    {
        int high = currentIndex - 1;

        // Adjacent segment fast path.
        if (high >= 0)
        {
            long start = high == 0 ? 0 : streamEnds[high - 1];

            if (position >= start && position < streamEnds[high])
                return high;
        }

        int low  = high;
        int step = 1;

        while (low > 0 && position < streamEnds[low - 1])
        {
            int decrement = Math.Min(step, low);
            low -= decrement;

            if (step <= int.MaxValue / 2)
                step <<= 1;
        }

        return FindFirstEndGreaterThan(streamEnds, position, low, high);
    }

    private static int FindFirstEndGreaterThan(
        long[] streamEnds,
        long   position,
        int    low,
        int    high)
    {
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);

            if (streamEnds[middle] > position)
                high = middle;
            else
                low = middle + 1;
        }

        return low;
    }
}