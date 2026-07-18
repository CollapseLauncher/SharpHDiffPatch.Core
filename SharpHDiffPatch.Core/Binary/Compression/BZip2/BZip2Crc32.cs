using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET6_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming
#endif

namespace SharpHDiffPatch.Core.Binary.Compression.BZip2;

file static class BZip2Crc32Premul
{
    internal const uint InitialState = uint.MaxValue;
    internal const int  HashSize     = sizeof(uint);
    private const  uint Polynomial   = 0x04C11DB7u;

    // Eight concatenated 256-entry tables.
    private static readonly uint[] SCrcLookup = CreateSlicingBy8Lookup();

#if NET6_0_OR_GREATER
    internal const int PclmulThreshold = 128;

    /*
     * P(x) = x^32 + 0x04C11DB7
     *
     * Folding constants:
     *
     * k1 = x^576 mod P = 0x8833794C
     * k2 = x^512 mod P = 0xE6228B11
     * k3 = x^192 mod P = 0xC5B9CD4C
     * k4 = x^128 mod P = 0xE8A45605
     * k5 = x^96  mod P = 0xF200AA66
     * k6 = x^64  mod P = 0x490D678D
     *
     * mu = floor(x^64 / P) over GF(2) = 0x104D101DF
     *
     * Vector128.Create() specifies the low 64-bit lane first.
     */
    private static readonly Vector128<ulong> SK1K2 =
        Vector128.Create(0xE6228B11UL, // k2
                         0x8833794CUL  // k1
                        );

    private static readonly Vector128<ulong> SK3K4 =
        Vector128.Create(0xE8A45605UL, // k4
                         0xC5B9CD4CUL  // k3
                        );

    private static readonly Vector128<ulong> SFoldConstants =
        Vector128.Create(0x04C11DB7UL, // x^32 mod P
                         0xF200AA66UL  // k5
                        );

    private static readonly Vector128<byte> SReverseBytesMask =
        Vector128.Create((byte)15, 14, 13, 12,
                         11, 10, 9, 8,
                         7, 6, 5, 4,
                         3, 2, 1, 0);

    private static readonly Vector128<ulong> SLower64Mask =
        Vector128.Create(ulong.MaxValue, 0UL);

    private static readonly Vector128<ulong> SLow32PerLaneMask =
        Vector128.Create(0xFFFFFFFFUL, 0xFFFFFFFFUL);

    private const ulong K6 = 0x490D678DUL;
    private const ulong Mu = 0x104D101DFUL;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint UpdatePclmul(
        uint               crc,
        ReadOnlySpan<byte> source)
    {
        // The vector path processes complete 16-byte blocks.
        int vectorLength = source.Length & -Vector128<byte>.Count;

        ref byte sourceRef = ref MemoryMarshal.GetReference(source);

        Vector128<ulong> x1;
        int offset;

        if (vectorLength >= 4 * Vector128<byte>.Count)
        {
            // Start four independent 128-bit folding streams.
            x1 = LoadReversed(ref sourceRef);
            Vector128<ulong> x2 = LoadReversed(ref Unsafe.Add(ref sourceRef, 16));
            Vector128<ulong> x3 = LoadReversed(ref Unsafe.Add(ref sourceRef, 32));
            Vector128<ulong> x4 = LoadReversed(ref Unsafe.Add(ref sourceRef, 48));

            offset = 64;

            /*
             * The CRC occupies the most-significant 32 bits of the
             * first reversed 128-bit input block.
             */
            Vector128<ulong> initialCrc = Sse2.ShiftLeftLogical128BitLane(Vector128.CreateScalar((ulong)crc << 32), 8);

            x1 = Sse2.Xor(x1, initialCrc);

            while (vectorLength - offset >= 64)
            {
                Vector128<ulong> y1 = LoadReversed(ref Unsafe.Add(ref sourceRef, offset));
                Vector128<ulong> y2 = LoadReversed(ref Unsafe.Add(ref sourceRef, offset + 16));
                Vector128<ulong> y3 = LoadReversed(ref Unsafe.Add(ref sourceRef, offset + 32));
                Vector128<ulong> y4 = LoadReversed(ref Unsafe.Add(ref sourceRef, offset + 48));

                x1 = FoldPolynomialPair(y1, x1, SK1K2);
                x2 = FoldPolynomialPair(y2, x2, SK1K2);
                x3 = FoldPolynomialPair(y3, x3, SK1K2);
                x4 = FoldPolynomialPair(y4, x4, SK1K2);

                offset += 64;
            }

            // Merge the four parallel streams.
            x1 = FoldPolynomialPair(x2, x1, SK3K4);
            x1 = FoldPolynomialPair(x3, x1, SK3K4);
            x1 = FoldPolynomialPair(x4, x1, SK3K4);
        }
        else
        {
            x1 = LoadReversed(ref sourceRef);
            Vector128<ulong> initialCrc = Sse2.ShiftLeftLogical128BitLane(Vector128.CreateScalar((ulong)crc << 32), 8);
            x1 = Sse2.Xor(x1, initialCrc);

            offset = 16;
        }

        // Fold remaining complete 16-byte blocks.
        while (vectorLength - offset >= 16)
        {
            Vector128<ulong> next = LoadReversed(ref Unsafe.Add(ref sourceRef, offset));

            x1 = FoldPolynomialPair(next, x1, SK3K4);
            offset += 16;
        }

        /*
         * Fold 128 bits down toward 64 bits.
         *
         * SFoldConstants contains:
         *   low  lane: x^32 mod P
         *   high lane: x^96 mod P
         */
        x1 = FoldPolynomialPair(Vector128<ulong>.Zero, x1, SFoldConstants);

        /*
         * Fold 64 bits toward 32 bits:
         *
         *   upper64(x1) * k6 XOR lower64(x1)
         */
        Vector128<ulong> lower64 = Sse2.And(x1, SLower64Mask);
        Vector128<ulong> folded64 = Pclmulqdq.CarrylessMultiply(x1, Vector128.CreateScalar(K6), 0x01);

        x1 = Sse2.Xor(folded64, lower64);

        // Barrett reduction from the remaining polynomial to 32 bits.
        Vector128<ulong> temporary = x1;

        x1 = Sse2.ShiftRightLogical128BitLane(x1, 4);
        x1 = Sse2.And(x1, SLow32PerLaneMask);
        x1 = Pclmulqdq.CarrylessMultiply(x1, Vector128.CreateScalar(Mu), 0x00);
        x1 = Sse2.ShiftRightLogical128BitLane(x1, 4);
        x1 = Sse2.And(x1, SLow32PerLaneMask);
        x1 = Pclmulqdq.CarrylessMultiply(x1, Vector128.CreateScalar((ulong)Polynomial), 0x00);
        x1 = Sse2.Xor(x1, temporary);
        uint reducedCrc = x1.AsUInt32().GetElement(0);

        // Process the final 0–15 bytes using the existing implementation.
        return UpdateSlicingBy8(reducedCrc, source[vectorLength..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadReversed(ref byte source)
    {
        var value = Unsafe.ReadUnaligned<Vector128<byte>>(ref source);
        return Ssse3.Shuffle(value, SReverseBytesMask).AsUInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> FoldPolynomialPair(
        Vector128<ulong> target,
        Vector128<ulong> source,
        Vector128<ulong> constants)
    {
        /*
         * target XOR=
         *     CLMUL(source.low,  constants.low) XOR
         *     CLMUL(source.high, constants.high)
         */
        Vector128<ulong> lowerProduct = Pclmulqdq.CarrylessMultiply(source, constants, 0x00);
        Vector128<ulong> upperProduct = Pclmulqdq.CarrylessMultiply(source, constants, 0x11);
        Vector128<ulong> products     = Sse2.Xor(lowerProduct, upperProduct);
        return Sse2.Xor(target, products);
    }
#endif

    internal static uint UpdateSlicingBy8(
        uint               crc,
        ReadOnlySpan<byte> source)
        // The JIT treats this as a platform constant.
        => BitConverter.IsLittleEndian
            ? UpdateSlicingBy8LittleEndian(crc, source)
            : UpdateSlicingBy8Portable(crc, source);

    private static uint UpdateSlicingBy8LittleEndian(
        uint               crc,
        ReadOnlySpan<byte> source)
    {
        ref byte sourceRef = ref MemoryMarshal.GetReference(source);
        ref uint tableRef = ref SCrcLookup[0];

        int offset = 0;
        int length = source.Length;

        /*
         * Two-block unrolling reduces loop bookkeeping.
         *
         * The two CRC operations are still dependent, so larger
         * unrolling generally provides diminishing returns.
         */
        while (length >= 16)
        {
            ulong block0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sourceRef, offset));
            ulong block1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sourceRef, offset + 8));
            crc = UpdateSlicingBy8Block(crc, block0, ref tableRef);
            crc = UpdateSlicingBy8Block(crc, block1, ref tableRef);

            offset += 16;
            length -= 16;
        }

        if (length >= 8)
        {
            ulong block = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sourceRef, offset));
            crc = UpdateSlicingBy8Block(crc, block, ref tableRef);
            offset += 8;
            length -= 8;
        }

        // Process the remaining 0–7 bytes.
        while (length != 0)
        {
            byte value = Unsafe.Add(ref sourceRef, offset);
            byte index = (byte)((crc >> 24) ^ value);
            crc = (crc << 8) ^ Unsafe.Add(ref tableRef, index);
            offset++;
            length--;
        }

        return crc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint UpdateSlicingBy8Block(
        uint     crc,
        ulong    block,
        ref uint table)
    {
        /*
         * A little-endian ulong load gives:
         *
         * bits  0–7  = source[0]
         * bits  8–15 = source[1]
         * ...
         * bits 56–63 = source[7]
         *
         * ReverseEndianness converts source[0..3] into the
         * big-endian uint required by the non-reflected CRC.
         */
        uint first = crc ^ BinaryPrimitives.ReverseEndianness((uint)block);
        return Unsafe.Add(ref table, 0 * 256 + (byte)(block >> 56)) ^
               Unsafe.Add(ref table, 1 * 256 + (byte)(block >> 48)) ^
               Unsafe.Add(ref table, 2 * 256 + (byte)(block >> 40)) ^
               Unsafe.Add(ref table, 3 * 256 + (byte)(block >> 32)) ^
               Unsafe.Add(ref table, 4 * 256 + (byte)first) ^
               Unsafe.Add(ref table, 5 * 256 + (byte)(first >> 8)) ^
               Unsafe.Add(ref table, 6 * 256 + (byte)(first >> 16)) ^
               Unsafe.Add(ref table, 7 * 256 + (byte)(first >> 24));
    }

    private static uint UpdateSlicingBy8Portable(
        uint               crc,
        ReadOnlySpan<byte> source)
    {
        ref byte sourceRef = ref MemoryMarshal.GetReference(source);
        ref uint tableRef  = ref SCrcLookup[0];

        int offset = 0;
        int length = source.Length;

        while (length >= 8)
        {
            uint first = crc ^
                         ((uint)Unsafe.Add(ref sourceRef, offset) << 24) ^
                         ((uint)Unsafe.Add(ref sourceRef, offset + 1) << 16) ^
                         ((uint)Unsafe.Add(ref sourceRef, offset + 2) << 8) ^
                         Unsafe.Add(ref sourceRef, offset + 3);

            crc = Unsafe.Add(ref tableRef, 0 * 256 + Unsafe.Add(ref sourceRef, offset + 7)) ^
                  Unsafe.Add(ref tableRef, 1 * 256 + Unsafe.Add(ref sourceRef, offset + 6)) ^
                  Unsafe.Add(ref tableRef, 2 * 256 + Unsafe.Add(ref sourceRef, offset + 5)) ^
                  Unsafe.Add(ref tableRef, 3 * 256 + Unsafe.Add(ref sourceRef, offset + 4)) ^
                  Unsafe.Add(ref tableRef, 4 * 256 + (byte)first) ^
                  Unsafe.Add(ref tableRef, 5 * 256 + (byte)(first >> 8)) ^
                  Unsafe.Add(ref tableRef, 6 * 256 + (byte)(first >> 16)) ^
                  Unsafe.Add(ref tableRef, 7 * 256 + (byte)(first >> 24));

            offset += 8;
            length -= 8;
        }

        while (length != 0)
        {
            byte index = (byte)((crc >> 24) ^ Unsafe.Add(ref sourceRef, offset));
            crc = (crc << 8) ^ Unsafe.Add(ref tableRef, index);
            offset++;
            length--;
        }

        return crc;
    }

    private static uint[] CreateSlicingBy8Lookup()
    {
        uint[] table = new uint[8 * 256];

        // T0: CRC of one byte in the most-significant position.
        for (uint value = 0; value < 256; value++)
        {
            uint crc = value << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
            }

            table[value] = crc;
        }

        /*
         * For a non-reflected, left-shifting CRC:
         *
         * T[n][x] = advance T[n - 1][x] by one zero byte.
         */
        for (int slice = 1; slice < 8; slice++)
        {
            int previousOffset = (slice - 1) * 256;
            int currentOffset  = slice * 256;

            for (int value = 0; value < 256; value++)
            {
                uint crc = table[previousOffset + value];
                table[currentOffset + value] = (crc << 8) ^ table[(byte)(crc >> 24)];
            }
        }

        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint UpdateByte(uint crc, byte value)
    {
        ref uint table = ref SCrcLookup[0];

        byte index = (byte)((crc >> 24) ^ value);
        return (crc << 8) ^ Unsafe.Add(ref table, index);
    }
}

