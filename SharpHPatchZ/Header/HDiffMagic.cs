namespace SharpHPatchZ.Header;

/// <summary>
/// The signature type of the HDiff patch format.
/// </summary>
public enum HDiffMagic
{
    /// <summary>
    /// Whether to indicate that the file is not an HDiff Patch or unsupported format.
    /// </summary>
    Unknown,

    /// <summary>
    /// Patch is a single HDiff format (HDiff13).
    /// </summary>
    HDiff13,

    /// <summary>
    /// Patch is a Directory HDiff extension for HDiff13 format (HDiff19+HDiff13 aka DirHDiff).
    /// </summary>
    HDiff19
}
