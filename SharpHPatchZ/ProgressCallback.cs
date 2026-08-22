#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;

namespace SharpHPatchZ;

public delegate void ProcessedBytesManagedCallback(long totalProcessed, long totalSize, int written);

[StructLayout(LayoutKind.Sequential)]
public readonly
#if NET6_0_OR_GREATER
    unsafe
#endif
    struct ProgressCallback
{
#if NET6_0_OR_GREATER
    public readonly delegate* unmanaged[Cdecl]<long, long, int, void> ProcessedBytesCallback;
#else
    public readonly ProcessedBytesManagedCallback ProcessedBytesCallback;
#endif

    internal bool IsAllocated => ProcessedBytesCallback != null;

    public ProgressCallback() : this(null) { }

#if NET6_0_OR_GREATER
    public ProgressCallback(ProcessedBytesManagedCallback? callback)
    {
        if (callback != null)
        {
            nint callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);
            ProcessedBytesCallback = (delegate* unmanaged[Cdecl]<long, long, int, void>)callbackPtr;
            return;
        }

        ProcessedBytesCallback = &NopProcessedBytesCallback;
    }
#else
    public ProgressCallback(ProcessedBytesManagedCallback? callback)
    {
        if (callback != null)
        {
            ProcessedBytesCallback = callback;
            return;
        }

        ProcessedBytesCallback = NopProcessedBytesCallback;
    }
#endif

#if NET6_0_OR_GREATER
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SkipLocalsInit]
#endif
    private static void NopProcessedBytesCallback(long totalProcessed, long totalSize, int written) { }

    public static ProgressCallback CreateFromManaged(ProcessedBytesManagedCallback callback) => new(callback);
}
