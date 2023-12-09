using System;

namespace ManagedLzma.LZMA
{
    internal static class CUtils
    {
        public static void memcpy(P<byte> dst, P<byte> src, long size)
        {
            memcpy(dst, src, checked((int)size));
        }

        public static void memcpy(P<byte> dst, P<byte> src, int size)
        {
            if (dst.mBuffer == src.mBuffer && src.mOffset < dst.mOffset + size && dst.mOffset < src.mOffset + size)
            {
                System.Diagnostics.Debugger.Break();
                throw new InvalidOperationException("memcpy cannot handle overlapping regions correctly");
            }

            Buffer.BlockCopy(src.mBuffer, src.mOffset, dst.mBuffer, dst.mOffset, size);
        }
    }
}
