#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;

namespace SharpHPatchZ;

/// <summary>
/// A callback delegate for the progress of the patching.
/// </summary>
/// <param name="totalProcessed">Determines how many bytes already processed.</param>
/// <param name="totalSize">Determines how many bytes to be processed in total.</param>
/// <param name="written">How many bytes is currently being written into the disk.</param>
public delegate void ProcessedBytesManagedCallback(long totalProcessed, long totalSize, int written);

/// <summary>
/// Sets progress callback for the patching process.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly
#if NET6_0_OR_GREATER
    unsafe
#endif
    struct ProgressCallback
{
    /// <summary>
    /// The delegate callback for the progress method.
    /// </summary>
#if NET6_0_OR_GREATER
    internal readonly delegate* unmanaged[Cdecl]<long, long, int, void> ProcessedBytesCallback;
#else
    internal readonly ProcessedBytesManagedCallback ProcessedBytesCallback;
#endif

    /// <summary>
    /// Whether the callback pointer is allocated or not.
    /// </summary>
    internal bool IsAllocated => ProcessedBytesCallback != null;

    /// <summary>
    /// Creates a new default <see cref="ProgressCallback"/> struct with a NOP callback.
    /// </summary>
    public ProgressCallback() : this(null) { }

    /// <summary>
    /// Creates a new <see cref="ProgressCallback"/> with specified callback method.
    /// </summary>
    /// <param name="callback"></param>
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

    /// <summary>
    /// This method is expected to be exists as a NOP method as a placeholder.
    /// The <see cref="ProcessedBytesCallback"/> field on .NET 6 uses an unmanaged delegate pointer to the method since it cannot be null.
    /// So the field is pointing to this method if no callback delegate is set.
    /// </summary>
#if NET6_0_OR_GREATER
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SkipLocalsInit]
#endif
    private static void NopProcessedBytesCallback(long totalProcessed, long totalSize, int written) { }

    /// <summary>
    /// Creates a new <see cref="ProgressCallback"/> with specified callback method.
    /// </summary>
    /// <param name="callback">A method used for the callback of the progress</param>
    /// <returns>A new <see cref="ProgressCallback"/> struct.</returns>
    public static ProgressCallback CreateFromManaged(ProcessedBytesManagedCallback callback) => new(callback);
}
