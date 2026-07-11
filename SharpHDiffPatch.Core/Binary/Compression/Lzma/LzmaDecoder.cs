using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using SharpHDiffPatch.Core.Binary.Compression.Lzma.LZ;
using SharpHDiffPatch.Core.Binary.Compression.Lzma.RangeCoder;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma;

internal class Decoder : IDisposable
{
    private class LenDecoder
    {
        private          BitDecoder       _choice;
        private          BitDecoder       _choice2;
        private readonly BitTreeDecoder[] _lowCoder  = new BitTreeDecoder[Base.KNumPosStatesMax];
        private readonly BitTreeDecoder[] _midCoder  = new BitTreeDecoder[Base.KNumPosStatesMax];
        private readonly BitTreeDecoder   _highCoder = new(Base.KNumHighLenBits);
        private          uint             _numPosStates;

        public void Create(uint numPosStates)
        {
            for (uint posState = _numPosStates; posState < numPosStates; posState++)
            {
                _lowCoder[posState] = new BitTreeDecoder(Base.KNumLowLenBits);
                _midCoder[posState] = new BitTreeDecoder(Base.KNumMidLenBits);
            }
            _numPosStates = numPosStates;
        }

        public void Init()
        {
            _choice.Init();
            for (uint posState = 0; posState < _numPosStates; posState++)
            {
                _lowCoder[posState].Init();
                _midCoder[posState].Init();
            }
            _choice2.Init();
            _highCoder.Init();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Decode(RangeDecoder rangeDecoder, uint posState)
        {
            if (_choice.Decode(rangeDecoder) == 0)
            {
                return Unsafe.Add(ref _lowCoder[0], (int)posState).Decode(rangeDecoder);
            }

            uint symbol = Base.KNumLowLenSymbols;
            if (_choice2.Decode(rangeDecoder) == 0)
            {
                symbol += Unsafe.Add(ref _midCoder[0], (int)posState).Decode(rangeDecoder);
            }
            else
            {
                symbol += Base.KNumMidLenSymbols;
                symbol += _highCoder.Decode(rangeDecoder);
            }
            return symbol;
        }
    }

    private class LiteralDecoder : IDisposable
    {
        private const int KNumModelsPerState = 0x300;

        private BitDecoder[] _coders;
        private int          _modelCount;
        private int          _numPrevBits;
        private int          _numPosBits;
        private uint         _posMask;

        public void Create(int numPosBits, int numPrevBits)
        {
            if (_coders != null && _numPrevBits == numPrevBits && _numPosBits == numPosBits)
            {
                return;
            }

            _numPosBits  = numPosBits;
            _posMask     = ((uint)1 << numPosBits) - 1;
            _numPrevBits = numPrevBits;

            int numStates  = 1 << (_numPrevBits + _numPosBits);
            int modelCount = checked(numStates * KNumModelsPerState);

            ReturnModels();
            _coders     = ArrayPool<BitDecoder>.Shared.Rent(modelCount);
            _modelCount = modelCount;
        }