public sealed class BZip2Crc32 : NonCryptographicHashAlgorithm
{
    private uint _crc = BZip2Crc32Premul.InitialState;

    public BZip2Crc32()
        : base(BZip2Crc32Premul.HashSize)
    {
    }

    private BZip2Crc32(uint crc)
        : base(BZip2Crc32Premul.HashSize)
    {
        _crc = crc;
    }

    public BZip2Crc32 Clone() => new(_crc);

    public void AppendByte(byte value) => _crc = BZip2Crc32Premul.UpdateByte(_crc, value);

    public override void Append(ReadOnlySpan<byte> source) => _crc = Update(_crc, source);

    public override void Reset() => _crc = BZip2Crc32Premul.InitialState;

    protected override void GetCurrentHashCore(Span<byte> destination) => BinaryPrimitives.WriteUInt32BigEndian(destination, ~_crc);

    protected override void GetHashAndResetCore(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, ~_crc);
        _crc = BZip2Crc32Premul.InitialState;
    }

    public uint GetCurrentHashAsUInt32() => ~_crc;

    private static uint Update(uint crc, ReadOnlySpan<byte> source)
    {
#if NET6_0_OR_GREATER
        if (BitConverter.IsLittleEndian &&
            Pclmulqdq.IsSupported &&
            Ssse3.IsSupported &&
            source.Length >= BZip2Crc32Premul.PclmulThreshold)
        {
            return BZip2Crc32Premul.UpdatePclmul(crc, source);
        }
#endif

        return BZip2Crc32Premul.UpdateSlicingBy8(crc, source);
    }
}