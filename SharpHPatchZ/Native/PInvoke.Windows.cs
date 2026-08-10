using System.Runtime.InteropServices;

namespace SharpHPatchZ.Native;

internal static unsafe partial class PInvoke
{
    public static class Windows
    {
        [DllImport("ucrtbase", CallingConvention = CallingConvention.Cdecl)]
        public static extern int _fileno(void* stream);

        [DllImport("ucrtbase", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint _get_osfhandle(int fd);
    }
}
