using System;

namespace SharpHPatchZ.Header.Metadata;

[Flags]
public enum MetadataTypeConst : short
{
    IsMetadataType = unchecked((short)0b_10000000_10000000),

    PatchMetadataType          = IsMetadataType | 0b_01000000_00000000,
    DirectoryPatchMetadataType = IsMetadataType | 0b_00100000_00000000,
    ChecksumDataInfoType       = IsMetadataType | 0b_00010000_00000000,
    UnmanagedArrayType         = IsMetadataType | 0b_00001000_00000000,
    Utf16UnmanagedStringType   = IsMetadataType | 0b_00000100_00000000
}
