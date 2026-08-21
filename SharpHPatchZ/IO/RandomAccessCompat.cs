using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

#if !NET6_0_OR_GREATER
using System.ComponentModel;
using System.Runtime.InteropServices;
using SharpHPatchZ.Native;
#endif
// ReSharper disable InconsistentNaming
// ReSharper disable StringLiteralTypo

namespace SharpHPatchZ.IO;

/// <summary>
/// Provides position-independent file I/O on all target frameworks supported by
/// SharpHPatchZ. Modern targets use the runtime implementation; .NET Standard 2.0
/// calls the equivalent operating-system APIs directly.
/// </summary>
internal static class RandomAccessCompat
{
#if !NET6_0_OR_GREATER
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly unsafe bool UseLongLongPReadWrite = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && sizeof(nint) == 4;
#endif

    public static int Read(SafeFileHandle handle, Span<byte> buffer, long fileOffset)
    {
#if NET6_0_OR_GREATER
        return RandomAccess.Read(handle, buffer, fileOffset);
#else
        ValidateArguments(handle, fileOffset);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        return IsWindows
            ? ReadWindows(handle, buffer, fileOffset)
            : ReadUnix(handle, buffer, fileOffset);
#endif
    }

    public static void Write(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
    {
#if NET6_0_OR_GREATER
        RandomAccess.Write(handle, buffer, fileOffset);
#else
        ValidateArguments(handle, fileOffset);

        if (IsWindows)
        {
            WriteWindows(handle, buffer, fileOffset);
        }
        else
        {
            WriteUnix(handle, buffer, fileOffset);
        }
#endif
    }


#if !NET6_0_OR_GREATER
    private static void ValidateArguments(SafeFileHandle handle, long fileOffset)
    {
        if (handle is null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new ObjectDisposedException(nameof(handle));
        }

        if (fileOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileOffset));
        }
    }

    private const int ErrorHandleEof  = 38;
    private const int ErrorInterrupted = 4;

    private static int ReadWindows(
        SafeFileHandle handle,
        Span<byte>     buffer,
        long           fileOffset)
    {
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            PInvoke.Windows.NativeOverlappedData overlapped = new(fileOffset);

            bool success = PInvoke.Windows.ReadFile(handle.DangerousGetHandle(),
                                                    ref buffer.GetPinnableReference(),
                                                    (uint)buffer.Length,
                                                    out uint bytesRead,
                                                    ref overlapped);
            if (success)
            {
                return checked((int)bytesRead);
            }

            int error = Marshal.GetLastWin32Error();
            return error == ErrorHandleEof
                ? 0
                : throw CreateIOException("ReadFile", error);
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static void WriteWindows(
        SafeFileHandle     handle,
        ReadOnlySpan<byte> buffer,
        long               fileOffset)
    {
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);

            while (!buffer.IsEmpty)
            {
                PInvoke.Windows.NativeOverlappedData overlapped = new(fileOffset);
                bool success = PInvoke.Windows.WriteFile(handle.DangerousGetHandle(),
                                                         ref MemoryMarshal.GetReference(buffer),
                                                         (uint)buffer.Length,
                                                         out uint bytesWritten,
                                                         ref overlapped);
                if (!success)
                {
                    throw CreateIOException("WriteFile", Marshal.GetLastWin32Error());
                }

                if (bytesWritten == 0)
                {
                    throw new IOException("WriteFile completed without writing any data.");
                }

                int written = checked((int)bytesWritten);
                buffer     = buffer[written..];
                fileOffset = checked(fileOffset + written);
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static int ReadUnix(
        SafeFileHandle handle,
        Span<byte>     buffer,
        long           fileOffset)
    {
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            int fileDescriptor = handle.DangerousGetHandle().ToInt32();

            while (true)
            {
                long result = InvokePRead(fileDescriptor,
                                          ref MemoryMarshal.GetReference(buffer),
                                          (uint)buffer.Length,
                                          fileOffset).ToInt64();
                if (result >= 0)
                {
                    return checked((int)result);
                }

                int error = Marshal.GetLastWin32Error();
                if (error != ErrorInterrupted)
                {
                    throw CreateIOException("pread", error);
                }
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static void WriteUnix(
        SafeFileHandle     handle,
        ReadOnlySpan<byte> buffer,
        long               fileOffset)
    {
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            int fileDescriptor = handle.DangerousGetHandle().ToInt32();

            while (!buffer.IsEmpty)
            {
                long result;
                do
                {
                    result = InvokePWrite(fileDescriptor,
                                          ref MemoryMarshal.GetReference(buffer),
                                          (uint)buffer.Length,
                                          fileOffset).ToInt64();
                }
                while (result < 0 && Marshal.GetLastWin32Error() == ErrorInterrupted);

                switch (result)
                {
                    case < 0:
                        throw CreateIOException("pwrite", Marshal.GetLastWin32Error());
                    case 0:
                        throw new IOException("pwrite completed without writing any data.");
                }

                int written = checked((int)result);
                buffer     = buffer[written..];
                fileOffset = checked(fileOffset + written);
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static IntPtr InvokePRead(
        int      fileDescriptor,
        ref byte buffer,
        nuint    count,
        long     fileOffset)
    {
        // Linux x86 exposes the 64-bit offset ABI as pread64. Other supported
        // Unix targets use a 64-bit off_t for pread.
        return UseLongLongPReadWrite
            ? PInvoke.Unix.PRead64(fileDescriptor, ref buffer, count, fileOffset)
            : PInvoke.Unix.PRead(fileDescriptor, ref buffer, count, fileOffset);
    }

    private static IntPtr InvokePWrite(
        int      fileDescriptor,
        ref byte buffer,
        nuint    count,
        long     fileOffset)
    {
        return UseLongLongPReadWrite
            ? PInvoke.Unix.PWrite64(fileDescriptor, ref buffer, count, fileOffset)
            : PInvoke.Unix.PWrite(fileDescriptor, ref buffer, count, fileOffset);
    }

    private static IOException CreateIOException(string operation, int error)
        => new($"{operation} failed with native error {error}.", new Win32Exception(error));
#endif
}
