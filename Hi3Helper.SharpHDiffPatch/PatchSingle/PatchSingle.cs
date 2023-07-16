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
            using (Stream patchStream = spawnPatchStream())
            {
                patchStream.Seek((long)hDiffInfo.headInfo.headEndPos, SeekOrigin.Begin);
                TryCheckMatchOldSize(inputStream);

                StartPatchRoutine(inputStream, patchStream, outputStream);
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

        private void StartPatchRoutine(Stream inputStream, Stream patchStream, Stream outputStream)
        {
            patchStream.Seek((long)hDiffInfo.headInfo.headEndPos, SeekOrigin.Begin);

            HDiffPatch.currentSizePatched = 0;
            HDiffPatch.totalSizePatched = (long)hDiffInfo.newDataSize;

            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!isUseBufferedPatch)
            {
                Stream[] clips = new Stream[4];

                PatchCore.FillBufferClip(hDiffInfo.compMode, patchStream, clips[0] = new MemoryStream(), (long)hDiffInfo.headInfo.cover_buf_size, (long)hDiffInfo.headInfo.compress_cover_buf_size);
                PatchCore.FillBufferClip(hDiffInfo.compMode, patchStream, clips[1] = new MemoryStream(), (long)hDiffInfo.headInfo.rle_ctrlBuf_size, (long)hDiffInfo.headInfo.compress_rle_ctrlBuf_size);
                PatchCore.FillBufferClip(hDiffInfo.compMode, patchStream, clips[2] = new MemoryStream(), (long)hDiffInfo.headInfo.rle_codeBuf_size, (long)hDiffInfo.headInfo.compress_rle_codeBuf_size);
                clips[3] = PatchCore.GetBufferClipsStream(hDiffInfo.compMode, patchStream, (long)hDiffInfo.headInfo.newDataDiff_size, (long)hDiffInfo.headInfo.compress_newDataDiff_size);

                PatchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);
            }
            else
            {
                Console.Write("Buffering clips data into memory... ");
                long bufferTotalSize = (long)hDiffInfo.headInfo.cover_buf_size
                    + (long)hDiffInfo.headInfo.rle_ctrlBuf_size
                    + (long)hDiffInfo.headInfo.rle_codeBuf_size
                    + (long)hDiffInfo.headInfo.newDataDiff_size;

                PatchCore.FillSingleBufferClip(hDiffInfo.compMode, patchStream, out Stream[] singleBufferClips, hDiffInfo.headInfo);
                Console.WriteLine($"Done!\r\n    Clips Buffer size in bytes: {bufferTotalSize}\r\n");

                PatchCore.UncoverBufferClipsStream(singleBufferClips, inputStream, outputStream, hDiffInfo, hDiffInfo.newDataSize);
            }
            stopwatch.Stop();

            TimeSpan timeTaken = stopwatch.Elapsed;
            Console.WriteLine($"Patch has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.Milliseconds} ms)");
        }
    }
}
