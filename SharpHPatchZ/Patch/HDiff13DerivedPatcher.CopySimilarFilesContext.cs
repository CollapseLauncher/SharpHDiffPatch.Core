using System.Buffers;
using System.IO;
using System.Threading;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Patch;

internal sealed partial class HDiff13DerivedPatcher
{
    private class CopySimilarFilesContext(string inputDir,
                                          string[] inputPaths,
                                          string outputDir,
                                          string[] outputPaths)
    {
        private string InputDir  { get; } = inputDir;
        private string OutputDir { get; } = outputDir;

        private string[] InputPaths  { get; } = inputPaths;
        private string[] OutputPaths { get; } = outputPaths;

        public void RunCopy(PatcherBase       patcher,
                            PatchOptions      options,
                            CancellationToken token)
        {
            int bufferSize                  = options.CopyBufferSize;
            if (bufferSize <= 0) bufferSize = 16 << 10;

            byte[] buffer = BigArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int count = InputPaths.Length;
                token.ThrowIfCancellationRequested();

                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string inputPath  = Path.GetFullPath(Path.Combine(InputDir,  InputPaths[i]));
                    string outputPath = Path.GetFullPath(Path.Combine(OutputDir, OutputPaths[i]));

                    if (Path.GetDirectoryName(outputPath) is { } outputDir)
                        Directory.CreateDirectory(outputDir);

                    using FileStream inputStream  = File.Open(inputPath, FileMode.Open, FileAccess.Read);
                    using FileStream outputStream = File.Create(outputPath, bufferSize);
                    int              read;
                    while ((read = inputStream.Read(buffer, 0, bufferSize)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        outputStream.Write(buffer, 0, read);
                        patcher.AdvanceProgress(read);
                    }
                }
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
