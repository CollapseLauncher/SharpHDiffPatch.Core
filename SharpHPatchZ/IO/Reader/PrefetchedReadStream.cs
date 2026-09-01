using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.IO.Reader;

internal sealed class PrefetchedReadStream : Stream
{
    private readonly Stream                          _source;
    private readonly int                             _bufferSize;
    private readonly BlockingCollection<BufferChunk> _queue;
    private readonly CancellationTokenSource         _disposeCancellation = new();
    private readonly Task                            _producer;

    private ExceptionDispatchInfo? _producerFailure;
    private BufferChunk?           _current;
    private int                    _currentOffset;
    private int                    _disposed;

    internal PrefetchedReadStream(Stream source,
                                  int    bufferSize,
                                  int    queueCapacity = 2)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        _bufferSize = bufferSize > 0
            ? bufferSize
            : throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _queue = new BlockingCollection<BufferChunk>(queueCapacity > 0
            ? queueCapacity
            : throw new ArgumentOutOfRangeException(nameof(queueCapacity)));

        _producer = Task.Factory.StartNew(Produce,
                                          CancellationToken.None,
                                          TaskCreationOptions.LongRunning,
                                          TaskScheduler.Default);
    }

    public override bool CanRead  => Volatile.Read(ref _disposed) == 0;
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
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || buffer.Length - offset < count) throw new ArgumentOutOfRangeException(nameof(count));

        return ReadCore(buffer.AsSpan(offset, count));
    }

#if NET6_0_OR_GREATER
    public override int Read(Span<byte> buffer)
        => ReadCore(buffer);
#endif

    private int ReadCore(Span<byte> destination)
    {
        ThrowIfDisposed();
        int totalRead = 0;

        while (!destination.IsEmpty)
        {
            if (_current is null)
            {
                try
                {
                    if (!_queue.TryTake(out BufferChunk? chunk,
                                        Timeout.Infinite,
                                        _disposeCancellation.Token))
                    {
                        _producerFailure?.Throw();
                        break;
                    }

                    _current       = chunk;
                    _currentOffset = 0;
                }
                catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(PrefetchedReadStream));
                }
            }

            BufferChunk current = _current!;
            int step = Math.Min(destination.Length, current.Length - _currentOffset);
            current.Buffer.Span.Slice(_currentOffset, step).CopyTo(destination);
            _currentOffset += step;
            totalRead      += step;
            destination     = destination[step..];

            if (_currentOffset != current.Length)
            {
                continue;
            }

            current.Dispose();
            _current       = null;
            _currentOffset = 0;
        }

        return totalRead;
    }

    private void Produce()
    {
#if !NET6_0_OR_GREATER
        byte[] streamBuffer = BigArrayPool<byte>.Shared.Rent(_bufferSize);
#endif
        try
        {
            while (!_disposeCancellation.IsCancellationRequested)
            {
                NativeMemoryBuffer<byte> buffer = new(_bufferSize);
                try
                {
#if NET6_0_OR_GREATER
                    int read = _source.Read(buffer.Span);
#else
                    int read = _source.Read(streamBuffer, 0, streamBuffer.Length);
                    streamBuffer.AsSpan(0, read).CopyTo(buffer.Span);
#endif
                    if (read == 0)
                    {
                        buffer.Dispose();
                        break;
                    }

                    _queue.Add(new BufferChunk(buffer, read), _disposeCancellation.Token);
                }
                catch
                {
                    buffer.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _producerFailure,
                                        ExceptionDispatchInfo.Capture(exception),
                                        null);
        }
        finally
        {
#if !NET6_0_OR_GREATER
            BigArrayPool<byte>.Shared.Return(streamBuffer);
#endif
            try
            {
                _source.Dispose();
            }
            catch (Exception exception)
            {
                if (!_disposeCancellation.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref _producerFailure,
                                                ExceptionDispatchInfo.Capture(exception),
                                                null);
                }
            }
            finally
            {
                _queue.CompleteAdding();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing)
        {
            _disposeCancellation.Cancel();
            // Produce owns _source and disposes it after its final read.
            _producer.GetAwaiter().GetResult();

            _current?.Dispose();
            _current = null;
            while (_queue.TryTake(out BufferChunk? chunk))
            {
                chunk.Dispose();
            }

            _queue.Dispose();
            _disposeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PrefetchedReadStream));
        }
    }

    private sealed class BufferChunk(NativeMemoryBuffer<byte> buffer, int length) : IDisposable
    {
        public NativeMemoryBuffer<byte> Buffer { get; } = buffer;
        public int                      Length { get; } = length;

        public void Dispose()
            => Buffer.Dispose();
    }
}
