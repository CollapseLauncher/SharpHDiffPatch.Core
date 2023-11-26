using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hi3Helper.SharpHDiffPatch
{
    public sealed class PatchSingle : IPatch
    {
        private HDiffInfo hDiffInfo;
        private Func<Stream> spawnPatchStream;
        private CancellationToken token;

        private bool isUseBufferedPatch = false;
        private bool isUseFullBuffer = false;
        private bool isUseFastBuffer = false;

        public PatchSingle(HDiffInfo hDiffInfo, CancellationToken token)
        {
            this.token = token;
            this.hDiffInfo = hDiffInfo;
            this.spawnPatchStream = new Func<Stream>(() => new FileStream(hDiffInfo.patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer)
        {
            isUseBufferedPatch = useBufferedPatch;
            isUseFullBuffer = useFullBuffer;
            isUseFastBuffer = useFastBuffer;

            using (FileStream inputStream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream outputStream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (inputStream.Length != hDiffInfo.oldDataSize)
                    throw new InvalidDataException($"[PatchSingle::Patch] The patch directory is expecting old size to be equivalent as: {hDiffInfo.oldDataSize} bytes, but the input file has unmatch size: {inputStream.Length} bytes!");

                HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Existing old file size: {inputStream.Length} is matched!", Verbosity.Verbose);
                HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Staring patching routine at position: {hDiffInfo.headInfo.headEndPos}", Verbosity.Verbose);

                IPatchCore patchCore = null;
                if (isUseFastBuffer && isUseBufferedPatch)
                    patchCore = new PatchCoreFastBuffer(token, hDiffInfo.newDataSize, Stopwatch.StartNew(), input, output);
                else
                    patchCore = new PatchCore(token, hDiffInfo.newDataSize, Stopwatch.StartNew(), input, output);

                StartPatchRoutine(inputStream, outputStream, patchCore);
            }
        }

        private void StartPatchRoutine(Stream inputStream, Stream outputStream, IPatchCore patchCore)
        {
            bool isCompressed = hDiffInfo.compMode != CompressionMode.nocomp;

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
                long offset = hDiffInfo.headInfo.headEndPos;
                int coverPadding = hDiffInfo.headInfo.compress_cover_buf_size > 0 ? padding : 0;
                clips[0] = patchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[0], offset + coverPadding,
                    hDiffInfo.headInfo.cover_buf_size, hDiffInfo.headInfo.compress_cover_buf_size, out long nextLength, this.isUseBufferedPatch, false);

                offset += nextLength;
                int rle_ctrlBufPadding = hDiffInfo.headInfo.compress_rle_ctrlBuf_size > 0 ? padding : 0;
                clips[1] = patchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[1], offset + rle_ctrlBufPadding,
                    hDiffInfo.headInfo.rle_ctrlBuf_size, hDiffInfo.headInfo.compress_rle_ctrlBuf_size, out nextLength, this.isUseBufferedPatch, this.isUseFastBuffer);

                offset += nextLength;
                int rle_codeBufPadding = hDiffInfo.headInfo.compress_rle_codeBuf_size > 0 ? padding : 0;
                clips[2] = patchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[2], offset + rle_codeBufPadding,
                    hDiffInfo.headInfo.rle_codeBuf_size, hDiffInfo.headInfo.compress_rle_codeBuf_size, out nextLength, this.isUseBufferedPatch, this.isUseFastBuffer);

                offset += nextLength;
                int newDataDiffPadding = hDiffInfo.headInfo.compress_newDataDiff_size > 0 ? padding : 0;
                clips[3] = patchCore.GetBufferStreamFromOffset(hDiffInfo.compMode, sourceClips[3], offset + newDataDiffPadding,
                    hDiffInfo.headInfo.newDataDiff_size, hDiffInfo.headInfo.compress_newDataDiff_size - padding, out _, this.isUseBufferedPatch && this.isUseFullBuffer, false);

                patchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);
            }
            catch
            {
                throw;
            }
            finally
            {
                foreach (Stream clip in clips) clip?.Dispose();
                foreach (Stream clip in sourceClips) clip?.Dispose();
            }
        }
    }
}
