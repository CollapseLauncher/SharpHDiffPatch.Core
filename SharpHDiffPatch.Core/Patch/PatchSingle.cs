using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpHDiffPatch.Core.Binary;
using SharpHDiffPatch.Core.Binary.Compression;

namespace SharpHDiffPatch.Core.Patch
{
    public sealed class PatchSingle(HeaderInfo headerInfo, CancellationToken token) : IPatch
    {
        private readonly Func<Stream> _spawnPatchStream = headerInfo.PatchCreateStream ?? (() => new FileStream(headerInfo.PatchPath, FileMode.Open, FileAccess.Read, FileShare.Read));

        private bool _isUseBufferedPatch;
        private bool _isUseFullBuffer;
        private bool _isUseFastBuffer;

        public void Patch(string input, string output, Action<long> writeBytesDelegate, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer)
        {
            _isUseBufferedPatch = useBufferedPatch;
            _isUseFullBuffer = useFullBuffer;
            _isUseFastBuffer = useFastBuffer;

            using FileStream inputStream  = new(input, FileMode.Open, FileAccess.Read, FileShare.Read, headerInfo.OldDataSize.GetFileStreamBufferSize());
            using FileStream outputStream = new(output, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, headerInfo.NewDataSize.GetFileStreamBufferSize());
            if (inputStream.Length != headerInfo.OldDataSize)
                throw new InvalidDataException($"[PatchSingle::Patch] The patch directory is expecting old size to be equivalent as: {headerInfo.OldDataSize} bytes, but the input file has unmatched size: {inputStream.Length} bytes!");

            HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Existing old file size: {inputStream.Length} is matched!", Verbosity.Verbose);
            HDiffPatch.Event.PushLog($"[PatchSingle::Patch] Staring patching routine at position: {headerInfo.ChunkInfo.HeadEndPos}", Verbosity.Verbose);

            IPatchCore patchCore = CreatePatchCore(input, output, writeBytesDelegate);

            StartPatchRoutine(inputStream, outputStream, patchCore);
        }

        private IPatchCore CreatePatchCore(string input, string output, Action<long> writeBytesDelegate)
        {
            bool wantFastBuffer = _isUseFastBuffer && _isUseBufferedPatch;
            if (wantFastBuffer && PatchSizeHelper.CanUseFastBuffer(headerInfo))
                return new PatchCoreFastBuffer(headerInfo.NewDataSize, Stopwatch.StartNew(), input, output, writeBytesDelegate, token);

            if (wantFastBuffer)
                HDiffPatch.Event.PushLog("[PatchSingle::CreatePatchCore] Fast buffer disabled: patch chunk sizes exceed int32-safe limits; using streaming patch core.");

            return new PatchCore(headerInfo.NewDataSize, Stopwatch.StartNew(), input, output, writeBytesDelegate, token);
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

            int padding = headerInfo.CompMode == HDiffCompressionMode.zlib ? 1 : 0;

            try
            {
                long offset = headerInfo.ChunkInfo.HeadEndPos;
                int coverPadding = headerInfo.ChunkInfo.CompressCoverBufSize > 0 ? padding : 0;
                clips[0] = patchCore.GetBufferStreamFromOffset(headerInfo.CompMode, sourceClips[0], offset + coverPadding,
                    headerInfo.ChunkInfo.CoverBufSize, headerInfo.ChunkInfo.CompressCoverBufSize, out long nextLength, _isUseBufferedPatch, false);

                offset += nextLength;
                int rleCtrlBufPadding = headerInfo.ChunkInfo.CompressRleCtrlBufSize > 0 ? padding : 0;
                clips[1] = patchCore.GetBufferStreamFromOffset(headerInfo.CompMode, sourceClips[1], offset + rleCtrlBufPadding,
                    headerInfo.ChunkInfo.RleCtrlBufSize, headerInfo.ChunkInfo.CompressRleCtrlBufSize, out nextLength, _isUseBufferedPatch, _isUseFastBuffer);

                offset += nextLength;
                int rleCodeBufPadding = headerInfo.ChunkInfo.CompressRleCodeBufSize > 0 ? padding : 0;
                clips[2] = patchCore.GetBufferStreamFromOffset(headerInfo.CompMode, sourceClips[2], offset + rleCodeBufPadding,
                    headerInfo.ChunkInfo.RleCodeBufSize, headerInfo.ChunkInfo.CompressRleCodeBufSize, out nextLength, _isUseBufferedPatch, _isUseFastBuffer);

                offset += nextLength;
                int newDataDiffPadding = headerInfo.ChunkInfo.CompressNewDataDiffSize > 0 ? padding : 0;
                clips[3] = patchCore.GetBufferStreamFromOffset(headerInfo.CompMode, sourceClips[3], offset + newDataDiffPadding,
                    headerInfo.ChunkInfo.NewDataDiffSize, headerInfo.ChunkInfo.CompressNewDataDiffSize - padding, out _, _isUseBufferedPatch && _isUseFullBuffer, false);

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
