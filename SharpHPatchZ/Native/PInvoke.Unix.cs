using System.Runtime.InteropServices;
// ReSharper disable IdentifierTypo

namespace SharpHPatchZ.Native;

internal static unsafe partial class PInvoke
{
    public static class Unix
    {
        [DllImport("libc", EntryPoint = "fileno", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetFileDescriptor(void* stream);

        [DllImport("libc", EntryPoint = "pread", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint PRead(
            int      fileDescriptor,
            ref byte buffer,
            nuint    count,
            long     fileOffset);

        [DllImport("libc", EntryPoint = "pread64", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint PRead64(
            int      fileDescriptor,
            ref byte buffer,
            nuint    count,
            long     fileOffset);

        [DllImport("libc", EntryPoint = "pwrite", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint PWrite(
            int      fileDescriptor,
            ref byte buffer,
            nuint    count,
            long     fileOffset);

        [DllImport("libc", EntryPoint = "pwrite64", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint PWrite64(
            int      fileDescriptor,
            ref byte buffer,
            nuint    count,
            long     fileOffset);
    }
}
