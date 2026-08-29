using System;
using System.Runtime.InteropServices;

namespace SharpHPatchZ;

/// <summary>
/// Specifies options during patching operations. This options contains some essential settings to determine the buffer size and parallelization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PatchOptions()
{
    /// <summary>
    /// Default and optimal <see cref="PatchOptions"/> for both performance and memory allocation.
    /// </summary>
    public static readonly PatchOptions Default = new()
    {
        ParallelThreads = (uint)Environment.ProcessorCount,
#if NET6_0_OR_GREATER
        UseSIMD = true
#endif
    };

    /// <summary>
    /// Prioritizes performance by allocating much bigger sequential buffer for the reader, patch workers and the copy routine
    /// </summary>
    public static readonly PatchOptions BigBuffer = Default with
    {
        ReaderBufferSize = 1 << 20,
        CopyBufferSize = 128 << 10,
        PatchWorkerBufferSize = 16 << 20
    };

    /// <summary>
    /// Maintaining smaller memory allocation possible while impacting the performance.
    /// </summary>
    public static readonly PatchOptions SmallBuffer = Default with
    {
        ReaderBufferSize = 4 << 10,
        CopyBufferSize = 4 << 10,
        PatchWorkerBufferSize = 128 << 10
    };

    /// <summary>
    /// Optimized for Hard Drives where it requires sequential read/write routines and avoiding random seek by reducing the amount of patch workers.
    /// </summary>
    public static readonly PatchOptions OptimizeForHDD = Default with
    {
        ParallelThreads = 1,
        CopyBufferSize = BigBuffer.CopyBufferSize
    };

#if NET6_0_OR_GREATER
    /// <summary>
    /// Whether to use SIMD on the RLE addition functions.<br/>
    /// If enabled, AVX2 will be used by default on supported CPUs.<br/>
    /// Otherwise, falling back to SSE2 for supported x86-64-v2 CPUs.<br/>
    /// If none of the above are available, fallback to Runtime-Vectorization or Scalar on ARM or unsupported CPUs
    /// </summary>
    public bool UseSIMD = true;
#endif

    /// <summary>
    /// Determines how much the maximum parallel threads being used to produce the patch workers.
    /// </summary>
    public uint ParallelThreads = 0;

    /// <summary>
    /// Determines how much buffer to be allocated for the RLE Stream Readers.
    /// </summary>
    public int ReaderBufferSize = 0;

    /// <summary>
    /// Determines how much buffer to be allocated for the same-file copy operation.
    /// </summary>
    public int CopyBufferSize = 0;

    /// <summary>
    /// Determines how much buffer to be allocated for each of the patch workers.
    /// </summary>
    public int  PatchWorkerBufferSize = 0;
}
