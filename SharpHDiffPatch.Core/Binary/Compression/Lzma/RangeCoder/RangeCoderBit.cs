using System.Runtime.CompilerServices;

namespace SharpHDiffPatch.Core.Binary.Compression.Lzma.RangeCoder;

internal struct BitDecoder
{
    public const  int  KNumBitModelTotalBits = 11;
    public const  uint KBitModelTotal        = 1 << KNumBitModelTotalBits;
    private const int  KNumMoveBits          = 5;

    private uint _prob;

    public void Init() => _prob = KBitModelTotal >> 1;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Decode(RangeDecoder rangeDecoder)
    {
        uint range       = rangeDecoder.Range;
        uint code        = rangeDecoder.Code;
        uint probability = _prob;
        uint newBound    = (range >> KNumBitModelTotalBits) * probability;
        uint symbol;

        if (code < newBound)
        {
            range       =  newBound;
            probability += (KBitModelTotal - probability) >> KNumMoveBits;
            symbol      =  0;
        }
        else
        {
            range       -= newBound;
            code        -= newBound;
            probability -= probability >> KNumMoveBits;
            symbol      =  1;
        }

        if (range < RangeDecoder.KTopValue)
        {
            code = (code << 8) | rangeDecoder.ReadByte();
            range <<= 8;
        }

        rangeDecoder.Range = range;
        rangeDecoder.Code  = code;
        _prob              = probability;
        return symbol;
    }
}