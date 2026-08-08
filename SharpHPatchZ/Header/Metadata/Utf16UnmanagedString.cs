using System;
using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct Utf16UnmanagedString : IMetadataInit
{
    public Utf16UnmanagedString()
    {
        Init();
    }

    public void Init()
    {
        if (IsDisposed || IsInitialized)
        {
            return;
        }

        IsInitialized = true;
        IsDisposed    = false;
        MetadataType  = MetadataTypeConst.Utf16UnmanagedStringType;
    }

    public void Dispose()
    {
        if (IsDisposed || !IsInitialized || Native is { Chars: null, Length: 0 })
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;
        NativeStringW old = Native;
        Native = NativeStringW.Empty;

        MemoryAlloc.Free(old.Chars);
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

    private byte          _isInitialized;
    private byte          _isDisposed;
    public  NativeStringW Native;

    public static Utf16UnmanagedString CreateFromManaged(ReadOnlySpan<char> source)
    {
        int   lengthToAlloc = source.Length + 1;
        char* nativeChar    = MemoryAlloc.Alloc<char>(lengthToAlloc, true);

        source.CopyTo(new Span<char>(nativeChar, source.Length));
        Utf16UnmanagedString thisStruct = new()
        {
            Native = new NativeStringW(nativeChar, source.Length)
        };
        thisStruct.Init();

        return thisStruct;
    }

    public static implicit operator ReadOnlySpan<char>(Utf16UnmanagedString unmanaged)
        => unmanaged.Native;

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativeStringW(char* chars, int length)
    {
        public static NativeStringW Empty => default;

        public readonly char* Chars  = chars;
        public readonly int   Length = length;

        public static implicit operator ReadOnlySpan<char>(NativeStringW unmanaged)
            => new(unmanaged.Chars, unmanaged.Length);

        public override string ToString() => new(Chars, 0, Length);
    }
}
