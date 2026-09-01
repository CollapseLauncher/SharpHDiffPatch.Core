using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
#if NET6_0_OR_GREATER
using SharpHPatchZ.Extension;
#endif

namespace SharpHPatchZ.IO.Compression.Lzma.RangeCoder;

internal class RangeDecoder : IDisposable
{
    public const  uint KTopValue       = 1 << 24;
    private const int  InputBufferSize = 32 << 10;
    public        uint Range;
    public        uint Code;

    public Stream Stream;
    public long   Total;

#if NET6_0_OR_GREATER
    private NativeMemoryBuffer<byte>? _inputBuffer;
#else
    private byte[]                    _inputBuffer = [];
#endif
    private int                       _inputOffset;
    private int                       _inputCount;
    private long                      _inputLimit;
    private bool                      _useInputBuffer;

    public void Init(Stream stream, long inputLimit = -1)
    {
        Stream          = stream;
        _inputOffset    = 0;
        _inputCount     = 0;
        _inputLimit     = inputLimit;
        _useInputBuffer = inputLimit >= 0;

        Code = 0;
        Range = 0xFFFFFFFF;
        Total = 0;
        for (int i = 0; i < 5; i++)
        {
            Code = (Code << 8) | ReadByte();
        }
    }

    public void ReleaseStream() => Stream = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Normalize()
    {
        while (Range < KTopValue)
        {
            Code = (Code << 8) | ReadByte();
            Range <<= 8;
        }
    }

    public void Decode(uint start, uint size)
    {
        Code -= start * Range;
        Range *= size;
        Normalize();
    }

    public uint DecodeDirectBits(int numTotalBits)
    {
        uint range  = Range;
        uint code   = Code;
        uint result = 0;
        for (int i = numTotalBits; i > 0; i--)
        {
            range >>= 1;
            uint t = (code - range) >> 31;
            code   -= range & (t - 1);
            result =  (result << 1) | (1 - t);

            if (range >= KTopValue) continue;
            code  =   (code << 8) | ReadByte();
            range <<= 8;
        }

        Range = range;
        Code  = code;
        return result;
    }

    public bool IsFinished => Code == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint ReadByte()
    {
        if (!_useInputBuffer)
        {
            Total++;
            return (byte)Stream.ReadByte();
        }

        if (_inputOffset >= _inputCount)
        {
            FillInputBuffer();
        }

        Total++;
#if NET6_0_OR_GREATER
        return Unsafe.Add(ref _inputBuffer!.GetReference(), _inputOffset++);
#else
        return _inputBuffer[_inputOffset++];
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FillInputBuffer()
    {
        long remaining = _inputLimit - Total;
        if (remaining <= 0)
        {
            throw new LzmaDataErrorException();
        }

#if NET6_0_OR_GREATER
        _inputBuffer ??= new NativeMemoryBuffer<byte>(InputBufferSize);
        int requested = (int)Math.Min(_inputBuffer.Length, remaining);
        _inputCount = Stream.Read(_inputBuffer.Span[..requested]);
#else
        if (_inputBuffer.Length == 0)
        {
            _inputBuffer = ArrayPool<byte>.Shared.Rent(InputBufferSize);
        }

        int requested = (int)Math.Min(_inputBuffer.Length, remaining);
        _inputCount = Stream.Read(_inputBuffer, 0, requested);
#endif
        _inputOffset = 0;
        if (_inputCount <= 0)
        {
            throw new LzmaDataErrorException();
        }
    }

    public void Dispose()
    {
        ReleaseStream();
#if NET6_0_OR_GREATER
        NativeMemoryBuffer<byte>? inputBuffer = _inputBuffer;
        _inputBuffer = null;
        inputBuffer?.Dispose();
#else
        byte[] inputBuffer = _inputBuffer;
        _inputBuffer = [];

        if (inputBuffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
        }
#endif
    }
}
