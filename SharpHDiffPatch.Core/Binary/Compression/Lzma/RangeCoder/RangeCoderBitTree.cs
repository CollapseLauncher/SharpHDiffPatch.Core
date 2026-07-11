using System.Runtime.CompilerServices;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma.RangeCoder;

internal readonly struct BitTreeDecoder(int numBitLevels)
{
    private readonly BitDecoder[] _models = new BitDecoder[1 << numBitLevels];

    public void Init()
    {
        ref BitDecoder current = ref _models[1];
        ref BitDecoder end     = ref Unsafe.Add(ref current, _models.Length - 1);

        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            current.Init();
            current = ref Unsafe.Add(ref current, 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Decode(RangeDecoder rangeDecoder)
    {
        ref BitDecoder models = ref _models[0];
        uint           m      = 1;

        for (int bitIndex = numBitLevels; bitIndex > 0; bitIndex--)
        {
            m = (m << 1) + Unsafe.Add(ref models, (int)m).Decode(rangeDecoder);
        }
        return m - ((uint)1 << numBitLevels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReverseDecode(RangeDecoder rangeDecoder)
    {
        ref BitDecoder models = ref _models[0];
        uint           m      = 1;
        uint           symbol = 0;

        for (int bitIndex = 0; bitIndex < numBitLevels; bitIndex++)
        {
            uint bit = Unsafe.Add(ref models, (int)m).Decode(rangeDecoder);
            m      =  (m << 1) + bit;
            symbol |= bit << bitIndex;
        }
        return symbol;
    }

    public static uint ReverseDecode(
        BitDecoder[] models,
        uint         startIndex,
        RangeDecoder rangeDecoder,
        int          numBitLevels)
    {
        ref BitDecoder modelBase = ref models[0];
        uint           m         = 1;
        uint           symbol    = 0;

        for (int bitIndex = 0; bitIndex < numBitLevels; bitIndex++)
        {
            int  modelIndex = unchecked((int)(startIndex + m));
            uint bit        = Unsafe.Add(ref modelBase, modelIndex).Decode(rangeDecoder);
            m      =  (m << 1) + bit;
            symbol |= bit << bitIndex;
        }
        return symbol;
    }
}