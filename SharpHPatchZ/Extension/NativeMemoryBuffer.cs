using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpHPatchZ.Extension;

internal sealed unsafe class NativeMemoryBuffer<T> : IDisposable
    where T : unmanaged
{
    private readonly int  _length;
    private          long _pointer;

    public NativeMemoryBuffer(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _length = length;
        if (length == 0)
        {
            return;
        }

        _pointer = (nint)MemoryAlloc.Alloc<T>(length);
        if (_pointer == 0)
        {
            throw new OutOfMemoryException();
        }
    }

    ~NativeMemoryBuffer()
        => Release();

    public int Length => _length;

    public Span<T> Span
    {
        get
        {
            nint pointer = (nint)_pointer;
            if (pointer == 0)
            {
                return _length == 0
                    ? Span<T>.Empty
                    : throw new ObjectDisposedException(nameof(NativeMemoryBuffer<T>));
            }

            return new Span<T>((void*)pointer, _length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetReference()
    {
        nint pointer = (nint)_pointer;
        if (pointer == 0)
        {
            throw new ObjectDisposedException(nameof(NativeMemoryBuffer<T>));
        }

        return ref Unsafe.AsRef<T>((void*)pointer);
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        nint pointer = (nint)Interlocked.Exchange(ref _pointer, 0);
        if (pointer == 0)
        {
            return;
        }

        MemoryAlloc.FreeRaw((void*)pointer);
    }
}
