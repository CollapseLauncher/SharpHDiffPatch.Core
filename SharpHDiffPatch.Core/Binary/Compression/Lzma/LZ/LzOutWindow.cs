using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma.LZ;

internal class OutWindow : IDisposable
{
    private byte[] _buffer = [];
    private int    _windowSize;
    private int    _pos;
    private int    _streamPos;
    private int    _pendingLen;
    private int    _pendingDist;
    private Stream _stream;

    public long Total;
    public long Limit;

    public void Create(int windowSize)
    {
        if (_buffer.Length < windowSize)
        {
            ReturnPooledBuffer();
            _buffer = ArrayPool<byte>.Shared.Rent(windowSize);
        }

        _buffer[windowSize - 1] = 0;
        _windowSize             = windowSize;
        _pos                    = 0;
        _streamPos              = 0;
        _pendingLen             = 0;
        Total                   = 0;
        Limit                   = 0;
    }

    public void Reset() => Create(_windowSize);

    public void Init(Stream stream)
    {
        ReleaseStream();
        _stream = stream;
    }

    public void Train(Stream stream)
    {
        long len  = stream.Length;
        int  size = len < _windowSize ? (int)len : _windowSize;
        stream.Position = len - size;

        Total = 0;
        Limit = size;
        _pos  = _windowSize - size;
        CopyStream(stream, size);

        if (_pos == _windowSize)
        {
            _pos = 0;
        }
        _streamPos = _pos;
    }

    public void ReleaseStream()
    {
        Flush();
        _stream = null;
    }

    public void Flush()
    {
        if (_stream is null)
        {
            return;
        }
        int size = _pos - _streamPos;
        if (size == 0)
        {
            return;
        }

        _stream.Write(_buffer, _streamPos, size);
        if (_pos >= _windowSize)
        {
            _pos = 0;
        }

        _streamPos = _pos;
    }

    public void CopyBlock(int distance, int len)
    {
        int remaining = len;
        int source    = _pos - distance - 1;
        if (source < 0)
        {
            source += _windowSize;
        }

        while (remaining > 0 && _pos < _windowSize && Total < Limit)
        {
            int  copySize  = Math.Min(remaining, _windowSize - _pos);
            long available = Limit - Total;
            if (copySize > available)
            {
                copySize = (int)available;
            }

            ref byte buffer     = ref _buffer[0];
            int      beforeWrap = Math.Min(copySize, _windowSize - source);
            CopyBytes(ref buffer, source, _pos, beforeWrap);
            source += beforeWrap;
            _pos   += beforeWrap;

            int afterWrap = copySize - beforeWrap;
            if (afterWrap > 0)
            {
                source = 0;
                CopyBytes(ref buffer, source, _pos, afterWrap);
                source += afterWrap;
                _pos   += afterWrap;
            }

            remaining -= copySize;
            Total += copySize;
            if (_pos >= _windowSize)
            {
                Flush();
            }
        }

        _pendingLen = remaining;
        _pendingDist = distance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyBytes(ref byte buffer, int sourceOffset, int destinationOffset, int count)
    {
        ref byte source      = ref Unsafe.Add(ref buffer, sourceOffset);
        ref byte destination = ref Unsafe.Add(ref buffer, destinationOffset);

        bool isNonOverlapping = sourceOffset + count <= destinationOffset || destinationOffset + count <= sourceOffset;

        if (count >= 16 && isNonOverlapping)
        {
            ReadOnlySpan<byte> sourceSpan      = new(Unsafe.AsPointer(ref source), count);
            Span<byte>         destinationSpan = new(Unsafe.AsPointer(ref destination), count);

            sourceSpan.CopyTo(destinationSpan);
            return;
        }

        ref byte destinationEnd = ref Unsafe.Add(ref destination, count);
        while (Unsafe.IsAddressLessThan(ref destination, ref destinationEnd))
        {
            destination = source;
            source      = ref Unsafe.Add(ref source,      1);
            destination = ref Unsafe.Add(ref destination, 1);
        }
    }

    public void PutByte(byte b)
    {
        _buffer[_pos++] = b;
        Total++;

        if (_pos >= _windowSize)
        {
            Flush();
        }
    }

    public byte GetByte(int distance)
    {
        int pos = _pos - distance - 1;
        if (pos < 0)
        {
            pos += _windowSize;
        }
        return _buffer[pos];
    }

    public int CopyStream(Stream stream, int len)
    {
        int size = len;
        while (size > 0 && _pos < _windowSize && Total < Limit)
        {
            int curSize = _windowSize - _pos;
            if (curSize > Limit - Total)
            {
                curSize = (int)(Limit - Total);
            }

            if (curSize > size)
            {
                curSize = size;
            }

            int numReadBytes = stream.Read(_buffer, _pos, curSize);
            if (numReadBytes == 0)
            {
                throw new LzmaDataErrorException();
            }

            size  -= numReadBytes;
            _pos  += numReadBytes;
            Total += numReadBytes;
            if (_pos >= _windowSize)
            {
                Flush();
            }
        }
        return len - size;
    }

    public void SetLimit(long size) => Limit = Total + size;

    public bool HasSpace => _pos < _windowSize && Total < Limit;

    public bool HasPending => _pendingLen > 0;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_streamPos >= _pos)
        {
            return 0;
        }

        int size = _pos - _streamPos;
        if (size > count)
        {
            size = count;
        }

        _buffer.AsSpan(_streamPos, size).CopyTo(buffer.AsSpan(offset, size));
        _streamPos += size;
        if (_streamPos < _windowSize) return size;

        _pos       = 0;
        _streamPos = 0;
        return size;
    }

    public void CopyPending()
    {
        if (_pendingLen > 0)
        {
            CopyBlock(_pendingDist, _pendingLen);
        }
    }

    public int AvailableBytes => _pos - _streamPos;

    public void Dispose()
    {
        ReleaseStream();
        ReturnPooledBuffer();
    }

    private void ReturnPooledBuffer()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}