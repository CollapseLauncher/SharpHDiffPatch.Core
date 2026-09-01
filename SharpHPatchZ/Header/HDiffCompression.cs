// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming
namespace SharpHPatchZ.Header;

/// <summary>
/// Determines the Compression Type of the HDiff file.
/// </summary>
public enum HDiffCompression
{
    /// <summary>The patch data is not compressed.</summary>
    Uncompressed,
    /// <summary>The patch data uses LZMA compression.</summary>
    Lzma,
    /// <summary>The patch data uses LZMA2 compression.</summary>
    Lzma2,
    /// <summary>The patch data uses Zlib compression.</summary>
    Zlib,
    /// <summary>The patch data uses parallel BZip2 compression.</summary>
    PBZ2,
    /// <summary>The patch data uses BZip2 compression.</summary>
    BZ2,
    /// <summary>The patch data uses Zstandard compression.</summary>
    Zstd
}
