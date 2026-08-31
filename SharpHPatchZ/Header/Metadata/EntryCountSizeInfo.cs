using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Stores the number and total byte size of a group of patch entries.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EntryCountSizeInfo
{
    /// <summary>The number of entries.</summary>
    public int  Count;
    /// <summary>The total size of the entries, in bytes.</summary>
    public long Size;
}
