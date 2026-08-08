using System.Runtime.InteropServices;

namespace SharpHDiffPatch.New.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct EntryCountSizeInfo
{
    public int  Count;
    public long Size;
}