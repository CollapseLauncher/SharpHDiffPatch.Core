using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using SharpHDiffPatch.Core.Binary.Compression.Lzma.LZ;
using SharpHDiffPatch.Core.Binary.Compression.Lzma.RangeCoder;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma;

internal class Decoder : IDisposable
{
    private class LenDecoder : IDisposable
    {
        private const int KLowModelsCount   = (int)Base.KNumPosStatesMax << Base.KNumLowLenBits;
        private const int KMidModelsOffset  = KLowModelsCount;
        private const int KHighModelsOffset = KMidModelsOffset + ((int)Base.KNumPosStatesMax << Base.KNumMidLenBits);
        private const int KModelsCount      = KHighModelsOffset + (1 << Base.KNumHighLenBits);

        private BitDecoder   _choice;
        private BitDecoder   _choice2;
        private BitDecoder[] _models = [];
        private uint         _numPosStates;

        public void Create(uint numPosStates)
        {
            if (_models.Length == 0)
            {
                _models = ArrayPool<BitDecoder>.Shared.Rent(KModelsCount);
            }
            _numPosStates = numPosStates;
        }

        public void Init()
        {
            _choice.Init();
            _choice2.Init();

            int activeLowModels = (int)_numPosStates << Base.KNumLowLenBits;
            int activeMidModels = (int)_numPosStates << Base.KNumMidLenBits;
            BitDecoder.Init(_models, 0, activeLowModels);
            BitDecoder.Init(_models, KMidModelsOffset, activeMidModels);
            BitDecoder.Init(_models, KHighModelsOffset, 1 << Base.KNumHighLenBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Decode(RangeDecoder rangeDecoder, uint posState)
        {
            if (_choice.Decode(rangeDecoder) == 0)
            {
                int modelOffset = (int)posState << Base.KNumLowLenBits;
                return BitTreeDecoder.Decode(_models, modelOffset, rangeDecoder, Base.KNumLowLenBits);
            }

            uint symbol = Base.KNumLowLenSymbols;
            if (_choice2.Decode(rangeDecoder) == 0)
            {
                int modelOffset = KMidModelsOffset + ((int)posState << Base.KNumMidLenBits);
                symbol += BitTreeDecoder.Decode(_models, modelOffset, rangeDecoder, Base.KNumMidLenBits);
            }
            else
            {
                symbol += Base.KNumMidLenSymbols;
                symbol += BitTreeDecoder.Decode(_models, KHighModelsOffset, rangeDecoder, Base.KNumHighLenBits);
            }
            return symbol;
        }

        public void Dispose()
        {
            BitDecoder[] models = _models;
            _models       = [];
            _numPosStates = 0;

            if (models.Length != 0)
            {
                ArrayPool<BitDecoder>.Shared.Return(models);
            }
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

        public void Init() => BitDecoder.Init(_coders, 0, _modelCount);

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

    private const int KStatePosModelsCount  = (int)Base.KNumStates << Base.KNumPosStatesBitsMax;
    private const int KStateModelsCount     = (int)Base.KNumStates;
    private const int KIsMatchOffset        = 0;
    private const int KIsRepOffset          = KIsMatchOffset + KStatePosModelsCount;
    private const int KIsRepG0Offset        = KIsRepOffset + KStateModelsCount;
    private const int KIsRepG1Offset        = KIsRepG0Offset + KStateModelsCount;
    private const int KIsRepG2Offset        = KIsRepG1Offset + KStateModelsCount;
    private const int KIsRep0LongOffset     = KIsRepG2Offset + KStateModelsCount;
    private const int KPosDecodersOffset    = KIsRep0LongOffset + KStatePosModelsCount;
    private const int KPosDecodersCount     = (int)(Base.KNumFullDistances - Base.KEndPosModelIndex);
    private const int KPosSlotModelsOffset  = KPosDecodersOffset + KPosDecodersCount;
    private const int KPosSlotModelsCount   = (int)Base.KNumLenToPosStates << Base.KNumPosSlotBits;
    private const int KPosAlignModelsOffset = KPosSlotModelsOffset + KPosSlotModelsCount;
    private const int KModelsCount          = KPosAlignModelsOffset + (1 << Base.KNumAlignBits);

    private          BitDecoder[]   _models         = ArrayPool<BitDecoder>.Shared.Rent(KModelsCount);
    private readonly LenDecoder     _lenDecoder     = new();
    private readonly LenDecoder     _repLenDecoder  = new();
    private readonly LiteralDecoder _literalDecoder = new();

    private int _dictionarySize = -1;

    private uint _posStateMask;

    private Base.State _state;
    private uint       _rep0, _rep1, _rep2, _rep3;

    private void CreateDictionary()
    {
        if (_dictionarySize < 0)
        {
            throw new LzmaInvalidParamException();
        }
        _outWindow = new OutWindow();
        int blockSize = Math.Max(_dictionarySize, 1 << 12);
        _outWindow.Create(blockSize);
    }

    private void SetLiteralProperties(int lp, int lc)
    {
        if (lp > 8 || lc > 8)
        {
            throw new LzmaInvalidParamException();
        }

        _literalDecoder.Create(lp, lc);
    }

    private void SetPosBitsProperties(int pb)
    {
        if (pb > Base.KNumPosStatesBitsMax)
        {
            throw new LzmaInvalidParamException();
        }

        uint numPosStates = (uint)1 << pb;
        _lenDecoder.Create(numPosStates);
        _repLenDecoder.Create(numPosStates);
        _posStateMask = numPosStates - 1;
    }

    private void Init()
    {
        BitDecoder.Init(_models, 0, KModelsCount);

        _literalDecoder.Init();
        _lenDecoder.Init();
        _repLenDecoder.Init();

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

        ref BitDecoder models             = ref _models[0];
        ref BitDecoder isMatchDecoders    = ref Unsafe.Add(ref models, KIsMatchOffset);
        ref BitDecoder isRepDecoders      = ref Unsafe.Add(ref models, KIsRepOffset);
        ref BitDecoder isRepG0Decoders    = ref Unsafe.Add(ref models, KIsRepG0Offset);
        ref BitDecoder isRepG1Decoders    = ref Unsafe.Add(ref models, KIsRepG1Offset);
        ref BitDecoder isRepG2Decoders    = ref Unsafe.Add(ref models, KIsRepG2Offset);
        ref BitDecoder isRep0LongDecoders = ref Unsafe.Add(ref models, KIsRep0LongOffset);

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

                    int posSlotOffset = KPosSlotModelsOffset + ((int)Base.GetLenToPosState(len) << Base.KNumPosSlotBits);
                    uint posSlot = BitTreeDecoder.Decode(_models, posSlotOffset, rangeDecoder, Base.KNumPosSlotBits);
                    if (posSlot >= Base.KStartPosModelIndex)
                    {
                        int numDirectBits = (int)((posSlot >> 1) - 1);
                        _rep0 = (2 | (posSlot & 1)) << numDirectBits;

                        if (posSlot < Base.KEndPosModelIndex)
                        {
                            _rep0 += BitTreeDecoder.ReverseDecode(
                                _models,
                                KPosDecodersOffset + _rep0 - posSlot - 1,
                                rangeDecoder,
                                numDirectBits
                            );
                        }
                        else
                        {
                            _rep0 += rangeDecoder.DecodeDirectBits(numDirectBits - Base.KNumAlignBits) << Base.KNumAlignBits;
                            _rep0 += BitTreeDecoder.ReverseDecode(
                                _models,
                                KPosAlignModelsOffset,
                                rangeDecoder,
                                Base.KNumAlignBits);
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
                        : throw new LzmaDataErrorException();
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
            throw new LzmaInvalidParamException();
        }

        int lc        = properties[0] % 9;
        int remainder = properties[0] / 9;
        int lp        = remainder % 5;
        int pb        = remainder / 5;
        if (pb > Base.KNumPosStatesBitsMax)
        {
            throw new LzmaInvalidParamException();
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

    public void Dispose()
    {
        BitDecoder[] models = _models;
        _models = [];

        _lenDecoder.Dispose();
        _repLenDecoder.Dispose();
        _literalDecoder.Dispose();

        if (models.Length != 0)
        {
            ArrayPool<BitDecoder>.Shared.Return(models);
        }
    }
}
