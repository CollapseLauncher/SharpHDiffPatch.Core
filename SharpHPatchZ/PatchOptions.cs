using System;
using System.Runtime.InteropServices;

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions()
{
    public static readonly PatchOptions Default = new()
    {
        ParallelThreads = (uint)Environment.ProcessorCount,
#if NET6_0_OR_GREATER
        UseSIMD = true
#endif
    };

    public static readonly PatchOptions BigBuffer = Default with
    {
        ReaderBufferSize = 1 << 20,
        CopyBufferSize = 128 << 10,
        PatchWorkerBufferSize = 16 << 20
    };

    public static readonly PatchOptions SmallBuffer = Default with
    {
        ReaderBufferSize = 4 << 10,
        CopyBufferSize = 4 << 10,
        PatchWorkerBufferSize = 128 << 10
    };

    public static readonly PatchOptions OptimizeForHDD = Default with
    {
        ParallelThreads = 1,
        CopyBufferSize = BigBuffer.CopyBufferSize
    };

#if NET6_0_OR_GREATER
    public bool UseSIMD = true;
#endif

    public uint ParallelThreads       = 0;
    public int  ReaderBufferSize      = 0;
    public int  CopyBufferSize        = 0;
    public int  PatchWorkerBufferSize = 0;
}
