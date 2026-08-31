using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Maps an input-file index to its corresponding output-file index.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FileIndexPair
{
    /// <summary>The input-file index.</summary>
    public int OldIndex;
    /// <summary>The output-file index.</summary>
    public int NewIndex;

    /// <inheritdoc/>
    public override string ToString() => $"Old: {OldIndex} - New: {NewIndex}";
}
