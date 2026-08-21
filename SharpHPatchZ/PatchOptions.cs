using System.Runtime.InteropServices;

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions()
{
    public uint ParallelThreads  = 0;
    public bool IgnoreErrors     = false;
    public bool UseSIMD          = true;
    public int  ReaderBufferSize = -1;
}
