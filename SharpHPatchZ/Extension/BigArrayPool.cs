using System;
using System.Buffers;

namespace SharpHPatchZ.Extension;

internal static class BigArrayPool<T>
{
    public static ArrayPool<T> Shared { get; } = ArrayPool<T>.Create(128 << 20, Environment.ProcessorCount << 10);
}
