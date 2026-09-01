using System;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Identifies the concrete kind of unmanaged patch metadata.</summary>
[Flags]
public enum MetadataTypeConst : short
{
    /// <summary>Mask applied to values that identify metadata.</summary>
    IsMetadataType = unchecked((short)0b_10000000_10000000),

    /// <summary>Identifies <see cref="PatchMetadata"/>.</summary>
    PatchMetadataType          = IsMetadataType | 0b_01000000_00000000,
    /// <summary>Identifies <see cref="DirectoryPatchMetadata"/>.</summary>
    DirectoryPatchMetadataType = IsMetadataType | 0b_00100000_00000000,
    /// <summary>Identifies <see cref="ChecksumDataInfo"/>.</summary>
    ChecksumDataInfoType       = IsMetadataType | 0b_00010000_00000000,
    /// <summary>Identifies <see cref="UnmanagedArray{T}"/>.</summary>
    UnmanagedArrayType         = IsMetadataType | 0b_00001000_00000000,
    /// <summary>Identifies <see cref="Utf16UnmanagedString"/>.</summary>
    Utf16UnmanagedStringType   = IsMetadataType | 0b_00000100_00000000
}
