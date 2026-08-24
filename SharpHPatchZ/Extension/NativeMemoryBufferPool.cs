using System;
using System.Collections.Generic;

namespace SharpHPatchZ.Extension;

internal sealed class NativeMemoryBufferPool<T>(int bufferLength) : IDisposable
    where T : unmanaged
{
    private readonly object                       _sync    = new();
    private readonly Stack<NativeMemoryBuffer<T>> _buffers = new();
    private          bool                         _disposed;

    public NativeMemoryBuffer<T> Rent()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeMemoryBufferPool<T>));
            }
            if (_buffers.Count != 0)
            {
                return _buffers.Pop();
            }
        }

        return new NativeMemoryBuffer<T>(bufferLength);
    }

    public void Return(NativeMemoryBuffer<T> buffer)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.Length != bufferLength)
        {
            buffer.Dispose();
            throw new ArgumentException("The buffer has a different length than this pool.", nameof(buffer));
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _buffers.Push(buffer);
                return;
            }
        }

        buffer.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_buffers.Count != 0)
            {
                _buffers.Pop().Dispose();
            }
        }
    }
}
