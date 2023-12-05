using Hi3Helper.SharpHDiffPatch;
using System;
using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SharpHDiffPatchBin
{
    public static class PatcherBin
    {
        private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        private static readonly Stopwatch RefreshStopwatch = Stopwatch.StartNew();
        private const int RefreshInterval = 100;
        private static RootCommand Command = new RootCommand();

        public static int Main(params string[] args)
        {
            Argument<string> _inputPathArg, _patchPathArg, _outputPathArg;
            Option<BufferMode> _bufferModeOpt;
            Option<bool> _bufferFastOpt;
            Option<Verbosity> _logLevelOpt;

            string inputPath, patchPath, outputPath;
            bool isUseBufferedPatch, isUseFullBuffer, isUseFastBuffer;

            Command.AddArgument(_inputPathArg = new Argument<string>("Input File", "Input path of the old file/folder to patch"));
            Command.AddArgument(_patchPathArg = new Argument<string>("Patch File", "Patch file path to produce the new version of the file/folder"));
            Command.AddArgument(_outputPathArg = new Argument<string>("Output File", "Output path of the new version to be produced"));
            Command.AddOption(_bufferModeOpt = new Option<BufferMode>(new string[] { "-b", "--buffer-mode" }, () => BufferMode.Partial,
                @"Determines the buffering mode for reading the clips of the patch files.
[None]
No buffering and read the clips directly from the disk stream

[Partial]
Buffers only the Cover Code, RLE Control and RLE Code clips only into memory. Read the New Data clip directly from the disk stream.

[Full]
Buffers all clips into memory. This option is the fastest but it requires more memory depending on the patch size."));
            Command.AddOption(_bufferFastOpt = new Option<bool>(new string[] { "-B", "--fast-buffer" }, () => false, "Use array-based buffer for RLE Control and Code clips."));
            Command.AddOption(_logLevelOpt = new Option<Verbosity>(new string[] { "-l", "--log-level" }, () => Verbosity.Info, "Defines the verbosity of the info to be displayed."));

            Command.SetHandler((context) =>
            {
                inputPath = context.ParseResult.GetValueForArgument(_inputPathArg);
                patchPath = context.ParseResult.GetValueForArgument(_patchPathArg);
                outputPath = context.ParseResult.GetValueForArgument(_outputPathArg);

                (isUseBufferedPatch, isUseFullBuffer) = context.ParseResult.GetValueForOption(_bufferModeOpt) switch
                {
                    BufferMode.Full => (true, true),
                    BufferMode.Partial => (true, false),
                    _ => (false, false)
                };

                isUseFastBuffer = context.ParseResult.GetValueForOption(_bufferFastOpt);
                HDiffPatch.LogVerbosity = context.ParseResult.GetValueForOption(_logLevelOpt);

                try
                {
                    HDiffPatch patcher = new HDiffPatch();
                    if (HDiffPatch.LogVerbosity != Verbosity.Quiet)
                    {
                        EventListener.LoggerEvent += EventListener_LoggerEvent;
                        EventListener.PatchEvent += EventListener_PatchEvent;
                    }
#if BENCHMARK
                    Stopwatch benchmarkSw = Stopwatch.StartNew();
                    double[] warmUpAvgs = new double[5];
                    Console.WriteLine($"Warming up {warmUpAvgs.Length} runtime attempts!");
                    for (int h = 0; h < warmUpAvgs.Length; h++)
                    {
                        patcher.Initialize(patchPath);
                        patcher.Patch(inputPath, outputPath, isUseBufferedPatch, default, isUseFullBuffer, isUseFastBuffer);
                        warmUpAvgs[h] = benchmarkSw?.Elapsed.TotalMilliseconds ?? 0;
                        benchmarkSw?.Restart();
                        Console.WriteLine($"  Starting warm-up {h + 1}: {warmUpAvgs[h]} ms");
                    }
                    Console.WriteLine($"Finished warming up runtime attempts in: {warmUpAvgs.Average()} ms");

                    double[] numAvgs = new double[5];
                    Console.WriteLine($"Starting all {numAvgs.Length} runtime attempts!");
                    for (int h = 0; h < numAvgs.Length; h++)
                    {
                        Console.WriteLine($"  Starting Attempt {h + 1}: {numAvgs[h]} ms");
                        int repeat = 20;
                        double[] num = new double[repeat];
                        benchmarkSw?.Restart();
                        for (int i = 0; i < num.Length; i++)
                        {
#endif
                            patcher.Initialize(patchPath);
#if !BENCHMARK
                            RefreshStopwatch?.Restart();
#endif
                            patcher.Patch(inputPath, outputPath, isUseBufferedPatch, default, isUseFullBuffer, isUseFastBuffer);
#if BENCHMARK
                            num[i] = benchmarkSw?.Elapsed.TotalMilliseconds ?? 0;
                            benchmarkSw?.Restart();
                            Console.WriteLine($"    Finished on run {i + 1} - {repeat} Attempt {h + 1}: {num[i]} ms");
                        }

                        numAvgs[h] = num.Average();
                        Console.WriteLine($"  Runtime Attempt {h + 1}: {numAvgs[h]} ms");
                    }
                    Console.WriteLine($"Average all {numAvgs.Length} runtime attempt: {numAvgs.Average()} ms");
#endif
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error has occured! [{ex.GetType().Name}]: {ex.Message}\r\nStack Trace:\r\n{ex.StackTrace}");
                    context.ExitCode = int.MinValue;
#if DEBUG
                    throw;
#endif
                }
                finally
                {
                    RefreshStopwatch?.Stop();
                    if (HDiffPatch.LogVerbosity != Verbosity.Quiet)
                    {
                        EventListener.LoggerEvent -= EventListener_LoggerEvent;
                        EventListener.PatchEvent -= EventListener_PatchEvent;
                    }
                }
            });

            return Command.Invoke(args);
        }

        private static void EventListener_LoggerEvent(object? sender, LoggerEvent e)
        {
            if (HDiffPatch.LogVerbosity == Verbosity.Quiet
            || (HDiffPatch.LogVerbosity == Verbosity.Debug
            && !(e.LogLevel == Verbosity.Debug ||
                 e.LogLevel == Verbosity.Verbose ||
                 e.LogLevel == Verbosity.Info))
            || (HDiffPatch.LogVerbosity == Verbosity.Verbose
            && !(e.LogLevel == Verbosity.Verbose ||
                 e.LogLevel == Verbosity.Info))
            || (HDiffPatch.LogVerbosity == Verbosity.Info
            && !(e.LogLevel == Verbosity.Info))) return;

            PrintLog(e);
        }

        private static void PrintLog(LoggerEvent e)
        {
            string label = e.LogLevel switch
            {
                Verbosity.Info => $"[Info] ",
                Verbosity.Verbose => $"[Verbose] ",
                Verbosity.Debug => $"[Debug] ",
                _ => ""
            };

            Console.WriteLine($"{label}{e.Message}");
        }

        private static async void EventListener_PatchEvent(object? sender, PatchEvent e)
        {
            if (await CheckIfNeedRefreshStopwatch(e.ProgressPercentage))
            {
                Console.Write($"Patching: {e.ProgressPercentage}% | {SummarizeSizeSimple(e.CurrentSizePatched)}/{SummarizeSizeSimple(e.TotalSizeToBePatched)} @{SummarizeSizeSimple(e.Speed)}/s    \r");
            }
        }

        private static async Task<bool> CheckIfNeedRefreshStopwatch(double progress)
        {
            if (RefreshStopwatch.ElapsedMilliseconds > RefreshInterval)
            {
                RefreshStopwatch.Restart();
                return true;
            }

            await Task.Delay(RefreshInterval);
            return false;
        }

        private static string SummarizeSizeSimple(double value, int decimalPlaces = 2)
        {
            byte mag = (byte)Math.Log(value, 1000);

            return string.Format("{0} {1}", Math.Round(value / (1L << (mag * 10)), decimalPlaces), SizeSuffixes[mag]);
        }
    }
}
