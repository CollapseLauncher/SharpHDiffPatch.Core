using System.Runtime.InteropServices;
// ReSharper disable StringLiteralTypo

namespace SharpHPatchZ.Native;

internal static unsafe partial class PInvoke
{
    public static class Windows
    {
        [DllImport("ucrtbase", EntryPoint = "_fileno", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetFileDescriptor(void* stream);

        [DllImport("ucrtbase", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint GetOSFileHandle(int fd);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            nint                     fileHandle,
            ref byte                 buffer,
            uint                     bytesToRead,
            out uint                 bytesRead,
            ref NativeOverlappedData overlapped);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            nint                     fileHandle,
            ref byte                 buffer,
            uint                     bytesToWrite,
            out uint                 bytesWritten,
            ref NativeOverlappedData overlapped);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeOverlappedData(long offset)
        {
            private readonly nint _internal     = 0;
            private readonly nint _internalHigh = 0;
            private readonly uint _offset       = unchecked((uint)offset);
            private readonly uint _offsetHigh   = unchecked((uint)(offset >> 32));
            private readonly nint _eventHandle  = 0;
        }
    }
}
