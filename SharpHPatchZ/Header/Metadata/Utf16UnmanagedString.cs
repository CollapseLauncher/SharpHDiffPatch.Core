using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
        if (IsDisposed || !IsInitialized || Native is { Chars: 0, Length: 0 })
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;
        NativeStringW old = Native;
        Native = NativeStringW.Empty;

        MemoryAlloc.Free((char*)old.Chars);
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

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    public static Utf16UnmanagedString CreateFromManaged(ReadOnlySpan<byte> source)
    {
        int maxLenTransform = Encoding.Unicode.GetMaxByteCount(source.Length);
        char[]? tempCharsBuffer = maxLenTransform <= 1024
            ? null
            : ArrayPool<char>.Shared.Rent(maxLenTransform);

        Span<char> tempCharsSpan = tempCharsBuffer ?? stackalloc char[maxLenTransform];
        try
        {
            return TransformUtf8ToUnicode(source, tempCharsSpan);
        }
        finally
        {
            if (tempCharsBuffer != null) ArrayPool<char>.Shared.Return(tempCharsBuffer);
        }
    }

#if NET6_0_OR_GREATER
    [SkipLocalsInit]
#endif
    public static Utf16UnmanagedString CreateFromManaged(scoped ReadOnlySpan<char> source)
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

    public ReadOnlySpan<char> GetSpan() => Native.GetSpan();

    public static implicit operator ReadOnlySpan<char>(Utf16UnmanagedString unmanaged)
        => unmanaged.Native;

    public static implicit operator string(Utf16UnmanagedString unmanaged)
        => unmanaged.Native.Length == 0
            ? ""
            : unmanaged.Native.ToString();

    public override string ToString() => Native.ToString();

    private static Utf16UnmanagedString TransformUtf8ToUnicode(
        ReadOnlySpan<byte> source,
        Span<char>         target)
    {
        try
        {
            ref byte sourceRef = ref MemoryMarshal.GetReference(source);
            ref char targetRef = ref MemoryMarshal.GetReference(target);

            byte* sourceP = (byte*)Unsafe.AsPointer(ref sourceRef);
            char* targetP = (char*)Unsafe.AsPointer(ref targetRef);

            int written = Encoding.UTF8.GetChars(sourceP, source.Length, targetP, target.Length);
            return CreateFromManaged(target[..written]);
        }
        catch (Exception ex)
        {
            throw ExceptionHelper.ThrowHDiffStringEncodingFailed(ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativeStringW(char* chars, int length)
    {
        public static NativeStringW Empty => default;

        public readonly nint Chars  = (nint)chars;
        public readonly int  Length = length;

        public static implicit operator ReadOnlySpan<char>(NativeStringW unmanaged)
            => new((char*)unmanaged.Chars, unmanaged.Length);

        public override string ToString() => new((char*)Chars, 0, Length);

        public static implicit operator string(NativeStringW unmanaged)
            => unmanaged.Length == 0
                ? ""
                : unmanaged.ToString();

        public ReadOnlySpan<char> GetSpan() => this;
    }
}
