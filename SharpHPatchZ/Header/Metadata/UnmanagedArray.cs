using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct UnmanagedArray<T> : IMetadataInit
    where T : unmanaged
{
    public UnmanagedArray()
    {
        Init();
    }

    public void Init()
    {
        if (IsDisposed || IsInitialized)
        {
            return;
        }

        TypeSize      = sizeof(T);
        IsInitialized = true;
        IsDisposed    = false;
        MetadataType  = MetadataTypeConst.UnmanagedArrayType;
    }

    public void Dispose()
    {
        if (IsDisposed || !IsInitialized || Data == null)
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;
        T* oldData = Data;
        Data = null;

        if (oldData != null)
        {
            // Try to automatically dispose if data is a member of IMetadataInit
            ref byte startRef = ref Unsafe.AsRef<byte>(oldData);
            ref byte endRef   = ref Unsafe.Add(ref startRef, TypeSize * Length);
            if (startRef.TryGetMetadataType(out _))
            {
                while (Unsafe.IsAddressLessThan(ref startRef, ref endRef))
                {
                    startRef.TryDisposeIfMetadataType();
                    startRef = ref Unsafe.Add(ref startRef, TypeSize);
                }
            }
        }

        MemoryAlloc.Free(oldData);
    }

    public MetadataTypeConst MetadataType { get; private set; }

    public bool IsInitialized
    {
        get => _isInitialized == 1;
        private set => _isInitialized = value ? (byte)1 : (byte)0;
    }

    public bool IsDisposed
    {
        get => _isDisposed == 1;
        private set => _isDisposed = value ? (byte)1 : (byte)0;
    }

    private byte _isInitialized;
    private byte _isDisposed;

    public int Length;
    public int TypeSize;
    public T*  Data;

    public Span<T> GetSpan()
    {
        if (sizeof(T) != TypeSize)
        {
            throw new ArrayTypeMismatchException("Type mismatched!");
        }

        return Data == null ? Span<T>.Empty : new Span<T>(Data, Length);
    }

    public static UnmanagedArray<T>* CreateAllocUnsafe(int count, bool initialize = false)
    {
        UnmanagedArray<T>* alloc = MemoryAlloc.Alloc<UnmanagedArray<T>>();
        alloc->Data   = MemoryAlloc.Alloc<T>(count, initialize);
        alloc->Length = count;
        alloc->Init();

        return alloc;
    }

    public static UnmanagedArray<T> CreateAlloc(int count, bool initialize = false)
    {
        var array = new UnmanagedArray<T>
        {
            Data   = MemoryAlloc.Alloc<T>(count, initialize),
            Length = count
        };

        array.Init();
        return array;
    }
    

    public static implicit operator Span<T>(UnmanagedArray<T> unmanagedSpan) => unmanagedSpan.GetSpan();

    public ref T this[int index]
    {
        get
        {
            if (index > Length - 1) throw new ArgumentOutOfRangeException();
            return ref Unsafe.AsRef<T>(Unsafe.Add<T>(Data, index));
        }
    }
}
