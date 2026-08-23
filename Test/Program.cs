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
    private static string TestPathInput       = @"G:\UnityPlayer-SEA.dll";
    private static string TestPathOutput      = @"G:\UnityPlayer-CN-Dotnet.dll";
    private static string TestPathPatch       = @"G:\UnityPlayer.lzma2.diff";
    private static string TestPathPatchLzma   = @"G:\UnityPlayer.lzma.diff";
    private static string TestPathPatchNoComp = @"G:\UnityPlayer.nocomp.diff";
    public static async Task Main()
    {
        Stopwatch  sw        = Stopwatch.StartNew();
        HDiffPatch hPatchOld = new();

        long oldImplNewSize      = HDiffPatch.GetHDiffNewSize(TestPathPatchNoComp);
        long oldImplTotalWritten = 0;

        hPatchOld.Initialize(CreateStream);
        hPatchOld.Patch(TestPathInput, TestPathOutput, true, (written) =>
        {
            Interlocked.Add(ref oldImplTotalWritten, written);
            Console.Write($"{oldImplTotalWritten} / {oldImplNewSize} {(int)(oldImplTotalWritten / (double)oldImplNewSize * 100)}%\r");
        });
        Console.WriteLine();
        Console.WriteLine($"Old Implementation completed in: {sw.ElapsedMilliseconds} ms");

        ProgressCallback progressCallback = ProgressCallback.CreateFromManaged(WriteProgress);
        PatchOptions     options          = PatchOptions.BigBuffer;

        sw.Restart();
        using (HDiffInfo info1 = await HPatch.CreateInstanceAsync(CreateStreamAsync))
        {
            PatchResult result = await HPatch.PatchAsync(info1, CreateStreamAsync, TestPathInput, TestPathOutput, options, progressCallback: progressCallback);
        }

        Console.WriteLine();
        Console.WriteLine($"New Implementation completed in: {sw.ElapsedMilliseconds} ms");
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
        FileStream stream = File.Open(TestPathPatch, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return (stream, false);
    }

    public static ValueTask<(Stream, bool)> CreateStreamAsync(long position, CancellationToken token)
    {
        FileStream stream = File.Open(TestPathPatch, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return new ValueTask<(Stream, bool)>((stream, false));
    }
}