using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hi3Helper.SharpHDiffPatch
{
    public static class BinaryReaderExtensions
    {
        private static byte[] StringBuffer = new byte[4 << 10];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadStringToNull(this BinaryReader reader)
        {
            byte currentValue;
            int i = 0;
            while (StringBuffer.Length > i && (currentValue = reader.ReadByte()) != 0)
            {
                StringBuffer[i++] = currentValue;
            }

            return Encoding.UTF8.GetString(StringBuffer, 0, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt7bit(this BinaryReader reader, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : reader.ReadByte();
            int value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (4 * 4 - 7)) != 0) return 0;
                    code = reader.ReadByte();
                    value = (value << 7) | (code & ((1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadLong7bit(this BinaryReader reader, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : reader.ReadByte();
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = reader.ReadByte();
                    value = (value << 7) | (code & (((long)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }
    }
}
