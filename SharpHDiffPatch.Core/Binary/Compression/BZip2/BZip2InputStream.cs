using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

// ReSharper disable CommentTypo

namespace SharpHDiffPatch.Core.Binary.Compression.BZip2;

/// <summary>
/// An input stream that decompresses files in the BZip2 format.
/// </summary>
public sealed class BZip2InputStream : Stream
{
    private const int InputBufferSize      = 32 << 10;
    private const int MaximumHuffmanLength = 20;
    private const int AlphaTableStride     = BZip2Constants.MaximumAlphaSize;
    private const int CodeTableStride      = BZip2Constants.MaximumCodeLength;

    private readonly bool   _decompressConcatenated;
    private readonly bool   _leaveOpen;
    private readonly Stream _baseStream;

    // Buffer compressed input. The original implementation called Stream.ReadByte()
    // for every compressed byte, which is particularly expensive for unbuffered streams.
    private readonly byte[] _inputBuffer;
    private          int    _inputOffset;
    private          int    _inputCount;

    private int  _last;
    private int  _blockSize100K;
    private bool _blockRandomised;

    private uint _bsBuff;
    private int  _bsLive;

    private readonly BZip2Crc32 _nCrc32 = new();

    private readonly byte[] _seqToUnseq;
    private          byte[] _selector;

    // Reused per block. This removes the allocations previously made by
    // ReceiveDecodingTables() and GetAndMoveToFrontDecode().
    private          byte[] _yy;
    private readonly byte[] _codeLengths;

    private int[]    _tt  = [];
    private byte[]   _ll8 = [];
    private ushort[] _perm;

    private readonly int[]    _unZfTab;
    private readonly int[]    _limit     = new int[BZip2Constants.GroupCount * CodeTableStride];
    private readonly int[]    _baseArray = new int[BZip2Constants.GroupCount * CodeTableStride];
    private readonly byte[]   _minLens   = new byte[BZip2Constants.GroupCount];

    private bool _streamEnd;
    private bool _blockEndPending;
    private int  _currentChar = -1;

    private BZip2Constants.DecodeState _currentState = BZip2Constants.DecodeState.StartBlock;

    private uint _storedBlockCrc;
    private uint _computedCombinedCrc;

    private byte   _count;
    private ushort _chPrev;
    private ushort _ch2;
    private int    _tPos;
    private ushort _rNToGo;
    private ushort _rTPos;
    private int    _i2;
    private byte   _j2;
    private byte   _z;

    private readonly int  _inputBufferCapacity;
    private          bool _disposed;

    private static long TryGetStreamLength(Stream stream)
    {
        try
        {
            return stream.Length;
        }
        catch
        {
            return -1;
        }
    }

