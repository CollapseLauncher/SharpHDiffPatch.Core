using System;
using System.Diagnostics;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    public sealed class PatchSingle : IPatch
    {
        private CompressedHDiffInfo hDiffInfo;
        private Func<Stream> spawnPatchStream;

        private bool isUseBufferedPatch = false;

        public PatchSingle(CompressedHDiffInfo hDiffInfo)
        {
            this.hDiffInfo = hDiffInfo;
            this.spawnPatchStream = new Func<Stream>(() => new FileStream(hDiffInfo.patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch)
        {
            isUseBufferedPatch = useBufferedPatch;

            using (FileStream inputStream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream outputStream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                TryCheckMatchOldSize(inputStream);

                StartPatchRoutine(inputStream, outputStream);
            }
        }

        private void TryCheckMatchOldSize(Stream inputStream)
        {
            if (inputStream.Length != (long)hDiffInfo.oldDataSize)
            {
                throw new InvalidDataException($"The patch file is expecting old size to be equivalent as: {hDiffInfo.oldDataSize} bytes, but the input file has unmatch size: {inputStream.Length} bytes!");
            }

            Console.WriteLine("Patch Information:");
            Console.WriteLine($"    Old Size: {hDiffInfo.oldDataSize} bytes");
            Console.WriteLine($"    New Size: {hDiffInfo.newDataSize} bytes");
            Console.WriteLine($"    Is Use Buffered Patch: {isUseBufferedPatch}");

            Console.WriteLine();
            Console.WriteLine("Technical Information:");
            Console.WriteLine($"    Cover Data Offset: {hDiffInfo.headInfo.headEndPos}");
            Console.WriteLine($"    Cover Data Size: {hDiffInfo.headInfo.cover_buf_size}");
            Console.WriteLine($"    Cover Count: {hDiffInfo.headInfo.coverCount}");

            Console.WriteLine($"    RLE Data Offset: {hDiffInfo.headInfo.coverEndPos}");
            Console.WriteLine($"    RLE Control Data Size: {hDiffInfo.headInfo.rle_ctrlBuf_size}");
            Console.WriteLine($"    RLE Code Data Size: {hDiffInfo.headInfo.rle_codeBuf_size}");

            Console.WriteLine($"    New Diff Data Size: {hDiffInfo.headInfo.newDataDiff_size}");
            Console.WriteLine();
        }

        private void StartPatchRoutine(Stream inputStream, Stream outputStream)
        {
            bool isCompressed = hDiffInfo.compMode != CompressionMode.nocomp;

            HDiffPatch.currentSizePatched = 0;
            HDiffPatch.totalSizePatched = (long)hDiffInfo.newDataSize;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Stream[] clips = new Stream[4];
            Stream[] sourceClips = new Stream[4]
            {
                spawnPatchStream(),
                spawnPatchStream(),
                spawnPatchStream(),
                spawnPatchStream()
            };

            int padding = hDiffInfo.compMode == CompressionMode.zlib ? 1 : 0;

            try
            {
                long offset = (long)hDiffInfo.headInfo.headEndPos;
                clips[0] = PatchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[0], offset + padding,
                    (long)hDiffInfo.headInfo.cover_buf_size, (long)hDiffInfo.headInfo.compress_cover_buf_size, out long nextLength, this.isUseBufferedPatch);

                offset += nextLength;
                clips[1] = PatchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[1], offset + padding,
                    (long)hDiffInfo.headInfo.rle_ctrlBuf_size, (long)hDiffInfo.headInfo.compress_rle_ctrlBuf_size, out nextLength, this.isUseBufferedPatch);

                offset += nextLength;
                clips[2] = PatchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[2], offset + padding,
                    (long)hDiffInfo.headInfo.rle_codeBuf_size, (long)hDiffInfo.headInfo.compress_rle_codeBuf_size, out nextLength, this.isUseBufferedPatch);

                offset += nextLength;
                clips[3] = PatchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[3], offset + padding,
                    (long)hDiffInfo.headInfo.newDataDiff_size, (long)hDiffInfo.headInfo.compress_newDataDiff_size - padding, out _, false);

                PatchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);

                TimeSpan timeTaken = stopwatch.Elapsed;
                Console.WriteLine($"Patch has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
            }
            catch
            {
                throw;
            }
            finally
            {
                stopwatch.Stop();
                foreach (Stream clip in clips) clip?.Dispose();
                foreach (Stream clip in sourceClips) clip?.Dispose();
            }
        }
    }
}
