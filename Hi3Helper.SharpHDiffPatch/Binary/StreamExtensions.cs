using System;
using System.IO;
using System.Text;

namespace Hi3Helper.SharpHDiffPatch
{
    public static class StreamExtension
    {
        private static byte[] StringBuffer = new byte[4 << 10];

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


#if !(NETSTANDARD2_0 || NET7_0_OR_GREATER)
        public static int ReadExactly(this Stream stream, Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer.Slice(totalRead));
                if (read == 0) return totalRead;

                totalRead += read;
            }

            return totalRead;
        }
#endif

        public static string ReadStringToNull(this Stream reader)
        {
            int currentValue;
            int i = 0;
            while (StringBuffer.Length > i && (currentValue = reader.ReadByte()) != 0)
            {
                StringBuffer[i++] = (byte)currentValue;
            }

            return Encoding.UTF8.GetString(StringBuffer, 0, i);
        }

        public static int ReadInt7bit(Stream inputStream, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)inputStream.ReadByte();
            int value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (4 * 4 - 7)) != 0) return 0;
                    code = (byte)inputStream.ReadByte();
                    value = (value << 7) | (code & ((1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        public static long ReadLong7bit(Stream inputStream, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)inputStream.ReadByte();
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = (byte)inputStream.ReadByte();
                    value = (value << 7) | (code & (((long)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        public static long ReadLong7bit(ReadOnlySpan<byte> inputBuffer, ref int offset, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : inputBuffer[offset++];
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = inputBuffer[offset++];
                    value = (value << 7) | (code & (((long)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        public static bool ReadBoolean(this Stream stream) => stream.ReadByte() != 0;
    }
}
