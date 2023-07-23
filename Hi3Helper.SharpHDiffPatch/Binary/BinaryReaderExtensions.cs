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

        public static ulong ReadUInt64VarInt(this BinaryReader reader) => ToTarget(reader, 0, 0);
        public static ulong ReadUInt64VarInt(this BinaryReader reader, int tagBit, byte prevTagBit) => ToTarget(reader, tagBit, prevTagBit);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ToTarget(BinaryReader reader, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : reader.ReadByte();
            ulong value = code & (((ulong)1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = reader.ReadByte();
                    value = (value << 7) | (code & (((ulong)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }
    }
}
