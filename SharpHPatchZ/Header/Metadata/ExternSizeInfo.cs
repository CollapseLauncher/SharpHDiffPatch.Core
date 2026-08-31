using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Stores size information for executable and external directory-patch data.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ExternSizeInfo
{
    /// <summary>The number of new executable entries.</summary>
    public int  NewExecuteCount;
    /// <summary>The size of the private reserved data, in bytes.</summary>
    public long PrivateReservedDataSize;
    /// <summary>The size of the private external data, in bytes.</summary>
    public long PrivateExternDataSize;
    /// <summary>The size of the external data, in bytes.</summary>
    public long ExternDataSize;
}
