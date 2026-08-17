using System.Runtime.InteropServices;

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions
{
    public uint ParallelThreads;
    public bool IgnoreErrors;
    public bool UseSIMD;

    public PatchOptions()
    {
        ParallelThreads = 0;
        IgnoreErrors    = false;
        UseSIMD         = true;
    }
}
