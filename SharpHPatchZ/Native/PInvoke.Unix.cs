using System.Runtime.InteropServices;

namespace SharpHPatchZ.Native;

internal static unsafe partial class PInvoke
{
    public static class Unix
    {
        [DllImport("__Internal", EntryPoint = "fileno", CallingConvention = CallingConvention.Cdecl)]
        public static extern int fileno(void* stream);
    }
}
