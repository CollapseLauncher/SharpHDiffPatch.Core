using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header;

[StructLayout(LayoutKind.Sequential)]
public struct HDiffChecksumResult
{
    public bool      HasChecksumSupport;
    public bool      IsSuccessful;
    public HDiffInfo DiffInfo;

    public static implicit operator bool(HDiffChecksumResult result)
    {
        return !result.HasChecksumSupport ||
               // Pass it as true anyways while Diff has no checksum support
               result.IsSuccessful;
    }
}
