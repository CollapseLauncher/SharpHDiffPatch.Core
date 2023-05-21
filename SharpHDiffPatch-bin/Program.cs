using System;
using System.IO;
using System.Reflection;
using Hi3Helper.SharpHDiffPatch;

namespace SharpHDiffPatchBin
{
    public static class PatcherBin
    {
        public static void Main(params string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            if (args.Length == 1 && (args[0].ToLower() == "-v" || args[0].ToLower() == "--version" || args[0].ToLower() == "-version"))
            {
                ShowVersion();
                return;
            }

            if (args.Length < 3)
            {
                Console.WriteLine("Argument is incomplete/incorrect!");
                ShowUsage();
                return;
            }

            string inputPath = args[0];
            string patchPath = args[1];
            string outputPath = args[2];
            bool isUseBufferedPatch = false;

            if (args.Length == 4)
            {
                if (!bool.TryParse(args[3], out isUseBufferedPatch))
                {
                    Console.WriteLine("Invalid parameter for useBuffer!");
                    ShowUsage();
                    return;
                }
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine("Input file doesn't exist!");
                return;
            }

            if (!File.Exists(patchPath))
            {
                Console.WriteLine("Patch file doesn't exist!");
                return;
            }

            try
            {
                HDiffPatch patcher = new HDiffPatch();
                patcher.Initialize(patchPath);
                patcher.Patch(inputPath, outputPath, isUseBufferedPatch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error has occured!\r\n{ex}");
                Console.WriteLine("\r\nPress any key to exit...");
                Console.Read();
            }
        }

        private static void ShowVersion()
        {
            string version = Assembly.GetExecutingAssembly().GetName()?.Version?.ToString() ?? "";
            Console.WriteLine($"Sharp-HDiffPatch v{version}");
        }

        private static void ShowUsage()
        {
            string exeName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            ShowVersion();
            Console.WriteLine($"""
                Usage: {exeName} [input_path] [patch_path] [output_path] (useBuffer: true/false [default: false])
                    
                Example:
                    {exeName} Bank01.pck Bank01.pck.diff Bank01.pcknew

                Or if you want to enable buffer for patching process:
                    {exeName} Bank01.pck Bank01.pck.diff Bank01.pcknew true

                Note:
                - The output path is in "force" mode. Meaning that it will overwrite an existing file if exist.
                - This patcher doesn't support patch file with compression.
                - This doesn't support dirPatch (directory patch) at the moment. But we will bring it in the future.
                """);
        }
    }
}
