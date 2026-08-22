using System.Runtime.InteropServices;

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions()
{
    public uint ParallelThreads  = 0;
    public bool UseSIMD          = true;
    public int  ReaderBufferSize = -1;
    public int  CopyBufferSize   = -1;
}
