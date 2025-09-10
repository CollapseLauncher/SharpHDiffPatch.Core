using SharpHDiffPatch.Core.Patch;
using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpHDiffPatch.Core.Binary
{
    internal static class BinaryExtensions
    {
#if NETSTANDARD2_0 || !NET7_0_OR_GREATER
        public static int ReadExactly(this Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0) return totalRead;

                totalRead += read;
            }

            return totalRead;
        }
#endif

        public static string ReadStringToNull(this Stream reader, int bufferSize = 512)
        {
            int currentValue;
            int i = 0;

            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            byte[] stringBuffer = pool.Rent(bufferSize);

            while (stringBuffer.Length > i && (currentValue = reader.ReadByte()) != 0)
            {
                stringBuffer[i++] = (byte)currentValue;
            }

            return Encoding.UTF8.GetString(stringBuffer, 0, i);
        }

        public static int ReadInt7Bit(this Stream inputStream, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)inputStream.ReadByte();
            int value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) == 0) return value;
            do
            {
                if (value >> (4 * 4 - 7) != 0) return 0;
                code = (byte)inputStream.ReadByte();
                value = (value << 7) | (code & ((1 << 7) - 1));
            }
            while ((code & (1 << 7)) != 0);
            return value;
        }

        public static long ReadLong7Bit(this Stream inputStream, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)inputStream.ReadByte();
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) == 0) return value;
            do
            {
                if (value >> (8 * 8 - 7) != 0) return 0;
                code = (byte)inputStream.ReadByte();
                value = (value << 7) | (code & (((long)1 << 7) - 1));
            }
            while ((code & (1 << 7)) != 0);
            return value;
        }

        public static long ReadLong7Bit(this byte[] inputBuffer, ref int offset, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : inputBuffer[offset++];
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) == 0) return value;

            do
            {
                if (value >> (8 * 8 - 7) != 0) return 0;
                code = inputBuffer[offset++];
                value = (value << 7) | (code & (((long)1 << 7) - 1));
            }
            while ((code & (1 << 7)) != 0);
            return value;
        }

        public static long ReadLong7Bit(this ReadOnlySpan<byte> inputBuffer, ref int offset, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : inputBuffer[offset++];
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) == 0) return value;

            do
            {
                if (value >> (8 * 8 - 7) != 0) return 0;
                code = inputBuffer[offset++];
                value = (value << 7) | (code & (((long)1 << 7) - 1));
            }
            while ((code & (1 << 7)) != 0);
            return value;
        }

        public static ref byte ReadLong7Bit(this ref byte inputBuffer, out long value, int tagBit = 0, byte prevTagBit = 0)
        {
            byte code = tagBit == 0 ? inputBuffer : prevTagBit;
            value = code & ((1 << (7 - tagBit)) - 1);

            if (tagBit == 0)
            {
                inputBuffer = ref Unsafe.AddByteOffset(ref inputBuffer, 1);
            }

            if ((code & (1 << (7 - tagBit))) == 0)
            {
                return ref inputBuffer;
            }

            Calc:
            if (value >> (8 * 8 - 7) != 0)
            {
                value = 0;
                return ref inputBuffer;
            }
            code = inputBuffer;
            value = (value << 7) | (code & (((long)1 << 7) - 1));
            inputBuffer = ref Unsafe.AddByteOffset(ref inputBuffer, 1);
            if ((code & (1 << 7)) != 0)
            {
                goto Calc;
            }

            return ref inputBuffer;
        }

        public static bool ReadBoolean(this Stream stream) => stream.ReadByte() != 0;

        public static ref T AsRef<T>(this byte[] coverBuffer, int coverHeaderOffset = 0)
            => ref Unsafe.As<byte, T>(ref coverBuffer[coverHeaderOffset]);

        public static void GetPathsFromStream(this Stream reader, out string[] outputPaths, int bufferSize, int count)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                reader.ReadExactly(buffer, 0, bufferSize);
                buffer.AsSpan().GetPathsFromBuffer(out outputPaths, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static unsafe void GetPathsFromBuffer(this Span<byte> buffer, out string[] outputPaths, int count)
        {
            outputPaths = new string[count];
            int inLen = buffer.Length;

            int idx = 0, strIdx = 0;
#if (NETSTANDARD2_0 || NET461_OR_GREATER)
            int len = 0;
#endif
            fixed (byte* inputPtr = &MemoryMarshal.GetReference(buffer))
            {
                do
                {
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                    ReadOnlySpan<byte> inputSpanned =
                        MemoryMarshal.CreateReadOnlySpanFromNullTerminated(inputPtr + idx);
                    idx += inputSpanned.Length + 1;
                    outputPaths[strIdx++] = Encoding.UTF8.GetString(inputSpanned);
#else
                    if (*(inputPtr + idx++) == 0)
                    {
                        outputPaths[strIdx++] = Encoding.UTF8.GetString(inputPtr + (idx - len), len == 0 ? 0 : --len);
                        len = 0;
                    }

                    len++;
#endif
                } while (strIdx < count);
            }
        }

        public static void GetLongsFromStream(this Stream reader, out long[] outputLongs, long count, long checkCount)
        {
            outputLongs = new long[count];
            long backValue = -1;

            for (long i = 0; i < count; i++)
            {
                long num = reader.ReadLong7Bit();
                backValue += 1 + num;
                if (backValue > checkCount) throw new InvalidDataException($"[PatchDir::GetLongsFromStream] Given back value for the reference list is invalid! Having {i} refs while expecting max: {checkCount}");
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetLongsFromStream] value {i} - {count}: {backValue}", Verbosity.Debug);
#endif
                outputLongs[i] = backValue;
            }
        }

        public static void GetLongsFromStream(this Stream reader, out long[] outputLongs, long count)
        {
            outputLongs = new long[count];
            for (long i = 0; i < count; i++)
            {
                long num = reader.ReadLong7Bit();
                outputLongs[i] = num;
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetLongsFromStream] value {i} - {count}: {num}", Verbosity.Debug);
#endif
            }
        }

        public static void GetPairIndexReferenceFromStream(this Stream reader, out PairIndexReference[] outPair, long pairCount, long checkEndNewValue, long checkEndOldValue)
        {
            outPair = new PairIndexReference[pairCount];
            long backNewValue = -1;
            long backOldValue = -1;

            for (long i = 0; i < pairCount; ++i)
            {
                long incNewValue = reader.ReadLong7Bit();

                backNewValue += 1 + incNewValue;
                if (backNewValue > checkEndNewValue) throw new InvalidDataException($"[PatchDir::GetArrayOfSamePairULongTag] Given back new value for the list is invalid! Having {backNewValue} value while expecting max: {checkEndNewValue}");

                byte pSign = (byte)reader.ReadByte();
                long incOldValue = reader.ReadLong7Bit(1, pSign);

                if (pSign >> 8 - 1 == 0)
                    backOldValue += 1 + incOldValue;
                else
                    backOldValue = backOldValue + 1 - incOldValue;

                if (backOldValue > checkEndOldValue) throw new InvalidDataException($"[PatchDir::GetArrayOfSamePairULongTag] Given back old value for the list is invalid! Having {backOldValue} value while expecting max: {checkEndOldValue}");
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetArrayOfSamePairULongTag] value {i} - {pairCount}: newIndex -> {backNewValue} oldIndex -> {backOldValue}", Verbosity.Debug);
#endif
                outPair[i] = new PairIndexReference { NewIndex = backNewValue, OldIndex = backOldValue };
            }
        }

        public static int GetFileStreamBufferSize(this long fileSize)
            => fileSize switch
            {
                // 128 KiB
                <= 128 << 10 => 4 << 10,
                // 1 MiB
                <= 1 << 20 => 64 << 10,
                // 32 MiB
                <= 32 << 20 => 128 << 10,
                // 100 MiB
                <= 100 << 20 => 512 << 10,
                _ => 1 << 20
            };
    }
}
