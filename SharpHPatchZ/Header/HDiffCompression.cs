// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming
namespace SharpHPatchZ.Header;

/// <summary>
/// Determines the Compression Type of the HDiff file.
/// </summary>
public enum HDiffCompression
{
    Uncompressed,
    Lzma,
    Lzma2,
    Zlib,
    PBZ2,
    BZ2,
    Zstd
}
