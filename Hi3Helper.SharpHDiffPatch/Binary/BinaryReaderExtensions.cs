using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hi3Helper.UABT.Binary
{
    public static class BinaryReaderExtensions
    {
        public static string ReadStringToNull(this BinaryReader reader)
        {
            List<byte> list = new List<byte>();
            byte item;
            while (reader.BaseStream.Position != reader.BaseStream.Length && (item = reader.ReadByte()) != 0)
            {
                list.Add(item);
            }
            return Encoding.UTF8.GetString(list.ToArray());
        }

        public static ulong ReadUInt64VarInt(this BinaryReader reader) => ToTarget(reader, 0);
        public static ulong ReadUInt64VarInt(this BinaryReader reader, int tagBit) => ToTarget(reader, tagBit);

        private static ulong ToTarget(BinaryReader reader, int tagBit)
        {
            byte code = reader.ReadByte();
            int offsetRead = 0;
            ulong value = code & (((ulong)1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (sizeof(ulong) * 8 - 7)) != 0) return 0;
                    code = reader.ReadByte();
                    value = (value << 7) | (code & (((ulong)1 << 7) - 1));
                    offsetRead++;
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }
    }
}
