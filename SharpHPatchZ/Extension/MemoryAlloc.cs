using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

namespace SharpHPatchZ.Extension;

internal static unsafe class MemoryAlloc
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T* Alloc<T>(int elementCount = 1, bool initializeAlloc = false)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return (T*)Alloc(elementCount, initializeAlloc);
        }

        return (T*)Alloc(elementCount * sizeof(T), initializeAlloc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Alloc(long byteCount, bool initializeAlloc = false)
    {
#if NET6_0_OR_GREATER
        return initializeAlloc ?
            NativeMemory.AllocZeroed((nuint)byteCount) : 
            NativeMemory.Alloc((nuint)byteCount);
#else
        var ptr = (void*)Marshal.AllocHGlobal((nint)byteCount);
        if (!initializeAlloc) return ptr;

        Zero(ptr, byteCount);
        return ptr;
#endif
    }

    public static T* CopyToUnmanagedUnsafe<T>(this ref T toCopy)
        where T : unmanaged
    {
        T*    allocP   = Alloc<T>();
        ref T allocRef = ref allocP[0];
        allocRef = toCopy;
        return allocP;
    }

    public static nint CopyToUnmanaged<T>(this ref T toCopy)
        where T : unmanaged
        => (nint)toCopy.CopyToUnmanagedUnsafe();

    public static void CopyTo<T>(this ref T from, ref T to)
        where T : unmanaged
        => to = from;

    public static void CopyTo<T>(this ref T from, void* to)
        where T : unmanaged
        => Unsafe.AsRef<T>(to) = from;

    public static void CopyTo<T>(void* from, void* to)
        where T : unmanaged
        => Unsafe.AsRef<T>(to) = Unsafe.AsRef<T>(from);

    public static void CopyTo<T>(void* from, ref T to)
        where T : unmanaged
        => to = Unsafe.AsRef<T>(from);

    public static void Free<T>(T* ptr)
        where T : unmanaged
    {
        if (ptr == null)
        {
            return;
        }

        Free((void*)ptr);
    }

    public static void Free(void* ptr)
    {
        if (ptr == null)
        {
            return;
        }

        ref byte asRef = ref Unsafe.AsRef<byte>(ptr);
        asRef.TryDisposeIfMetadataType();

#if NET6_0_OR_GREATER
        NativeMemory.Free(ptr);
#else
        Marshal.FreeHGlobal((nint)ptr);
#endif
    }

#if !NET7_0_OR_GREATER
    public static void Zero(void* ptr, long byteCount)
    {
        ref byte current = ref Unsafe.AsRef<byte>(ptr);
        while (byteCount >= sizeof(nuint))
        {
            Unsafe.As<byte, nuint>(ref current) = 0;

            current = ref Unsafe.Add(ref current, sizeof(nuint));
            byteCount -= sizeof(nuint);
        }

        while (byteCount != 0)
        {
            current = 0;
            current = ref Unsafe.Add(ref current, 1);
            --byteCount;
        }
    }
#else
    public static void Zero(void* ptr, long byteCount)
        => NativeMemory.Fill(ptr, (nuint)byteCount, 0);
#endif
}
