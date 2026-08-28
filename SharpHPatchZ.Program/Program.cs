using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;

// ReSharper disable InconsistentNaming
// ReSharper disable CommentTypo

namespace SharpHPatchZ.Program;

public static class PatcherBin
{
    private static readonly string[]    SizeSuffixes     = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
    private const           int         RefreshInterval  = 100;
    private static readonly RootCommand Command          = new();
    private static readonly Stopwatch   Stopwatch        = Stopwatch.StartNew();
    private static readonly Stopwatch   RefreshStopwatch = Stopwatch.StartNew();

    public enum PatchPreset
    {
        Performance,
        Balanced,
        MemoryOptimized,
        OptimizedForHDD
    }

    private static Argument<T> RegisterTo<T>(this Argument<T> argument, Command command)
    {
        command.AddArgument(argument);
        return argument;
    }

    private static Option<T> RegisterTo<T>(this Option<T> option, Command command)
    {
        command.AddOption(option);
        return option;
    }

    public static int Main(params string[] args)
    {
        Argument<string> inputPathArg = new Argument<string>("Input File", "Input path of the old file/folder to patch").RegisterTo(Command);
        Argument<string> patchPathArg = new Argument<string>("Patch File", "Patch file path to produce the new version of the file/folder").RegisterTo(Command);
        Argument<string> outputPathArg = new Argument<string>("Output File", "Output path of the new version to be produced").RegisterTo(Command);

        Option<PatchPreset> bufferModeOpt = new Option<PatchPreset>(["-p", "--preset"], () => PatchPreset.Performance,
                                                                    """
                                                                    Determines the preset of the patching mode and also set its buffer size.
                                                                    [Performance]
                                                                    Buffer Size = Up to 16 MB per chunks
                                                                    Threads     = Automatic

                                                                    [Balanced]
                                                                    Buffer Size = Up to 1 MB per chunks
                                                                    Threads     = Automatic

                                                                    [MemoryOptimized]
                                                                    Buffer Size = Up to 128 KB per chunks
                                                                    Threads     = Automatic

                                                                    [OptimizedForHDD]
                                                                    Buffer Size = Up to 16 MB per chunks
                                                                    Threads     = 1
                                                                    (Note: -p/--parallel-threads will be ignored with this mode)


                                                                    """).RegisterTo(Command);

        Option<uint> threadsOpt = new Option<uint>(["-t", "--parallel-threads"], () => (uint)Environment.ProcessorCount,
                                                   "Determines how much parallel threads to be run.").RegisterTo(Command);
        Option<bool> isKuroTypeOpt = new Option<bool>(["-k", "--kuro"],
                                                      "Defining that the patch file is a Kuro Games HDiff format.").RegisterTo(Command);

#if NET6_0_OR_GREATER
        Option<bool> useSimdOpt = new Option<bool>(["--simd"],
                                                   () => true,
                                                   "Whether to use SIMD calculation while performing RLE additions.").RegisterTo(Command);
#endif

        Option<uint> bufferCopyBufferSizeOpt = new Option<uint>(["--buffer-copy"],
                                                                "Determines how big the buffer size while performing copy routines to similar files.").RegisterTo(Command);
        Option<uint> bufferPatchWorkerBufferSizeOpt = new Option<uint>(["--buffer-patch"],
                                                                       "Determines how big the buffer size used by the patch worker.").RegisterTo(Command);
        Option<uint> bufferReaderBufferSizeOpt = new Option<uint>(["--buffer-reader"],
                                                                  "Determines how big the buffer size used by the RLE Stream Reader.").RegisterTo(Command);

        Command.SetHandler((context) =>
        {
            string inputPath  = context.ParseResult.GetValueForArgument(inputPathArg);
            string patchPath  = context.ParseResult.GetValueForArgument(patchPathArg);
            string outputPath = context.ParseResult.GetValueForArgument(outputPathArg);
            bool   isKuroType = context.ParseResult.GetValueForOption(isKuroTypeOpt);
            uint   threads    = context.ParseResult.GetValueForOption(threadsOpt);

#if NET6_0_OR_GREATER
            bool useSimd = context.ParseResult.GetValueForOption(useSimdOpt);
#endif

            uint bufferCopyBufferSize        = context.ParseResult.GetValueForOption(bufferCopyBufferSizeOpt);
            uint bufferPatchWorkerBufferSize = context.ParseResult.GetValueForOption(bufferPatchWorkerBufferSizeOpt);
            uint bufferReaderBufferSize      = context.ParseResult.GetValueForOption(bufferReaderBufferSizeOpt);

            PatchPreset patchPreset = context.ParseResult.GetValueForOption(bufferModeOpt);
            PatchOptions options = patchPreset switch
            {
                PatchPreset.Performance     => PatchOptions.BigBuffer,
                PatchPreset.Balanced        => PatchOptions.Default,
                PatchPreset.MemoryOptimized => PatchOptions.SmallBuffer,
                PatchPreset.OptimizedForHDD => PatchOptions.OptimizeForHDD,
                _                           => PatchOptions.BigBuffer
            };

            if (bufferCopyBufferSize != 0) options.CopyBufferSize               = (int)bufferCopyBufferSize;
            if (bufferPatchWorkerBufferSize != 0) options.PatchWorkerBufferSize = (int)bufferPatchWorkerBufferSize;
            if (bufferReaderBufferSize != 0) options.ReaderBufferSize           = (int)bufferReaderBufferSize;

            InitializeOptions initializeOptions = new()
            {
                IsKuroGamesHDiff = isKuroType
            };

#if NET6_0_OR_GREATER
            if (useSimd) options.UseSIMD = useSimd;
#endif

            if (patchPreset != PatchPreset.OptimizedForHDD)
            {
                threads = context.ParseResult.GetValueForOption(threadsOpt);
                options = options with
                {
                    ParallelThreads = threads
                };
            }

            try
            {
                ProgressCallback progressCallback = ProgressCallback.CreateFromManaged(PatchProgress);

                using HDiffInfo info = HPatch.CreateInstance(CreatePatchStream, initializeOptions);
                PrintFileInfo(info, inputPath, patchPath, outputPath, options, patchPreset);
                PatchResult result = HPatch.Patch(info, CreatePatchStream, inputPath, outputPath, options, progressCallback);
                Console.WriteLine();

                if (result.Exception != null)
                {
                    Console.WriteLine($"Patching process throws an error: {result.Exception}");
                    context.ExitCode = result;
                    return;
                }
                Console.WriteLine($"Patch completed in: {Stopwatch.Elapsed:c} ({Stopwatch.Elapsed.TotalSeconds} seconds)");

                (Stream, bool) CreatePatchStream(long position)
                {
                    FileStream stream = File.Open(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    stream.Position = position;
                    return (stream, false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error has occurred! [{ex.GetType().Name}]: {ex.Message}\r\nStack Trace:\r\n{ex.StackTrace}");
                context.ExitCode = int.MinValue;
#if DEBUG
                throw;
#endif
            }
            finally
            {
                Stopwatch.Stop();
                RefreshStopwatch.Stop();
            }
        });

        return Command.Invoke(args);
    }

#if NET6_0_OR_GREATER
    private static string DetermineSIMDCapability(PatchOptions options)
    {
        return options.UseSIMD switch
        {
            true when Avx2.IsSupported             => "true (AVX2)",
            true when Sse2.IsSupported             => "true (SSE2)",
            true when Vector.IsHardwareAccelerated => "true {Runtime-determined}",
            _                                      => "false (Scalar)"
        };
    }
#endif

    private static unsafe void PrintFileInfo(HDiffInfo    info,
                                             string       inputPath,
                                             string       patchPath,
                                             string       outputPath,
                                             PatchOptions options,
                                             PatchPreset  patchPresetType)
    {
        StringBuilder sb = new();
        sb.AppendLine($"""
                       Input Path       : {inputPath}
                       Patch Path       : {patchPath}
                       Output Path      : {outputPath}
                       Buffer Type      : {patchPresetType}
                       Diff Type        : {info.MagicType}
                       Diff File Size   : {new FileInfo(patchPath).Length}
                       Compression Type : {info.CompressionType}
                       
                       Options:
                           Max. Parallel Threads    : {options.ParallelThreads}
                           Copy Buffer Size         : {options.CopyBufferSize}
                           RLE Reader Buffer Size   : {options.ReaderBufferSize}
                           Patch Worker Buffer Size : {options.PatchWorkerBufferSize}
                       """);

#if NET6_0_OR_GREATER
        sb.AppendLine($"    Use SIMD?                : {DetermineSIMDCapability(options)}");
#endif

        if (HPatch.TryGetDirectoryPatchMetadata(ref info, out DirectoryPatchMetadata dirPatchMetadata))
        {
            sb.AppendLine($"""
                           
                           Directory Patch Info (HDiff19 Extension):
                               Checksum Type               : {info.ChecksumType}
                               New Path Count              : {dirPatchMetadata.OutputPathListP->Length} (File Count: {dirPatchMetadata.SameFilePathCountSizeInfoP->Count + dirPatchMetadata.OutputFileIndexListP->Length})
                               Identical File Count        : {dirPatchMetadata.SameFilePathCountSizeInfoP->Count} (Total Size: {dirPatchMetadata.SameFilePathCountSizeInfoP->Size})
                               Input Reference File Count  : {dirPatchMetadata.InputFileIndexListP->Length} (Total Size: {dirPatchMetadata.InputPathCountSizeInfoP->Size})
                               Output Reference File Count : {dirPatchMetadata.OutputFileIndexListP->Length} (Total Size: {dirPatchMetadata.OutputPathCountSizeInfoP->Size})
                               Input Total File Size       : {dirPatchMetadata.InputPathCountSizeInfoP->Size + dirPatchMetadata.SameFilePathCountSizeInfoP->Size}
                               Output Total File Size      : {dirPatchMetadata.OutputPathCountSizeInfoP->Size + dirPatchMetadata.SameFilePathCountSizeInfoP->Size}
                           """);

            if (dirPatchMetadata.InputFileSizeListP != null &&
                dirPatchMetadata.OutputFileHashesListP != null)
            {
                sb.AppendLine($"""
                               
                               Kuro Games HDiff Extension Info:
                                   Input File Asserted Count : {dirPatchMetadata.InputFileSizeListP->Length}
                                   Output File Hashes Count  : {dirPatchMetadata.OutputFileHashesListP->Length}
                               """);
            }
        }

        if (HPatch.TryGetPatchMetadata(ref info, out PatchMetadata patchMetadata))
        {
            sb.AppendLine($"""
                           
                           Generic Patch Info:
                               Input Size           : {patchMetadata.DiffOldSize}
                               Output Size          : {patchMetadata.DiffNewSize}
                               RLE Cover Info Count : {patchMetadata.CoverDataCount}
                               RLE Cover Info Size  : {patchMetadata.CoverDataSizeP->Size} (Compressed Size: {patchMetadata.CoverDataSizeP->CompressedSize})
                               RLE Control Size     : {patchMetadata.RleControlDataSizeP->Size} (Compressed Size: {patchMetadata.RleControlDataSizeP->CompressedSize})
                               RLE Code Size        : {patchMetadata.RleCodeDataSizeP->Size} (Compressed Size: {patchMetadata.RleCodeDataSizeP->CompressedSize})
                               RLE New Data Size    : {patchMetadata.NewDiffDataSizeP->Size} (Compressed Size: {patchMetadata.NewDiffDataSizeP->CompressedSize})
                           """);
        }

        Console.WriteLine(sb.ToString());
    }

    private static double   lastSpeed;
    private static TimeSpan lastRemainedTime = TimeSpan.Zero;

    public static void PatchProgress(long totalProcessed, long totalSize, int written)
    {
        double speed   = CalculateSpeed(written);
        double percent = Math.Round(totalProcessed / (double)totalSize * 100, 2);

        if (CheckIfNeedRefreshStopwatch())
        {
            lastRemainedTime = TimeSpan.FromSeconds((totalSize - totalProcessed) / UnZeroed(speed));
            Interlocked.Exchange(ref lastSpeed, speed);
        }

        Console.Write(
            $"Patching: {percent}% | " +
            $"{SummarizeSizeSimple(totalProcessed)}/{SummarizeSizeSimple(totalSize)} " +
            $"@{SummarizeSizeSimple(lastSpeed)}/s " +
            $"[{string.Format("{0:hh}h:{0:mm}m:{0:ss}s remaining", lastRemainedTime)}]    \r");

        return;
        static double UnZeroed(double input) => Math.Max(input, 1);
    }
    private const  double ScOneSecond = 1000;
    private static long   _scLastTick = Environment.TickCount;
    private static long   _scLastReceivedBytes;
    private static double _scLastSpeed;
    private static int    _riLastTick = Environment.TickCount;

    private static double CalculateSpeed(long receivedBytes) => CalculateSpeed(receivedBytes, ref _scLastSpeed, ref _scLastReceivedBytes, ref _scLastTick);

    private static double CalculateSpeed(long receivedBytes, ref double lastSpeedToUse, ref long lastReceivedBytesToUse, ref long lastTickToUse)
    {
        long   currentTick           = Environment.TickCount - lastTickToUse + 1;
        long   totalReceivedInSecond = Interlocked.Add(ref lastReceivedBytesToUse, receivedBytes);
        double speed                 = totalReceivedInSecond * ScOneSecond / currentTick;

        if (!(currentTick > ScOneSecond))
        {
            return lastSpeedToUse;
        }

        lastSpeedToUse = speed;
        _              = Interlocked.Exchange(ref lastSpeedToUse,         speed);
        _              = Interlocked.Exchange(ref lastReceivedBytesToUse, 0);
        _              = Interlocked.Exchange(ref lastTickToUse,          Environment.TickCount);
        return lastSpeedToUse;
    }

    public static bool CheckIfNeedRefreshStopwatch()
    {
        int currentTick = Environment.TickCount - _riLastTick;
        if (currentTick <= RefreshInterval)
        {
            return false;
        }

        Interlocked.Exchange(ref _riLastTick, Environment.TickCount);
        return true;
    }

    private static string SummarizeSizeSimple(double value, int decimalPlaces = 2)
    {
        byte mag = (byte)Math.Log(value, 1000);

        return $"{Math.Round(value / (1L << (mag * 10)), decimalPlaces)} {SizeSuffixes[mag]}";
    }
}
