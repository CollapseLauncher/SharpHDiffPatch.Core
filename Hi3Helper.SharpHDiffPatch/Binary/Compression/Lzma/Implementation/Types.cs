using System;
using System.Collections.Generic;

namespace ManagedLzma.LZMA.Master
{
    partial class LZMA
    {
        public struct SRes
        {
            private int _code;
            public SRes(int code) { _code = code; }
            public override int GetHashCode() { return _code; }
            public override bool Equals(object obj) { return obj is SRes && ((SRes)obj)._code == _code; }
            public bool Equals(SRes obj) { return obj._code == _code; }
            public static bool operator ==(SRes left, SRes right) { return left._code == right._code; }
            public static bool operator !=(SRes left, SRes right) { return left._code != right._code; }
            public static bool operator ==(SRes left, int right) { return left._code == right; }
            public static bool operator !=(SRes left, int right) { return left._code != right; }
        }

        public static SRes SZ_OK { get { return new SRes(0); } }

        public static SRes SZ_ERROR_DATA { get { return new SRes(1); } }
        public static SRes SZ_ERROR_MEM { get { return new SRes(2); } }
        public static SRes SZ_ERROR_CRC { get { return new SRes(3); } }
        public static SRes SZ_ERROR_UNSUPPORTED { get { return new SRes(4); } }
        public static SRes SZ_ERROR_PARAM { get { return new SRes(5); } }
        public static SRes SZ_ERROR_INPUT_EOF { get { return new SRes(6); } }
        public static SRes SZ_ERROR_OUTPUT_EOF { get { return new SRes(7); } }
        public static SRes SZ_ERROR_READ { get { return new SRes(8); } }
        public static SRes SZ_ERROR_WRITE { get { return new SRes(9); } }
        public static SRes SZ_ERROR_PROGRESS { get { return new SRes(10); } }
        public static SRes SZ_ERROR_FAIL { get { return new SRes(11); } }
        public static SRes SZ_ERROR_THREAD { get { return new SRes(12); } }

        public static SRes SZ_ERROR_ARCHIVE { get { return new SRes(16); } }
        public static SRes SZ_ERROR_NO_ARCHIVE { get { return new SRes(17); } }

        //public delegate object ISzAlloc_Alloc(object p, long size);
        //public delegate void ISzAlloc_Free(object p, object address); /* address can be null */
        public sealed class ISzAlloc
        {
            public static readonly ISzAlloc BigAlloc = new ISzAlloc(200);
            public static readonly ISzAlloc SmallAlloc = new ISzAlloc(100);

            private static Dictionary<long, List<byte[]>> Cache1 = new Dictionary<long, List<byte[]>>();
            private static Dictionary<long, List<ushort[]>> Cache2 = new Dictionary<long, List<ushort[]>>();
            private static Dictionary<long, List<uint[]>> Cache3 = new Dictionary<long, List<uint[]>>();

            private ISzAlloc(int kind)
            {
            }

            public byte[] AllocBytes(object p, long size)
            {
                lock (Cache1)
                {
                    List<byte[]> cache;
                    if (Cache1.TryGetValue(size, out cache) && cache.Count > 0)
                    {
                        byte[] buffer = cache[cache.Count - 1];
                        cache.RemoveAt(cache.Count - 1);
                        return buffer;
                    }
                }

                System.Diagnostics.Debug.WriteLine("Alloc byte size=" + size);
                return new byte[size];
            }

            public ushort[] AllocUInt16(object p, long size)
            {
                lock (Cache2)
                {
                    List<ushort[]> cache;
                    if (Cache2.TryGetValue(size, out cache) && cache.Count > 0)
                    {
                        ushort[] buffer = cache[cache.Count - 1];
                        cache.RemoveAt(cache.Count - 1);
                        return buffer;
                    }
                }

                System.Diagnostics.Debug.WriteLine("Alloc ushort size=" + size);
                return new ushort[size];
            }

            public uint[] AllocUInt32(object p, long size)
            {
                lock (Cache3)
                {
                    List<uint[]> cache;
                    if (Cache3.TryGetValue(size, out cache) && cache.Count > 0)
                    {
                        uint[] buffer = cache[cache.Count - 1];
                        cache.RemoveAt(cache.Count - 1);
                        return buffer;
                    }
                }

                System.Diagnostics.Debug.WriteLine("Alloc uint size=" + size);
                return new uint[size];
            }

            public void FreeBytes(object p, byte[] buffer)
            {
                if (buffer != null)
                {
                    lock (Cache1)
                    {
                        List<byte[]> cache;
                        if (!Cache1.TryGetValue(buffer.Length, out cache))
                            Cache1.Add(buffer.Length, cache = new List<byte[]>());

                        cache.Add(buffer);
                    }
                }
            }

            public void FreeUInt16(object p, ushort[] buffer)
            {
                if (buffer != null)
                {
                    lock (Cache2)
                    {
                        List<ushort[]> cache;
                        if (!Cache2.TryGetValue(buffer.Length, out cache))
                            Cache2.Add(buffer.Length, cache = new List<ushort[]>());

                        cache.Add(buffer);
                    }
                }
            }
        }
    }
}
