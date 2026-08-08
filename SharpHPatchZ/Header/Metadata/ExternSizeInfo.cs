using System.Runtime.InteropServices;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct ExternSizeInfo
{
    public int  NewExecuteCount;
    public long PrivateReservedDataSize;
    public long PrivateExternDataSize;
    public long ExternDataSize;
}
