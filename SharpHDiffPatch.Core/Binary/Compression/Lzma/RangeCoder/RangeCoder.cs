using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma.RangeCoder;

internal class RangeDecoder : IDisposable
{
    public const  uint KTopValue       = 1 << 24;
    private const int  InputBufferSize = 32 << 10;
    public        uint Range;
    public        uint Code;

    public Stream Stream;
    public long   Total;

    private byte[] _inputBuffer = [];
    private int    _inputOffset;
    private int    _inputCount;
    private long   _inputLimit;
    private bool   _useInputBuffer;

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
        return _inputBuffer[_inputOffset++];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FillInputBuffer()
    {
        long remaining = _inputLimit - Total;
        if (remaining <= 0)
        {
            throw new DataErrorException();
        }

        if (_inputBuffer.Length == 0)
        {
            _inputBuffer = ArrayPool<byte>.Shared.Rent(InputBufferSize);
        }

        int requested = (int)Math.Min(_inputBuffer.Length, remaining);
        _inputCount  = Stream.Read(_inputBuffer, 0, requested);
        _inputOffset = 0;
        if (_inputCount <= 0)
        {
            throw new DataErrorException();
        }
    }

    public void Dispose()
    {
        ReleaseStream();
        byte[] inputBuffer = _inputBuffer;
        _inputBuffer = [];

        if (inputBuffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
        }
    }
}