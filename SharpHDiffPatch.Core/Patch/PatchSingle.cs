using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpHDiffPatch.Core.Binary.Compression;

namespace SharpHDiffPatch.Core.Patch
{
    public sealed class PatchSingle(HeaderInfo headerInfo, CancellationToken token) : IPatch
    {
        private readonly Func<Stream> _spawnPatchStream = headerInfo.patchCreateStream ?? (() => new FileStream(headerInfo.patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));

        private bool _isUseBufferedPatch;
        private bool _isUseFullBuffer;
        private bool _isUseFastBuffer;

        public void Patch(string input, string output, Action<long> writeBytesDelegate, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer)
        {
            _isUseBufferedPatch = useBufferedPatch;
            _isUseFullBuffer = useFullBuffer;
            _isUseFastBuffer = useFastBuffer;

            using FileStream inputStream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileStream outputStream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (inputStream.Length != headerInfo.oldDataSize)
                throw new InvalidDataException($"[PatchSingle::Patch] The patch directory is expecting old size to be equivalent as: {headerInfo.oldDataSize} bytes, but the input file has unmatched size: {inputStream.Length} bytes!");

            HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Existing old file size: {inputStream.Length} is matched!", Verbosity.Verbose);
            HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Staring patching routine at position: {headerInfo.chunkInfo.headEndPos}", Verbosity.Verbose);

            IPatchCore patchCore;
            if (_isUseFastBuffer && _isUseBufferedPatch)
                patchCore = new PatchCoreFastBuffer(headerInfo.newDataSize, Stopwatch.StartNew(), input, output, writeBytesDelegate, token);
            else
                patchCore = new PatchCore(headerInfo.newDataSize, Stopwatch.StartNew(), input, output, writeBytesDelegate, token);

            StartPatchRoutine(inputStream, outputStream, patchCore);
        }

        private void StartPatchRoutine(Stream inputStream, Stream outputStream, IPatchCore patchCore)
        {
            Stream[] clips = new Stream[4];
            Stream[] sourceClips =
            [
                _spawnPatchStream(),
                _spawnPatchStream(),
                _spawnPatchStream(),
                _spawnPatchStream()
            ];

            int padding = headerInfo.compMode == CompressionMode.zlib ? 1 : 0;

            try
            {
                long offset = headerInfo.chunkInfo.headEndPos;
                int coverPadding = headerInfo.chunkInfo.compress_cover_buf_size > 0 ? padding : 0;
                clips[0] = patchCore.GetBufferStreamFromOffset(headerInfo.compMode, sourceClips[0], offset + coverPadding,
                    headerInfo.chunkInfo.cover_buf_size, headerInfo.chunkInfo.compress_cover_buf_size, out long nextLength, _isUseBufferedPatch, false);

                offset += nextLength;
                int rleCtrlBufPadding = headerInfo.chunkInfo.compress_rle_ctrlBuf_size > 0 ? padding : 0;
                clips[1] = patchCore.GetBufferStreamFromOffset(headerInfo.compMode, sourceClips[1], offset + rleCtrlBufPadding,
                    headerInfo.chunkInfo.rle_ctrlBuf_size, headerInfo.chunkInfo.compress_rle_ctrlBuf_size, out nextLength, _isUseBufferedPatch, _isUseFastBuffer);

                offset += nextLength;
                int rleCodeBufPadding = headerInfo.chunkInfo.compress_rle_codeBuf_size > 0 ? padding : 0;
                clips[2] = patchCore.GetBufferStreamFromOffset(headerInfo.compMode, sourceClips[2], offset + rleCodeBufPadding,
                    headerInfo.chunkInfo.rle_codeBuf_size, headerInfo.chunkInfo.compress_rle_codeBuf_size, out nextLength, _isUseBufferedPatch, _isUseFastBuffer);

                offset += nextLength;
                int newDataDiffPadding = headerInfo.chunkInfo.compress_newDataDiff_size > 0 ? padding : 0;
                clips[3] = patchCore.GetBufferStreamFromOffset(headerInfo.compMode, sourceClips[3], offset + newDataDiffPadding,
                    headerInfo.chunkInfo.newDataDiff_size, headerInfo.chunkInfo.compress_newDataDiff_size - padding, out _, _isUseBufferedPatch && _isUseFullBuffer, false);

                patchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, headerInfo);
            }
            finally
            {
                foreach (Stream clip in clips) clip?.Dispose();
                foreach (Stream clip in sourceClips) clip?.Dispose();
            }
        }
    }
}
