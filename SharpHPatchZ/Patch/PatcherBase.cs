using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Reader;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHPatchZ.Patch;

internal abstract class PatcherBase
    : IDisposable
#if NET6_0_OR_GREATER
      , IAsyncDisposable
#endif
{
    protected RandomMergedStreamWrapper? InputStream      { get; set; }
    protected RandomMergedStreamWrapper? OutputStream     { get; set; }

    protected HDiffInfo        Info             { get; set; }
    protected PatchOptions     Options          { get; set; }
    protected ProgressCallback ProgressCallback { get; }

    protected long TotalWritten;
    protected long TotalSize;

    protected PatcherBase(HDiffInfo info, PatchOptions options, ProgressCallback progressCallback)
    {
        Info             = info;
        Options          = options;
        ProgressCallback = !progressCallback.IsAllocated ? new ProgressCallback() : progressCallback;
        TotalSize        = info.GetPatchMetadata().DiffNewSize;
    }

    public abstract void StartPatch(string      inputPath, string outputPath, CancellationToken token);
    public abstract Task StartPatchAsync(string inputPath, string outputPath, CancellationToken token);

    internal
#if NET6_0_OR_GREATER
        unsafe
#endif
        void AdvanceProgress(int written)
    {
        Interlocked.Add(ref TotalWritten, written);
        ProgressCallback.ProcessedBytesCallback(TotalWritten, TotalSize, written);
    }

    public void Dispose() => DisposeCore();

#if NET6_0_OR_GREATER
    public ValueTask DisposeAsync() => DisposeCoreAsync();
#endif

    protected virtual void DisposeCore()
    {
        InputStream?.Dispose();
        OutputStream?.Dispose();
    }

#if NET6_0_OR_GREATER
    protected virtual ValueTask DisposeCoreAsync()
    {
        InputStream?.Dispose();
        OutputStream?.Dispose();

        return ValueTask.CompletedTask;
    }
#endif
}
