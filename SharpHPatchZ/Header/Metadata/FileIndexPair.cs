using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct FileIndexPair
{
    public int OldIndex;
    public int NewIndex;

    public override string ToString() => $"Old: {OldIndex} - New: {NewIndex}";
}
