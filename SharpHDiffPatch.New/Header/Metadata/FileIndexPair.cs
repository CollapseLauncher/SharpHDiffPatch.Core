using System.Runtime.InteropServices;

namespace SharpHDiffPatch.New.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct FileIndexPair
{
    public int OldIndex;
    public int NewIndex;

    public override string ToString() => $"Old: {OldIndex} - New: {NewIndex}";
}
