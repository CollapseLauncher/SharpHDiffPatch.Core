using System.Collections.Generic;

namespace ManagedLzma.LZMA.Master
{
    internal struct SRes
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

    internal sealed class ISzAlloc
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

    partial class LZMA
    {
    }
}
