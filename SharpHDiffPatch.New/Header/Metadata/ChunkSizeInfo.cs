using System.Runtime.InteropServices;

namespace SharpHDiffPatch.New.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct ChunkSizeInfo
{
    public long Size;
    public long CompressedSize;
}
