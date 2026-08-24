using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpHPatchZ.Header;

// ReSharper disable InconsistentNaming
// ReSharper disable CommentTypo

namespace SharpHPatchZ.Program
{
    public static class PatcherBin
    {
        private static readonly string[]    SizeSuffixes     = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        private const           int         RefreshInterval  = 100;
        private static readonly RootCommand Command          = new();
        private static readonly Stopwatch   Stopwatch        = Stopwatch.StartNew();
        private static readonly Stopwatch   RefreshStopwatch = Stopwatch.StartNew();

        public enum BufferSize
        {
            BigBuffer,
            MediumBuffer,
            SmallBuffer,
            OptimizedForHDD
        }

        public static int Main(params string[] args)
        {
            Argument<string>   inputPathArg, patchPathArg, outputPathArg;
            Option<BufferSize> bufferModeOpt;
            Option<int>        threadsOpt;
            Option<bool>       isKuroTypeOpt;

            string inputPath, patchPath, outputPath;
            bool   isKuroType;
            int    threads;

            Command.AddArgument(inputPathArg = new Argument<string>("Input File", "Input path of the old file/folder to patch"));
            Command.AddArgument(patchPathArg = new Argument<string>("Patch File", "Patch file path to produce the new version of the file/folder"));
            Command.AddArgument(outputPathArg = new Argument<string>("Output File", "Output path of the new version to be produced"));
            Command.AddOption(bufferModeOpt = new Option<BufferSize>(["-b", "--buffer-mode"], () => BufferSize.BigBuffer,
                                                                     """
                                                                     Determines the buffering mode for reading the clips of the patch files.
                                                                     [BigBuffer]
                                                                     Buffer Size = Up to 16 MB per chunks
                                                                     
                                                                     [MediumBuffer]
                                                                     Buffer Size = Up to 1 MB per chunks
                                                                     
                                                                     [SmallBuffer]
                                                                     Buffer Size = Up to 128 KB per chunks
                                                                     
                                                                     [OptimizedForHDD]
                                                                     Thread = 1, Buffer Size = Up to 16 MB per chunks
                                                                     (Note: -p/--parallel-threads will be ignored with this mode)
                                                                     
                                                                     
                                                                     """));
            Command.AddOption(threadsOpt = new Option<int>(["-p", "--parallel-threads"], () => Environment.ProcessorCount,
                                                           "Determines how much parallel threads to be run."));
            Command.AddOption(isKuroTypeOpt = new Option<bool>(["-k", "--kuro"], () => false,
                                                               "Defining that the patch file is a Kuro Games HDiff format."));

            Command.SetHandler((context) =>
            {
                inputPath  = context.ParseResult.GetValueForArgument(inputPathArg);
                patchPath  = context.ParseResult.GetValueForArgument(patchPathArg);
                outputPath = context.ParseResult.GetValueForArgument(outputPathArg);
                isKuroType = context.ParseResult.GetValueForOption(isKuroTypeOpt);

                BufferSize bufferSize = context.ParseResult.GetValueForOption(bufferModeOpt);
                PatchOptions options = bufferSize switch
                {
                    BufferSize.BigBuffer       => PatchOptions.BigBuffer,
                    BufferSize.MediumBuffer    => PatchOptions.Default,
                    BufferSize.SmallBuffer     => PatchOptions.SmallBuffer,
                    BufferSize.OptimizedForHDD => PatchOptions.OptimizeForHDD,
                    _                          => PatchOptions.BigBuffer
                };

                InitializeOptions initializeOptions = new()
                {
                    IsKuroGamesHDiff = isKuroType
                };

                if (bufferSize != BufferSize.OptimizedForHDD)
                {
                    threads = context.ParseResult.GetValueForOption(threadsOpt);
                    options = options with
                    {
                        ParallelThreads = (uint)threads
                    };
                }

                try
                {
                    ProgressCallback progressCallback = ProgressCallback.CreateFromManaged(PatchProgress);

                    using HDiffInfo info = HPatch.CreateInstance(CreatePatchStream, initializeOptions);
                    PatchResult? result = HPatch.Patch(info, CreatePatchStream, inputPath, outputPath, options, progressCallback);
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
}
