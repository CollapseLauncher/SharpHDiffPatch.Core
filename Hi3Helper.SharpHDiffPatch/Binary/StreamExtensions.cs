﻿using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hi3Helper.SharpHDiffPatch
{
    public static class StreamExtensions
    {
        private static byte[] StringBuffer = new byte[4 << 10];

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt7bit(this Stream reader, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)reader.ReadByte();
            int value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (4 * 4 - 7)) != 0) return 0;
                    code = (byte)reader.ReadByte();
                    value = (value << 7) | (code & ((1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadLong7bit(this Stream reader, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;

            byte code = isUseTagBit ? prevTagBit : (byte)reader.ReadByte();
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = (byte)reader.ReadByte();
                    value = (value << 7) | (code & (((long)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }


        public static long ReadLong7bit(this ReadOnlySpan<byte> buffer, int offset, out int read, int tagBit = 0, byte prevTagBit = 0)
        {
            bool isUseTagBit = tagBit != 0;
            read = 1;

            byte code = isUseTagBit ? prevTagBit : buffer[offset++];
            long value = code & ((1 << (7 - tagBit)) - 1);

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    if ((value >> (8 * 8 - 7)) != 0) return 0;
                    code = buffer[offset++];
                    read++;
                    value = (value << 7) | (code & (((long)1 << 7) - 1));
                }
                while ((code & (1 << 7)) != 0);
            }
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadBoolean(this Stream stream) => stream.ReadByte() != 0;
    }
}