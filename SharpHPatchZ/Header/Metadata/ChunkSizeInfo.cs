using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct ChunkSizeInfo
{
    public long Size;
    public long CompressedSize;
}
