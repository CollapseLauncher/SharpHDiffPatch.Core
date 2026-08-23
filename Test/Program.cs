using System;
using System.Diagnostics;
using SharpHDiffPatch.Core;
using SharpHPatchZ;
using SharpHPatchZ.Header;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Test;

public class Program
{
    private static string TestPathInput       = @"G:\Hi3SEA";
    private static string TestPathOutput      = @"G:\Hi3CN-dotnet";
    private static string TestPathPatch       = @"G:\Hi3SEAtoCNExecOnly.lzma2.diff";
    private static string TestPathPatchLzma   = @"G:\Hi3SEAtoCNExecOnly.lzma.diff";
    private static string TestPathPatchNoComp = @"G:\Hi3SEAtoCNExecOnly.nocomp.diff";
    public static async Task Main()
    {
        HDiffPatch hPatchOld = new();
        hPatchOld.Initialize(CreateStream);
        // hPatchOld.Patch(TestPathInput, TestPathOutput, true);

        ProgressCallback progressCallback = ProgressCallback.CreateFromManaged(WriteProgress);
        PatchOptions     options          = PatchOptions.Default;

        Stopwatch sw = Stopwatch.StartNew();
        using (HDiffInfo info1 = await HPatch.CreateInstanceAsync(CreateStreamAsync))
        {
            PatchResult result = await HPatch.PatchAsync(info1, CreateStreamAsync, TestPathInput, TestPathOutput, options, progressCallback: progressCallback);
        }

        Console.WriteLine();
        Console.WriteLine($"Completed in: {sw.ElapsedMilliseconds} ms");
    }

    public static void WriteProgress(long totalProcessed, long totalSize, int written)
    {
        Console.Write($"{totalProcessed} / {totalSize} {(int)(totalProcessed / (double)totalSize * 100)}%\r");
    }

    public static Stream CreateStream()
    {
        FileStream stream = File.Open(TestPathPatch, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream;
    }

    public static (Stream, bool) CreateStream(long position)
    {
        FileStream stream = File.Open(TestPathPatchLzma, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return (stream, false);
    }

    public static ValueTask<(Stream, bool)> CreateStreamAsync(long position, CancellationToken token)
    {
        FileStream stream = File.Open(TestPathPatchLzma, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return new ValueTask<(Stream, bool)>((stream, false));
    }
}