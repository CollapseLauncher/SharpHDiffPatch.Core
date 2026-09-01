using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
// ReSharper disable InconsistentNaming

namespace SharpHPatchZ.Native;

internal static unsafe partial class PInvoke
{
#if NET8_0_OR_GREATER
    public static SafeFileHandle GetSafeFileHandleFromFILE(void* file)
    {
        nint handle;

        if (OperatingSystem.IsWindows())
        {
            int fd = Windows.GetFileDescriptor(file);
            if (fd < 0)
                throw new IOException("_fileno() failed.");

            handle = Windows.GetOSFileHandle(fd);
            if (handle == -1)
                throw new IOException("_get_osfhandle() failed.");
        }
        else
        {
            int fd = Unix.GetFileDescriptor(file);
            if (fd < 0)
                throw new IOException("fileno() failed.");

            handle = fd;
        }

        return new SafeFileHandle(handle, ownsHandle: false);
    }
#endif
}
