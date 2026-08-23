using System.Runtime.InteropServices;
// ReSharper disable IdentifierTypo

namespace SharpHPatchZ;

[StructLayout(LayoutKind.Sequential)]
public struct InitializeOptions()
{
    private int _isKuroGamesHDiff = 0;

    public bool IsKuroGamesHDiff
    {
        get => _isKuroGamesHDiff > 0;
        set => _isKuroGamesHDiff = value ? 1 : 0;
    }
}
