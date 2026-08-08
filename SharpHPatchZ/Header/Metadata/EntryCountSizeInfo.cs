using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct EntryCountSizeInfo
{
    public int  Count;
    public long Size;
}