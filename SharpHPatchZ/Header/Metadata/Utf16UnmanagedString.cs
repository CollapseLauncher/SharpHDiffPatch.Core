using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Owns a UTF-16 string stored in unmanaged memory.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Utf16UnmanagedString : IMetadataInit
{
    /// <summary>Initializes a new empty <see cref="Utf16UnmanagedString"/>.</summary>
    public Utf16UnmanagedString()
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

        IsInitialized = true;
        IsDisposed    = false;
        MetadataType  = MetadataTypeConst.Utf16UnmanagedStringType;
    }

    /// <inheritdoc/>
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

    private byte          _isInitialized;
    private byte          _isDisposed;
    /// <summary>The native string pointer and length.</summary>
    public  NativeStringW Native;

    /// <summary>Creates an unmanaged UTF-16 string from UTF-8 bytes.</summary>
    /// <param name="source">The UTF-8 bytes to convert.</param>
    /// <returns>A newly allocated <see cref="Utf16UnmanagedString"/>.</returns>
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

    /// <summary>Creates an unmanaged UTF-16 string from managed characters.</summary>
    /// <param name="source">The characters to copy.</param>
    /// <returns>A newly allocated <see cref="Utf16UnmanagedString"/>.</returns>
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

    /// <summary>Gets a <see cref="ReadOnlySpan{T}"/> over the string's characters.</summary>
    /// <returns>A <see cref="ReadOnlySpan{T}"/> over the unmanaged UTF-16 characters.</returns>
    public ReadOnlySpan<char> GetSpan() => Native.GetSpan();

    public static implicit operator ReadOnlySpan<char>(Utf16UnmanagedString unmanaged)
        => unmanaged.Native;

    public static implicit operator string(Utf16UnmanagedString unmanaged)
        => unmanaged.Native.Length == 0
            ? ""
            : unmanaged.Native.ToString();

    /// <inheritdoc/>
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

    /// <summary>Represents a non-owning pointer and length for a UTF-16 string.</summary>
    /// <param name="chars">A pointer to the UTF-16 characters.</param>
    /// <param name="length">The number of characters.</param>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativeStringW(char* chars, int length)
    {
        /// <summary>Gets an empty <see cref="NativeStringW"/>.</summary>
        public static NativeStringW Empty => default;

        /// <summary>The address of the first UTF-16 character.</summary>
        public readonly nint Chars  = (nint)chars;
        /// <summary>The number of UTF-16 characters.</summary>
        public readonly int  Length = length;

        public static implicit operator ReadOnlySpan<char>(NativeStringW unmanaged)
            => new((char*)unmanaged.Chars, unmanaged.Length);

        /// <inheritdoc/>
        public override string ToString() => new((char*)Chars, 0, Length);

        public static implicit operator string(NativeStringW unmanaged)
            => unmanaged.Length == 0
                ? ""
                : unmanaged.ToString();

        /// <summary>Gets a <see cref="ReadOnlySpan{T}"/> over the native characters.</summary>
        /// <returns>A <see cref="ReadOnlySpan{T}"/> over the native characters.</returns>
        public ReadOnlySpan<char> GetSpan() => this;
    }
}
