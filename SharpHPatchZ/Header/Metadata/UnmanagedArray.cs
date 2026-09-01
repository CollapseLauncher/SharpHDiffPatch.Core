using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Owns a contiguous unmanaged array of values.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct UnmanagedArray<T> : IMetadataInit
    where T : unmanaged
{
    /// <summary>Initializes a new empty <see cref="UnmanagedArray{T}"/>.</summary>
    public UnmanagedArray()
    {
        Init();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public MetadataTypeConst MetadataType { get; private set; }

    /// <inheritdoc/>
    public bool IsInitialized
    {
        get => _isInitialized == 1;
        private set => _isInitialized = value ? (byte)1 : (byte)0;
    }

    /// <inheritdoc/>
    public bool IsDisposed
    {
        get => _isDisposed == 1;
        private set => _isDisposed = value ? (byte)1 : (byte)0;
    }

    private byte _isInitialized;
    private byte _isDisposed;

    /// <summary>The number of elements in the array.</summary>
    public int Length;
    /// <summary>The size of each element, in bytes.</summary>
    public int TypeSize;
    /// <summary>Points to the first element in the unmanaged allocation.</summary>
    public T*  Data;

    internal Span<T> GetSpan() => Data == null ? Span<T>.Empty : new Span<T>(Data, Length);

    /// <summary>Allocates an unmanaged array descriptor and its element storage.</summary>
    /// <param name="count">The number of elements to allocate.</param>
    /// <param name="initialize">Whether to zero-initialize the element storage.</param>
    /// <returns>A pointer to the allocated array descriptor.</returns>
    public static UnmanagedArray<T>* CreateAllocUnsafe(int count, bool initialize = false)
    {
        UnmanagedArray<T>* alloc = MemoryAlloc.Alloc<UnmanagedArray<T>>();
        alloc->Data   = MemoryAlloc.Alloc<T>(count, initialize);
        alloc->Length = count;
        alloc->Init();

        return alloc;
    }

    /// <summary>Creates an array value backed by newly allocated unmanaged storage.</summary>
    /// <param name="count">The number of elements to allocate.</param>
    /// <param name="initialize">Whether to zero-initialize the element storage.</param>
    /// <returns>The initialized <see cref="UnmanagedArray{T}"/>.</returns>
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

    /// <summary>Gets a reference to the element at the specified index.</summary>
    /// <param name="index">The zero-based element index.</param>
    /// <returns>A reference to the requested element.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the array bounds.</exception>
    public ref T this[int index]
    {
        get
        {
            if (index > Length - 1) throw new ArgumentOutOfRangeException();
            return ref Unsafe.AsRef<T>(Unsafe.Add<T>(Data, index));
        }
    }
}
