using System.Runtime.InteropServices;

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions()
{
    public static readonly PatchOptions Default = new();

#if NET6_0_OR_GREATER
    public bool UseSIMD = true;
#endif

    public uint ParallelThreads       = 0;
    public int  ReaderBufferSize      = 0;
    public int  CopyBufferSize        = 0;
    public int  PatchWorkerBufferSize = 0;
}
