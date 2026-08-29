using System.Runtime.InteropServices;
// ReSharper disable IdentifierTypo
// ReSharper disable CommentTypo

namespace SharpHPatchZ;

/// <summary>
/// Specifies the options during Patch context initialization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InitializeOptions()
{
    private int _isKuroGamesHDiff = 0;

    /// <summary>
    /// Specifies whether the patch file is a Kuro Games DirHDiff format.
    /// </summary>
    public bool IsKuroGamesHDiff
    {
        get => _isKuroGamesHDiff > 0;
        set => _isKuroGamesHDiff = value ? 1 : 0;
    }
}