        public void Init()
        {
            ref BitDecoder current = ref _coders[0];
            ref BitDecoder end     = ref Unsafe.Add(ref current, _modelCount);

            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                current.Init();
                current = ref Unsafe.Add(ref current, 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint GetState(uint pos, byte prevByte) => ((pos & _posMask) << _numPrevBits) + (uint)(prevByte >> (8 - _numPrevBits));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte DecodeNormal(RangeDecoder rangeDecoder, uint pos, byte prevByte)
        {
            ref BitDecoder decoders = ref Unsafe.Add(
                ref _coders[0],
                (int)GetState(pos, prevByte) * KNumModelsPerState);
            uint symbol = 1;
            do
            {
                symbol = (symbol << 1) | Unsafe.Add(ref decoders, (int)symbol).Decode(rangeDecoder);
            } while (symbol < 0x100);
            return unchecked((byte)symbol);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte DecodeWithMatchByte(
            RangeDecoder rangeDecoder,
            uint         pos,
            byte         prevByte,
            byte         matchByte)
        {
            ref BitDecoder decoders = ref Unsafe.Add(
                ref _coders[0],
                (int)GetState(pos, prevByte) * KNumModelsPerState);
            uint symbol = 1;
            do
            {
                uint matchBit = (uint)(matchByte >> 7) & 1;
                matchByte <<= 1;

                int  modelIndex = (int)(((1 + matchBit) << 8) + symbol);
                uint bit        = Unsafe.Add(ref decoders, modelIndex).Decode(rangeDecoder);
                symbol = (symbol << 1) | bit;

                if (matchBit == bit) continue;

                while (symbol < 0x100)
                {
                    symbol = (symbol << 1) | Unsafe.Add(ref decoders, (int)symbol).Decode(rangeDecoder);
                }
                break;
            } while (symbol < 0x100);
            return unchecked((byte)symbol);
        }

        public void Dispose() => ReturnModels();

        private void ReturnModels()
        {
            if (_coders == null) return;

            ArrayPool<BitDecoder>.Shared.Return(_coders);
            _coders     = null;
            _modelCount = 0;
        }
    }

    private OutWindow _outWindow;

    private readonly BitDecoder[] _isMatchDecoders    = new BitDecoder[Base.KNumStates << Base.KNumPosStatesBitsMax];
    private readonly BitDecoder[] _isRepDecoders      = new BitDecoder[Base.KNumStates];
    private readonly BitDecoder[] _isRepG0Decoders    = new BitDecoder[Base.KNumStates];
    private readonly BitDecoder[] _isRepG1Decoders    = new BitDecoder[Base.KNumStates];
    private readonly BitDecoder[] _isRepG2Decoders    = new BitDecoder[Base.KNumStates];
    private readonly BitDecoder[] _isRep0LongDecoders = new BitDecoder[Base.KNumStates << Base.KNumPosStatesBitsMax];

    private readonly BitDecoder[]     _posDecoders = new BitDecoder[Base.KNumFullDistances - Base.KEndPosModelIndex];
    private readonly BitTreeDecoder[] _posSlotDecoder  = new BitTreeDecoder[Base.KNumLenToPosStates];
    private readonly BitTreeDecoder   _posAlignDecoder = new(Base.KNumAlignBits);

    private readonly LenDecoder     _lenDecoder     = new();
    private readonly LenDecoder     _repLenDecoder  = new();
    private readonly LiteralDecoder _literalDecoder = new();

    private int _dictionarySize;

    private uint _posStateMask;

    private Base.State _state;
    private uint       _rep0, _rep1, _rep2, _rep3;

    public Decoder()
    {
        _dictionarySize = -1;
        for (int i = 0; i < Base.KNumLenToPosStates; i++)
        {
            _posSlotDecoder[i] = new BitTreeDecoder(Base.KNumPosSlotBits);
        }
    }

    private void CreateDictionary()
    {
        if (_dictionarySize < 0)
        {
            throw new InvalidParamException();
        }
        _outWindow = new OutWindow();
        int blockSize = Math.Max(_dictionarySize, 1 << 12);
        _outWindow.Create(blockSize);
    }

    private void SetLiteralProperties(int lp, int lc)
    {
        if (lp > 8 || lc > 8)
        {
            throw new InvalidParamException();
        }

        _literalDecoder.Create(lp, lc);
    }

    private void SetPosBitsProperties(int pb)
    {
        if (pb > Base.KNumPosStatesBitsMax)
        {
            throw new InvalidParamException();
        }

        uint numPosStates = (uint)1 << pb;
        _lenDecoder.Create(numPosStates);
        _repLenDecoder.Create(numPosStates);
        _posStateMask = numPosStates - 1;
    }

    private void Init()
    {
        uint i;
        for (i = 0; i < Base.KNumStates; i++)
        {
            for (uint j = 0; j <= _posStateMask; j++)
            {
                uint index = (i << Base.KNumPosStatesBitsMax) + j;
                _isMatchDecoders[index].Init();
                _isRep0LongDecoders[index].Init();
            }

            _isRepDecoders[i].Init();
            _isRepG0Decoders[i].Init();
            _isRepG1Decoders[i].Init();
            _isRepG2Decoders[i].Init();
        }

        _literalDecoder.Init();
        for (i = 0; i < Base.KNumLenToPosStates; i++)
        {
            _posSlotDecoder[i].Init();
        }

        for (i = 0; i < Base.KNumFullDistances - Base.KEndPosModelIndex; i++)
        {
            _posDecoders[i].Init();
        }

        _lenDecoder.Init();
        _repLenDecoder.Init();
        _posAlignDecoder.Init();

        _state.Init();
        _rep0 = 0;
        _rep1 = 0;
        _rep2 = 0;
        _rep3 = 0;
    }

    public void Code(
        Stream inStream,
        Stream outStream,
        long   inSize,
        long   outSize)
    {
        if (_outWindow is null)
        {
            CreateDictionary();
        }

        if (_outWindow != null)
        {
            _outWindow.Init(outStream);
            if (outSize > 0)
            {
                _outWindow.SetLimit(outSize);
            }
            else
            {
                _outWindow.SetLimit(long.MaxValue - _outWindow.Total);
            }

            RangeDecoder rangeDecoder = new();
            rangeDecoder.Init(inStream, inSize);

            Code(_dictionarySize, _outWindow, rangeDecoder);

            _outWindow.Dispose();
            rangeDecoder.Dispose();
        }

        _outWindow = null;
    }

    internal bool Code(int dictionarySize, OutWindow outWindow, RangeDecoder rangeDecoder)
    {
        int dictionarySizeCheck = Math.Max(dictionarySize, 1);

        outWindow.CopyPending();

        ref BitDecoder     isMatchDecoders    = ref _isMatchDecoders[0];
        ref BitDecoder     isRepDecoders      = ref _isRepDecoders[0];
        ref BitDecoder     isRepG0Decoders    = ref _isRepG0Decoders[0];
        ref BitDecoder     isRepG1Decoders    = ref _isRepG1Decoders[0];
        ref BitDecoder     isRepG2Decoders    = ref _isRepG2Decoders[0];
        ref BitDecoder     isRep0LongDecoders = ref _isRep0LongDecoders[0];
        ref BitTreeDecoder posSlotDecoders    = ref _posSlotDecoder[0];

        while (outWindow.HasSpace)
        {
            uint posState      = (uint)outWindow.Total & _posStateMask;
            int  stateIndex    = (int)_state.Index;
            int  statePosIndex = (stateIndex << Base.KNumPosStatesBitsMax) + (int)posState;

            if (Unsafe.Add(ref isMatchDecoders, statePosIndex).Decode(rangeDecoder) == 0)
            {
                byte prevByte = outWindow.GetByte(0);
                byte b = !_state.IsCharState()
                    ? _literalDecoder.DecodeWithMatchByte(rangeDecoder, (uint)outWindow.Total, prevByte, outWindow.GetByte((int)_rep0))
                    : _literalDecoder.DecodeNormal(rangeDecoder, (uint)outWindow.Total, prevByte);

                outWindow.PutByte(b);
                _state.UpdateChar();
            }
            else
            {
                uint len;
                if (Unsafe.Add(ref isRepDecoders, stateIndex).Decode(rangeDecoder) == 1)
                {
                    if (Unsafe.Add(ref isRepG0Decoders, stateIndex).Decode(rangeDecoder) == 0)
                    {
                        if (Unsafe.Add(ref isRep0LongDecoders, statePosIndex).Decode(rangeDecoder) == 0)
                        {
                            _state.UpdateShortRep();
                            outWindow.PutByte(outWindow.GetByte((int)_rep0));
                            continue;
                        }
                    }
                    else
                    {
                        uint distance;
                        if (Unsafe.Add(ref isRepG1Decoders, stateIndex).Decode(rangeDecoder) == 0)
                        {
                            distance = _rep1;
                        }
                        else
                        {
                            if (Unsafe.Add(ref isRepG2Decoders, stateIndex).Decode(rangeDecoder) == 0)
                            {
                                distance = _rep2;
                            }
                            else
                            {
                                distance = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = distance;
                    }
                    len = _repLenDecoder.Decode(rangeDecoder, posState) + Base.KMatchMinLen;
                    _state.UpdateRep();
                }
                else
                {
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;

                    len = Base.KMatchMinLen + _lenDecoder.Decode(rangeDecoder, posState);
                    _state.UpdateMatch();

                    uint posSlot = Unsafe.Add(ref posSlotDecoders, (int)Base.GetLenToPosState(len)).Decode(rangeDecoder);
                    if (posSlot >= Base.KStartPosModelIndex)
                    {
                        int numDirectBits = (int)((posSlot >> 1) - 1);
                        _rep0 = (2 | (posSlot & 1)) << numDirectBits;

                        if (posSlot < Base.KEndPosModelIndex)
                        {
                            _rep0 += BitTreeDecoder.ReverseDecode(
                                _posDecoders,
                                _rep0 - posSlot - 1,
                                rangeDecoder,
                                numDirectBits
                            );
                        }
                        else
                        {
                            _rep0 += rangeDecoder.DecodeDirectBits(numDirectBits - Base.KNumAlignBits) << Base.KNumAlignBits;
                            _rep0 += _posAlignDecoder.ReverseDecode(rangeDecoder);
                        }
                    }
                    else
                    {
                        _rep0 = posSlot;
                    }
                }
                if (_rep0 >= outWindow.Total || _rep0 >= dictionarySizeCheck)
                {
                    return _rep0 == 0xFFFFFFFF
                        ? true
                        : throw new DataErrorException();
                }
                outWindow.CopyBlock((int)_rep0, (int)len);
            }
        }
        return false;
    }

    public void SetDecoderProperties(byte[] properties)
    {
        if (properties.Length < 1)
        {
            throw new InvalidParamException();
        }

        int lc        = properties[0] % 9;
        int remainder = properties[0] / 9;
        int lp        = remainder % 5;
        int pb        = remainder / 5;
        if (pb > Base.KNumPosStatesBitsMax)
        {
            throw new InvalidParamException();
        }

        SetLiteralProperties(lp, lc);
        SetPosBitsProperties(pb);
        Init();

        if (properties.Length < 5) return;

        _dictionarySize = 0;
        for (int i = 0; i < 4; i++)
        {
            _dictionarySize += properties[1 + i] << (i * 8);
        }
    }

    public void Train(Stream stream)
    {
        if (_outWindow is null)
        {
            CreateDictionary();
        }

        _outWindow?.Train(stream);
    }

    public void Dispose() => _literalDecoder.Dispose();
}