using System;

namespace SharpHDiffPatch.Core.Patch
{
    internal static class PatchSizeHelper
    {
        private const int SizeOfCoverHeader = sizeof(long) * 4;

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

            if (!FitsInInt32(headerInfo.newDataSize))
                return false;

            if (!FitsInInt32(chunk.rle_ctrlBuf_size))
                return false;

            if (!FitsInInt32(chunk.rle_codeBuf_size))
                return false;

            if (!FitsInInt32(chunk.cover_buf_size))
                return false;

            if (!FitsInInt32(chunk.coverCount))
                return false;

            long coverBufferLen = checked(SizeOfCoverHeader * chunk.coverCount);
            return FitsInInt32(coverBufferLen);
        }
    }
}
