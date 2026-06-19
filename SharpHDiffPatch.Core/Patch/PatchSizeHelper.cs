using System;

namespace SharpHDiffPatch.Core.Patch
{
    internal static class PatchSizeHelper
    {
        internal static bool FitsInInt32(long value) => value is >= 0 and <= int.MaxValue;

        internal static int ToCheckedInt32(long value, string paramName)
        {
            if (!FitsInInt32(value))
                throw new ArgumentOutOfRangeException(paramName, value,
                    $"Value exceeds maximum safe array length ({int.MaxValue}).");

            return (int)value;
        }

        /// <summary>
        /// Returns true when <see cref="PatchCoreFastBuffer"/> can allocate its working
        /// buffers without overflowing <see cref="int"/>-sized APIs (ArrayPool, byte[], etc.).
        /// </summary>
        internal static bool CanUseFastBuffer(HeaderInfo headerInfo)
        {
            if (headerInfo.isSingleCompressedDiff)
                return false;

            DiffChunkInfo chunk = headerInfo.chunkInfo;

            if (!FitsInInt32(chunk.rle_ctrlBuf_size))
                return false;

            if (!FitsInInt32(chunk.rle_codeBuf_size))
                return false;

            if (!FitsInInt32(chunk.cover_buf_size))
                return false;

            long allBufferSize = chunk.rle_ctrlBuf_size + chunk.rle_codeBuf_size + chunk.cover_buf_size;
            return IsMemorySufficient(allBufferSize);
        }

        private static bool IsMemorySufficient(long bufferSize)
        {
#if NET6_0_OR_GREATER
            GCMemoryInfo info = GC.GetGCMemoryInfo();

            // Get possible minimum free memory size.
            // Let's say for a mid-end device with low memory capacity:
            //     Free Mem: 2 GiB * 0.50 = 1 GiB
            //     Divided by CPU threads: 1 GiB / 8 = 128 MiB
            long thresholdPossibleFreeMemSize = (long)(info.TotalAvailableMemoryBytes * 0.50d / Environment.ProcessorCount);
            return thresholdPossibleFreeMemSize >= bufferSize;
#else
            return true; // We have no simple way to get memory info. So, just pass it in.
#endif
        }
    }
}
