namespace SharpHPatchZ.Header;

/// <summary>
/// Determines the type of the Checksum used within the HDiff file.
/// </summary>
public enum HDiffChecksum
{
    /// <summary>The patch does not include checksums.</summary>
    NoChecksum,
    /// <summary>The patch uses the fast Adler-64 checksum.</summary>
    FAdler64,
    /// <summary>The patch uses the CRC-32 checksum.</summary>
    Crc32
}
