using System.Runtime.InteropServices;

namespace SharpHDiffPatch.New.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public struct ExternSizeInfo
{
    public int  NewExecuteCount;
    public long PrivateReservedDataSize;
    public long PrivateExternDataSize;
    public long ExternDataSize;
}