    public BZip2InputStream(Stream stream, bool decompressConcatenated, bool leaveOpen = false)
    {
        if (stream == null!)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        _baseStream             = stream;
        _decompressConcatenated = decompressConcatenated;
        _leaveOpen              = leaveOpen;

        try
        {
            long streamLength = TryGetStreamLength(stream);
            _inputBufferCapacity =
                streamLength is > 0 and < InputBufferSize
                    ? (int)streamLength
                    : InputBufferSize;

            _yy          = ArrayPool<byte>.Shared.Rent(256);
            _seqToUnseq  = ArrayPool<byte>.Shared.Rent(256);
            _unZfTab     = ArrayPool<int>.Shared.Rent(256);
            _inputBuffer = ArrayPool<byte>.Shared.Rent(_inputBufferCapacity);
            _selector    = ArrayPool<byte>.Shared.Rent(BZip2Constants.MaximumSelectors);
            _codeLengths = ArrayPool<byte>.Shared.Rent(BZip2Constants.GroupCount * AlphaTableStride);
            _perm        = ArrayPool<ushort>.Shared.Rent(BZip2Constants.GroupCount * AlphaTableStride);

            Initialize();

            if (_streamEnd)
            {
                return;
            }

            int origPtr = InitBlock();
            if (!_streamEnd)
            {
                SetupBlock(origPtr);
            }
        }
        catch
        {
            ReturnPooledBuffers();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;

        ReturnPooledBuffers();

        if (disposing && IsStreamOwner && !_leaveOpen)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ReturnPooledBuffers()
    {
        if (_yy.Length != 0) ArrayPool<byte>.Shared.Return(_yy);
        if (_inputBuffer.Length != 0) ArrayPool<byte>.Shared.Return(_inputBuffer);
        if (_selector.Length != 0) ArrayPool<byte>.Shared.Return(_selector);
        if (_codeLengths.Length != 0) ArrayPool<byte>.Shared.Return(_codeLengths);
        if (_perm.Length != 0) ArrayPool<ushort>.Shared.Return(_perm);
        if (_ll8.Length != 0) ArrayPool<byte>.Shared.Return(_ll8);
        if (_tt.Length != 0) ArrayPool<int>.Shared.Return(_tt);
        if (_unZfTab.Length != 0) ArrayPool<int>.Shared.Return(_unZfTab);
        if (_seqToUnseq.Length != 0) ArrayPool<byte>.Shared.Return(_seqToUnseq);
    }

    public bool IsStreamOwner { get; set; } = true;

    public override bool CanRead  => _baseStream.CanRead;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _baseStream.Length;

    public override long Position
    {
        get => throw new NotSupportedException("Cannot get the position of the compressed data");
        set => throw new NotSupportedException("BZip2InputStream position cannot be set");
    }

    public override void Flush() => _baseStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("BZip2InputStream Seek not supported");

    public override void SetLength(long value) => throw new NotSupportedException("BZip2InputStream SetLength not supported");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("BZip2InputStream Write not supported");

    public override void WriteByte(byte value) => throw new NotSupportedException("BZip2InputStream WriteByte not supported");

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if ((uint)offset > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return (uint)count > (uint)(buffer.Length - offset)
            ? throw new ArgumentOutOfRangeException(nameof(count))
            : ReadCore(buffer.AsSpan(offset, count));
    }

#if NET6_0_OR_GREATER
    public override int Read(Span<byte> buffer) => ReadCore(buffer);
#endif

    private int ReadCore(Span<byte> destination)
    {
        int written  = 0;
        int crcStart = 0;

        // Huffman decoding and inverse-BWT traversal are inherently serial, but the
        // second RLE stage frequently exposes byte runs. Emit those runs in bulk and
        // feed the CRC one span at a time instead of one byte at a time.
        while (written < destination.Length && !_streamEnd)
        {
            if (_blockEndPending)
            {
                AppendCrc(destination.Slice(crcStart, written - crcStart));
                crcStart = written;
                FinishBlock();
                continue;
            }

            if (_currentState is BZip2Constants.DecodeState.NoRandPartC
                              or BZip2Constants.DecodeState.RandPartC)
            {
                int available = _z - _j2 + 1; // Includes the already prepared current byte.
                int toWrite   = Math.Min(available, destination.Length - written);

                destination.Slice(written, toWrite).Fill((byte)_currentChar);
                written += toWrite;

                if (toWrite < available)
                {
                    // Leave the next repeated byte prepared for the following Read().
                    _j2 += (byte)toWrite;
                    break;
                }

                if (_currentState == BZip2Constants.DecodeState.RandPartC)
                {
                    CompleteRandPartC();
                }
                else
                {
                    CompleteNoRandPartC();
                }

                continue;
            }

            destination[written++] = (byte)_currentChar;
            AdvanceState();
        }

        AppendCrc(destination.Slice(crcStart, written - crcStart));

        // Match the old implementation's timing: consuming the final byte of a block
        // also validates that block and prepares the next one before Read() returns.
        if (_blockEndPending)
        {
            FinishBlock();
        }

        return written;
    }

    public override int ReadByte()
    {
        if (_streamEnd)
        {
            return -1;
        }

        int value = _currentChar;
        _nCrc32.AppendByte((byte)value);
        AdvanceState();

        if (_blockEndPending)
        {
            FinishBlock();
        }

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCrc(ReadOnlySpan<byte> source)
    {
        if (!source.IsEmpty)
        {
            _nCrc32.Append(source);
        }
    }

    private void FinishBlock()
    {
        _blockEndPending = false;
        EndBlock();

        int origPtr = InitBlock();
        if (!_streamEnd)
        {
            SetupBlock(origPtr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceState()
    {
        switch (_currentState)
        {
            case BZip2Constants.DecodeState.RandPartB:
                SetupRandPartB();
                break;

            case BZip2Constants.DecodeState.RandPartC:
                SetupRandPartC();
                break;

            case BZip2Constants.DecodeState.NoRandPartB:
                SetupNoRandPartB();
                break;

            case BZip2Constants.DecodeState.NoRandPartC:
                SetupNoRandPartC();
                break;

            case BZip2Constants.DecodeState.StartBlock:
            case BZip2Constants.DecodeState.RandPartA:
            case BZip2Constants.DecodeState.NoRandPartA:
                break;
        }
    }

    private void Initialize()
    {
        int magic1 = BsR(8);
        int magic2 = BsR(8);
        int magic3 = BsR(8);
        int magic4 = BsR(8);

        if (magic1 != 'B' ||
            magic2 != 'Z' ||
            magic3 != 'h' ||
            magic4 < '1' ||
            magic4 > '9')
        {
            _streamEnd = true;
            return;
        }

        SetDecompressStructureSizes(magic4 - '0');
        _computedCombinedCrc = 0;
    }

    private int InitBlock()
    {
        int origPtr = 0;
        while (true)
        {
            int magic1 = BsR(8);
            int magic2 = BsR(8);
            int magic3 = BsR(8);
            int magic4 = BsR(8);
            int magic5 = BsR(8);
            int magic6 = BsR(8);

            if (magic1 == 0x17 && magic2 == 0x72 && magic3 == 0x45 &&
                magic4 == 0x38 && magic5 == 0x50 && magic6 == 0x90)
            {
                if (Complete())
                {
                    return origPtr;
                }

                // Complete() consumed the next member's BZh header. Continue by
                // reading that member's first block marker.
                continue;
            }

            if (magic1 != 0x31 || magic2 != 0x41 || magic3 != 0x59 ||
                magic4 != 0x26 || magic5 != 0x53 || magic6 != 0x59)
            {
                BadBlockHeader();
            }

            _storedBlockCrc  = unchecked((uint)BsGetInt32());
            _blockRandomised = BsR1() != 0;

            origPtr = GetAndMoveToFrontDecode();

            _nCrc32.Reset();
            _currentState = BZip2Constants.DecodeState.StartBlock;
            return origPtr;
        }
    }

    private void EndBlock()
    {
        uint computedBlockCrc = _nCrc32.GetCurrentHashAsUInt32();
        if (_storedBlockCrc != computedBlockCrc)
        {
            CrcError();
        }

        _computedCombinedCrc =
            BitOperations.RotateLeft(_computedCombinedCrc, 1) ^
            computedBlockCrc;
    }

    private bool Complete()
    {
        if (unchecked((uint)BsGetInt32()) != _computedCombinedCrc)
        {
            CrcError();
        }

        if (!_decompressConcatenated)
        {
            // Bulk input may have prefetched data following the first member. Restore
            // the underlying position when possible so callers can continue reading it.
            RewindUnreadInput();
            _streamEnd = true;
            return true;
        }

        // Each bzip2 member is padded to the next byte boundary. At this point the
        // remaining bits in the cache are only that padding.
        _bsBuff = 0;
        _bsLive = 0;

        if (TryInitializeNextMember()) return false;

        _streamEnd = true;
        return true;
        }

    private bool TryInitializeNextMember()
    {
        if (!TryReadCompressedByte(out int magic1))
        {
            return false;
        }

        int magic2 = ReadCompressedByte();
        int magic3 = ReadCompressedByte();
        int magic4 = ReadCompressedByte();

        if (magic1 != 'B' ||
            magic2 != 'Z' ||
            magic3 != 'h')
        {
            throw new BZip2Exception("Unexpected data after a valid BZip2 stream");
        }

        if (magic4 is < '1' or > '9')
        {
            throw new BZip2Exception("Invalid BZip2 block size");
        }

        SetDecompressStructureSizes(magic4 - '0');
        _computedCombinedCrc = 0;
        return true;
    }

    private void RewindUnreadInput()
    {
        int unread = _inputCount - _inputOffset;
        if (unread != 0 && _baseStream.CanSeek)
        {
            _baseStream.Seek(-unread, SeekOrigin.Current);
        }

        _inputOffset = 0;
        _inputCount  = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FillBuffer()
    {
        _bsBuff = (_bsBuff << 8) | (uint)ReadCompressedByte();
        _bsLive += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadCompressedByte()
    {
        if (!TryReadCompressedByte(out int value))
        {
            CompressedStreamEof();
        }

        return value;
    }

    private bool TryReadCompressedByte(out int value)
    {
        if (_inputOffset >= _inputCount)
        {
            // A non-seekable stream must not be read past the end of the first member
            // when concatenation is disabled. Seekable streams are rewound in Complete().
            int requested = _blockSize100K == 0 ||
                            (!_decompressConcatenated && !_baseStream.CanSeek)
                ? 1
                : _inputBufferCapacity;

            _inputCount  = _baseStream.Read(_inputBuffer, 0, requested);
            _inputOffset = 0;

            if (_inputCount <= 0)
            {
                value = -1;
                return false;
            }
        }

        value = _inputBuffer[_inputOffset++];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BsR1()
    {
        if (_bsLive == 0) FillBuffer();
        return (int)((_bsBuff >> --_bsLive) & 1u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BsR(int bitCount)
    {
        while (_bsLive < bitCount)
        {
            FillBuffer();
        }

        int  shift = _bsLive - bitCount;
        uint mask  = (1u << bitCount) - 1u;
        int  value = (int)((_bsBuff >> shift) & mask);
        _bsLive = shift;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BsGetInt32() => (BsR(16) << 16) | BsR(16);

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    private void ReceiveDecodingTables(out int nInUse,
                                       out int alphaSize,
                                       out int selectorCount)
    {
        nInUse = 0;

        // The format stores sixteen group-presence bits followed by sixteen symbol
        // bits for every present group. Reading them in 16-bit chunks removes up to
        // 272 calls to BsR1() per block.
        int inUse16 = BsR(16);
        for (int group = 0; group < 16; group++)
        {
            if ((inUse16 & (0x8000 >> group)) == 0)
            {
                continue;
            }

            int inUseGroup = BsR(16);
            int symbolBase = group << 4;

            while (inUseGroup != 0)
            {
                int bit = BitOperations.LeadingZeroCount((uint)inUseGroup) - 16;
                _seqToUnseq[nInUse++] = (byte)(symbolBase + bit);

                inUseGroup &= ~(0x8000 >> bit);
            }
        }

        int nGroups = BsR(3);
        alphaSize     = nInUse + 2;
        selectorCount = BsR(15);

        if (nGroups < 2 || nGroups > BZip2Constants.GroupCount ||
            selectorCount < 1 || selectorCount > BZip2Constants.MaximumSelectors)
        {
            DataError();
        }

        Span<byte> selectorPositionInitial = [0, 1, 2, 3, 4, 5];

        // Decode selector MTF values immediately, avoiding selectorMtf[] entirely.
        for (int i = 0; i < selectorCount; i++)
        {
            int selectorMtf = 0;
            while (BsR1() != 0)
            {
                selectorMtf++;
                if (selectorMtf >= nGroups)
                {
                    DataError();
                }
            }

            ref byte selectorPosition = ref selectorPositionInitial[0];
            byte     selected         = Unsafe.Add(ref selectorPosition, selectorMtf);

            while (selectorMtf != 0)
            {
                Unsafe.Add(ref selectorPosition,     selectorMtf) =
                    Unsafe.Add(ref selectorPosition, selectorMtf - 1);

                selectorMtf--;
            }

            selectorPosition = selected;
            _selector[i]     = selected;
        }

        for (int table = 0; table < nGroups; table++)
        {
            int alphaOffset   = table * AlphaTableStride;
            int currentLength = BsR(5);

            for (int symbol = 0; symbol < alphaSize; symbol++)
            {
                while (BsR1() != 0)
                {
                    currentLength += BsR1() == 0 ? 1 : -1;
                    if ((uint)(currentLength - 1) >= MaximumHuffmanLength)
                    {
                        DataError();
                    }
                }

                if ((uint)(currentLength - 1) >= MaximumHuffmanLength)
                {
                    DataError();
                }

                _codeLengths[alphaOffset + symbol] = (byte)currentLength;
            }
        }

        for (int table = 0; table < nGroups; table++)
        {
            int alphaOffset = table * AlphaTableStride;
            int minLength   = MaximumHuffmanLength;
            int maxLength   = 0;

            ref byte codeLength =
                ref Unsafe.Add(ref _codeLengths[0], alphaOffset);

            int remaining = alphaSize;

            while (remaining != 0)
            {
                int length = codeLength;

                minLength = Math.Min(minLength, length);
                maxLength = Math.Max(maxLength, length);

                codeLength = ref Unsafe.Add(ref codeLength, 1);
                remaining--;
            }

            HbCreateDecodeTables(table, minLength, maxLength, alphaSize);

            _minLens[table] = (byte)minLength;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeNextSymbol(
        ref int groupNumber,
        ref int groupPosition,
        int     selectorCount,
        int     alphaSize)
    {
        if (groupPosition == 0)
        {
            int nextGroup = groupNumber + 1;
            if ((uint)nextGroup >= (uint)selectorCount)
            {
                DataError();
            }

            groupNumber   = nextGroup;
            groupPosition = BZip2Constants.GroupSize;
        }

        groupPosition--;

        ref byte selectorRef = ref _selector[0];
        int      table       = Unsafe.Add(ref selectorRef, groupNumber);

        int codeOffset  = table * CodeTableStride;
        int alphaOffset = table * AlphaTableStride;

        ref int    limitRef = ref _limit[0];
        ref int    baseRef  = ref _baseArray[0];
        ref ushort permRef  = ref _perm[0];
        ref byte   minRef   = ref _minLens[0];

        int codeLength = Unsafe.Add(ref minRef, table);
        int codeBits   = BsR(codeLength);

        while (codeBits > Unsafe.Add(ref limitRef, codeOffset + codeLength))
        {
            if (codeLength >= MaximumHuffmanLength)
            {
                DataError();
            }

            codeLength++;
            codeBits = (codeBits << 1) | BsR1();
        }

        int permIndex = codeBits - Unsafe.Add(ref baseRef, codeOffset + codeLength);
        if ((uint)permIndex >= (uint)alphaSize)
        {
            DataError();
        }

        return Unsafe.Add(ref permRef, alphaOffset + permIndex);
    }

    private int GetAndMoveToFrontDecode()
    {
        int blockCapacity = BZip2Constants.BaseBlockSize * _blockSize100K;
        int origPtr       = BsR(24);

        ReceiveDecodingTables(out int nInUse,
                              out int alphaSize,
                              out int selectorCount);

        int endOfBlock    = nInUse + 1;
        int groupNumber   = -1;
        int groupPosition = 0;

        BZip2Constants.IdentityMtf.CopyTo(_yy);

        _last = -1;
        int nextSymbol = DecodeNextSymbol(ref groupNumber, ref groupPosition, selectorCount, alphaSize);

        while (nextSymbol != endOfBlock)
        {
            if (nextSymbol is BZip2Constants.RunA or BZip2Constants.RunB)
            {
                int runLength = -1;
                int runPower  = 1;

                do
                {
                    if (runPower > blockCapacity)
                    {
                        BlockOverrun();
                    }

                    runLength += nextSymbol == BZip2Constants.RunA
                        ? runPower
                        : runPower << 1;

                    runPower   <<= 1;
                    nextSymbol =   DecodeNextSymbol(ref groupNumber, ref groupPosition, selectorCount, alphaSize);
                }
                while (nextSymbol is BZip2Constants.RunA or BZip2Constants.RunB);

                runLength++;
                int runStart = _last + 1;

                if (nInUse == 0 || runLength < 0 || runLength > blockCapacity - runStart)
                {
                    BlockOverrun();
                }

                byte value = _seqToUnseq[_yy[0]];
                _unZfTab[value] += runLength;

                // Span.Fill is lowered to an optimized bulk fill by modern runtimes
                // and uses SIMD when profitable. System.Memory supplies it on netstandard2.0.
                _ll8.AsSpan(runStart, runLength).Fill(value);
                _last += runLength;
                continue;
            }

            int mtfIndex = nextSymbol - 1;
            if ((uint)mtfIndex >= (uint)nInUse)
            {
                DataError();
            }

            int nextIndex = _last + 1;
            if (nextIndex >= blockCapacity)
            {
                BlockOverrun();
            }

            byte mtfValue     = _yy[mtfIndex];
            byte valueAtIndex = _seqToUnseq[mtfValue];

            _last = nextIndex;
            _unZfTab[valueAtIndex]++;
            _ll8[nextIndex] = valueAtIndex;

            if (mtfIndex != 0)
            {
                // Small MTF indexes dominate normal data, and a short scalar move avoids
                // the fixed cost of entering the runtime copy helper. Larger moves use
                // overlap-safe memmove, whose implementation selects the best CPU path.
                if (mtfIndex <= 16)
                {
                    ref byte yy = ref _yy[0];

                    int moveIndex = mtfIndex;
                    do
                    {
                        Unsafe.Add(ref yy, moveIndex) = Unsafe.Add(ref yy, moveIndex - 1);
                    }
                    while (--moveIndex != 0);
                }
                else
                {
                    _yy.AsSpan(0, mtfIndex).CopyTo(_yy.AsSpan(1));
                }

                _yy[0] = mtfValue;
            }

            nextSymbol = DecodeNextSymbol(ref groupNumber, ref groupPosition, selectorCount, alphaSize);
        }

        return origPtr;
    }

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    private void SetupBlock(int origPtr)
    {
        int last = _last;

        if (last < 0 || (uint)origPtr > (uint)last)
        {
            DataError();
        }

        Span<int> cumulativeFrequency = stackalloc int[257];

        ref int frequency = ref cumulativeFrequency[0];
        ref int counts    = ref _unZfTab[0];

        frequency = 0;
        int cumulative = 0;

        for (int symbol = 0; symbol < 256; symbol++)
        {
            ref int count = ref Unsafe.Add(ref counts, symbol);

            cumulative                            += count;
            Unsafe.Add(ref frequency, symbol + 1) =  cumulative;

            count = 0;
        }

        int length = last + 1;
        if (length >= 256 * 1024)
        {
            BuildTransformationTable4Way(cumulativeFrequency, length);
        }
        else
        {
            BuildTransformationTableScalar(cumulativeFrequency, length);
        }

        _tPos  = _tt[origPtr];
        _count = 0;
        _i2    = 0;
        _ch2   = 256;

        if (_blockRandomised)
        {
            _rNToGo = 0;
            _rTPos  = 0;
            SetupRandPartA();
        }
        else
        {
            SetupNoRandPartA();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BuildTransformationTableScalar(
        Span<int> cumulativeFrequency,
        int       length)
    {
        ref byte source      = ref _ll8[0];
        ref int  destination = ref _tt[0];
        ref int  cursors     = ref cumulativeFrequency[0];

        ref byte current = ref source;

        int index     = 0;
        int remaining = length;

        while (remaining != 0)
        {
            byte    value  = current;
            ref int cursor = ref Unsafe.Add(ref cursors, value);

            Unsafe.Add(ref destination, cursor) = index;
            cursor++;

            current = ref Unsafe.Add(ref current, 1);
            index++;
            remaining--;
        }
    }

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    private void BuildTransformationTable4Way(
        Span<int> cumulativeFrequency,
        int       length)
    {
        const int symbolCount = 256;
        const int chunkCount  = 4;

        // 4 × 256 local histograms and 4 × 256 independent cursors.
        Span<int> chunkCounts  = stackalloc int[chunkCount * symbolCount];
        Span<int> chunkCursors = stackalloc int[chunkCount * symbolCount];
        chunkCounts.Clear();

        int chunkSize = length / chunkCount;

        const int start0 = 0;
        int       start2 = chunkSize * 2;
        int       start3 = chunkSize * 3;

        ref byte ll8    = ref _ll8[0];
        ref int  counts = ref chunkCounts[0];

        // First pass: calculate one histogram per contiguous chunk.
        CountChunk(ref ll8, start0,    chunkSize, ref Unsafe.Add(ref counts, 0 * symbolCount));
        CountChunk(ref ll8, chunkSize, start2,    ref Unsafe.Add(ref counts, 1 * symbolCount));
        CountChunk(ref ll8, start2,    start3,    ref Unsafe.Add(ref counts, 2 * symbolCount));
        CountChunk(ref ll8, start3,    length,    ref Unsafe.Add(ref counts, 3 * symbolCount));

        ref int globalStart = ref cumulativeFrequency[0];
        ref int cursors     = ref chunkCursors[0];

        // Assign a disjoint output range to each chunk for every symbol.
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int position = Unsafe.Add(ref globalStart, symbol);
            Unsafe.Add(ref cursors, 0 * symbolCount + symbol) = position;
            position += Unsafe.Add(ref counts, 0 * symbolCount + symbol);
            Unsafe.Add(ref cursors, 1 * symbolCount + symbol) = position;
            position += Unsafe.Add(ref counts, 1 * symbolCount + symbol);
            Unsafe.Add(ref cursors, 2 * symbolCount + symbol) = position;
            position += Unsafe.Add(ref counts, 2 * symbolCount + symbol);
            Unsafe.Add(ref cursors, 3 * symbolCount + symbol) = position;
        }

        ref int tt = ref _tt[0];

        // The four scatter loops no longer share cursor state.
        ScatterChunk(ref ll8, ref tt, start0,    chunkSize, ref Unsafe.Add(ref cursors, 0 * symbolCount));
        ScatterChunk(ref ll8, ref tt, chunkSize, start2,    ref Unsafe.Add(ref cursors, 1 * symbolCount));
        ScatterChunk(ref ll8, ref tt, start2,    start3,    ref Unsafe.Add(ref cursors, 2 * symbolCount));
        ScatterChunk(ref ll8, ref tt, start3,    length,    ref Unsafe.Add(ref cursors, 3 * symbolCount));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountChunk(
        ref byte source,
        int      start,
        int      end,
        ref int  counts)
    {
        ref byte current   = ref Unsafe.Add(ref source, start);
        int      remaining = end - start;

        while (remaining >= 4)
        {
            byte value0 = current;
            byte value1 = Unsafe.Add(ref current, 1);
            byte value2 = Unsafe.Add(ref current, 2);
            byte value3 = Unsafe.Add(ref current, 3);

            Unsafe.Add(ref counts, value0)++;
            Unsafe.Add(ref counts, value1)++;
            Unsafe.Add(ref counts, value2)++;
            Unsafe.Add(ref counts, value3)++;

            current = ref Unsafe.Add(ref current, 4);
            remaining -= 4;
        }

        while (remaining != 0)
        {
            Unsafe.Add(ref counts, current)++;
            current = ref Unsafe.Add(ref current, 1);
            remaining--;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterChunk(
        ref byte source,
        ref int  destination,
        int      start,
        int      end,
        ref int  cursors)
    {
        ref byte current = ref Unsafe.Add(ref source, start);

        int index     = start;
        int remaining = end - start;

        while (remaining != 0)
        {
            byte    value  = current;
            ref int cursor = ref Unsafe.Add(ref cursors, value);

            Unsafe.Add(ref destination, cursor) = index;
            cursor++;

            current = ref Unsafe.Add(ref current, 1);
            index++;
            remaining--;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupRandPartA()
    {
        if (_i2 <= _last)
        {
            _chPrev = _ch2;
            _ch2    = _ll8[_tPos];
            _tPos   = _tt[_tPos];

            if (_rNToGo == 0)
            {
                _rNToGo = BZip2Constants.RandomNumbers[_rTPos++];
                if (_rTPos == 512)
                {
                    _rTPos = 0;
                }
            }

            _rNToGo--;
            _ch2 ^= (ushort)(_rNToGo == 1 ? 1 : 0);
            _i2++;

            _currentChar  = _ch2;
            _currentState = BZip2Constants.DecodeState.RandPartB;

            return;
        }

        _currentState    = BZip2Constants.DecodeState.StartBlock;
        _blockEndPending = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupNoRandPartA()
    {
        if (_i2 <= _last)
        {
            _chPrev = _ch2;
            _ch2    = _ll8[_tPos];
            _tPos   = _tt[_tPos];
            _i2++;

            _currentChar  = _ch2;
            _currentState = BZip2Constants.DecodeState.NoRandPartB;
            return;
        }

        _currentState    = BZip2Constants.DecodeState.StartBlock;
        _blockEndPending = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupRandPartB()
    {
        if (_ch2 != _chPrev)
        {
            _currentState = BZip2Constants.DecodeState.RandPartA;
            _count        = 1;
            SetupRandPartA();
            return;
        }

        _count++;
        if (_count < 4)
        {
            _currentState = BZip2Constants.DecodeState.RandPartA;
            SetupRandPartA();
            return;
        }

        _z    = _ll8[_tPos];
        _tPos = _tt[_tPos];

        if (_rNToGo == 0)
        {
            _rNToGo = BZip2Constants.RandomNumbers[_rTPos++];
            if (_rTPos == 512)
            {
                _rTPos = 0;
            }
        }

        _rNToGo--;
        _z            ^= (byte)(_rNToGo == 1 ? 1 : 0);
        _j2           =  0;
        _currentState =  BZip2Constants.DecodeState.RandPartC;
        SetupRandPartC();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupRandPartC()
    {
        if (_j2 < _z)
        {
            _currentChar = _ch2;
            _j2++;
            return;
        }

        CompleteRandPartC();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompleteRandPartC()
    {
        _currentState = BZip2Constants.DecodeState.RandPartA;
        _i2++;
        _count = 0;
        SetupRandPartA();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupNoRandPartB()
    {
        if (_ch2 != _chPrev)
        {
            _currentState = BZip2Constants.DecodeState.NoRandPartA;
            _count        = 1;
            SetupNoRandPartA();
            return;
        }

        _count++;
        if (_count < 4)
        {
            _currentState = BZip2Constants.DecodeState.NoRandPartA;
            SetupNoRandPartA();
            return;
        }

        _z            = _ll8[_tPos];
        _tPos         = _tt[_tPos];
        _currentState = BZip2Constants.DecodeState.NoRandPartC;
        _j2           = 0;
        SetupNoRandPartC();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupNoRandPartC()
    {
        if (_j2 < _z)
        {
            _currentChar = _ch2;
            _j2++;
            return;
        }

        CompleteNoRandPartC();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompleteNoRandPartC()
    {
        _i2++;
        _currentState = BZip2Constants.DecodeState.NoRandPartA;
        _count        = 0;
        SetupNoRandPartA();
    }

    private void SetDecompressStructureSizes(int newSize100K)
    {
        if ((uint)(newSize100K - 1) >= 9)
        {
            throw new BZip2Exception("Invalid block size");
        }

        _blockSize100K = newSize100K;
        int requiredLength = BZip2Constants.BaseBlockSize * newSize100K;

        if (_ll8.Length >= requiredLength && _tt.Length >= requiredLength)
        {
            return;
        }

        byte[] newLl8 = ArrayPool<byte>.Shared.Rent(requiredLength);
        int[]  newTt  = ArrayPool<int>.Shared.Rent(requiredLength);

        byte[] oldLl8 = _ll8;
        int[]  oldTt  = _tt;

        _ll8 = newLl8;
        _tt  = newTt;

        if (oldLl8.Length != 0) ArrayPool<byte>.Shared.Return(oldLl8);
        if (oldTt.Length != 0) ArrayPool<int>.Shared.Return(oldTt);
    }

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    private void HbCreateDecodeTables(
        int table,
        int minLength,
        int maxLength,
        int alphaSize)
    {
        int codeOffset  = table * CodeTableStride;
        int alphaOffset = table * AlphaTableStride;

        Span<int> lengthCounts = stackalloc int[CodeTableStride];
        lengthCounts.Clear();

        ref int  countRef   = ref lengthCounts[0];
        ref byte lengthsRef = ref Unsafe.Add(ref _codeLengths[0], alphaOffset);

        for (int symbol = 0; symbol < alphaSize; symbol++)
        {
            int length = Unsafe.Add(ref lengthsRef, symbol);
            Unsafe.Add(ref countRef, length)++;
        }

        Span<int> positions = stackalloc int[CodeTableStride];

        ref int positionRef = ref positions[0];
        int permutationIndex = alphaOffset;

        for (int length = minLength; length <= maxLength; length++)
        {
            Unsafe.Add(ref positionRef, length) = permutationIndex;
            permutationIndex += Unsafe.Add(ref countRef, length);
        }

        ref ushort permRef = ref _perm[0];

        // Stable scatter preserves ascending symbol order within each length.
        for (int symbol = 0; symbol < alphaSize; symbol++)
        {
            int     length   = Unsafe.Add(ref lengthsRef, symbol);
            ref int position = ref Unsafe.Add(ref positionRef, length);

            Unsafe.Add(ref permRef, position++) = (ushort)symbol;
        }

        // Build baseArray directly from the already available counts.
        ref int baseRef = ref Unsafe.Add(ref _baseArray[0], codeOffset);

        baseRef = 0;

        for (int length = 1; length < CodeTableStride; length++)
        {
            Unsafe.Add(ref baseRef, length) = Unsafe.Add(ref baseRef,  length - 1) + Unsafe.Add(ref countRef, length - 1);
        }

        ref int limitRef = ref Unsafe.Add(ref _limit[0], codeOffset);
        int code = 0;
        for (int length = minLength; length <= maxLength; length++)
        {
            code += Unsafe.Add(ref baseRef, length + 1) - Unsafe.Add(ref baseRef, length);
            Unsafe.Add(ref limitRef, length) = code - 1;
            code <<= 1;
        }

        for (int length = minLength + 1; length <= maxLength; length++)
        {
            Unsafe.Add(ref baseRef, length) = ((Unsafe.Add(ref limitRef, length - 1) + 1) << 1) - Unsafe.Add(ref baseRef, length);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompressedStreamEof() => throw new EndOfStreamException("BZip2 input stream end of compressed stream");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BlockOverrun() => throw new BZip2Exception("BZip2 input stream block overrun");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BadBlockHeader() => throw new BZip2Exception("BZip2 input stream bad block header");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CrcError() => throw new BZip2Exception("BZip2 input stream crc error");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DataError() => throw new BZip2Exception("Bzip data error");
}