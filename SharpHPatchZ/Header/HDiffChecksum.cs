namespace SharpHPatchZ.Header;

/// <summary>
/// Determines the type of the Checksum used within the HDiff file.
/// </summary>
public enum HDiffChecksum
{
    NoChecksum,
    FAdler64,
    Crc32
}
