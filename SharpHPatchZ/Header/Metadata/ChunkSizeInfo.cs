using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Stores the uncompressed and compressed sizes of a patch data chunk.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ChunkSizeInfo
{
    /// <summary>The uncompressed chunk size, in bytes.</summary>
    public long Size;
    /// <summary>The compressed chunk size, in bytes, or zero when the chunk is not compressed.</summary>
    public long CompressedSize;
}
