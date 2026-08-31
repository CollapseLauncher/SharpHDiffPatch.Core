using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header;

/// <summary>
/// Describes the result of validating a patch checksum.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct HDiffChecksumResult
{
    /// <summary>Indicates whether the patch's checksum algorithm is supported.</summary>
    public bool      HasChecksumSupport;
    /// <summary>Indicates whether checksum validation succeeded.</summary>
    public bool      IsSuccessful;
    /// <summary>Contains the parsed <see cref="HDiffInfo"/> associated with the validation.</summary>
    public HDiffInfo DiffInfo;

    public static implicit operator bool(HDiffChecksumResult result)
    {
        return !result.HasChecksumSupport ||
               // Pass it as true anyways while Diff has no checksum support
               result.IsSuccessful;
    }
}
