using SharpHDiffPatch.Core.Binary;
using SharpHDiffPatch.Core.Binary.Compression;
using SharpHDiffPatch.Core.Binary.Streams;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SharpHDiffPatch.Core.Patch
{
    internal class PairIndexReference
    {
        public long OldIndex;
        public long NewIndex;

        public override string ToString() => $"OldIndex: {OldIndex}, NewIndex: {NewIndex}";
    }

    internal class DirectoryReferencePair
    {
        internal string[] OldUtf8PathList;
        internal string[] NewUtf8PathList;
        internal long[] OldRefList;
        internal long[] NewRefList;
        internal long[] NewRefSizeList;
        internal PairIndexReference[] DataSamePairList;
        internal long[] NewExecuteList;
    }

    public sealed class PatchDir : IPatch
    {
        private HeaderInfo _headerInfo;
        private readonly DataReferenceInfo _referenceInfo;
        private readonly Func<FileStream> _spawnPatchStream;
        private string _basePathInput;
        private string _basePathOutput;
        private bool _useBufferedPatch;
        private bool _useFullBuffer;
        private bool _useFastBuffer;
#if USEEXPERIMENTALMULTITHREAD
        private bool useMultiThread;
#endif
        private int _padding;
        private readonly CancellationToken _token;

        public PatchDir(HeaderInfo headerInfo, DataReferenceInfo referenceInfo, string patchPath, CancellationToken token
#if USEEXPERIMENTALMULTITHREAD
            , bool useMultiThread
#endif
            )
        {
            _token = token;
            _headerInfo = headerInfo;
            _referenceInfo = referenceInfo;
#if USEEXPERIMENTALMULTITHREAD
            useMultiThread = useMultiThread;
#endif
            _spawnPatchStream = () => new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public void Patch(string input, string output, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer)
        {
            _basePathInput = input;
            _basePathOutput = output;
            _useBufferedPatch = useBufferedPatch;
            _useFullBuffer = useFullBuffer;
            _useFastBuffer = useFastBuffer;

            using FileStream patchStream = _spawnPatchStream();
            _padding = _headerInfo.compMode == CompressionMode.zlib ? 1 : 0;
            HDiffPatch.Event.PushLog($"[PatchDir::Patch] Padding applied: {_padding} byte(s)", Verbosity.Debug);

            int headerPadding = _referenceInfo.headDataCompressedSize > 0 ? _padding : 0;
            patchStream.Position = _referenceInfo.headDataOffset + headerPadding;
            HDiffPatch.Event.PushLog($"[PatchDir::Patch] Patch stream position moved from: {patchStream.Position - (_referenceInfo.headDataOffset + headerPadding)} to: {patchStream.Position}", Verbosity.Debug);

            HDiffPatch.Event.PushLog($"[PatchDir::Patch] Getting stream for header at size: {_referenceInfo.headDataSize} bytes ({_referenceInfo.headDataCompressedSize - headerPadding} bytes compressed)", Verbosity.Verbose);

            IPatchCore patchCore;
#if USEEXPERIMENTALMULTITHREAD
                if (this.useMultiThread && !headerInfo.isSingleCompressedDiff)
                    patchCore = new PatchCoreMultiThread(token, headerInfo.newDataSize, Stopwatch.StartNew(), basePathInput, basePathOutput);
                else
#endif
            if (_useFastBuffer && _useBufferedPatch && !_headerInfo.isSingleCompressedDiff)
                patchCore = new PatchCoreFastBuffer(_headerInfo.newDataSize, Stopwatch.StartNew(), _basePathInput, _basePathOutput, _token);
            else
                patchCore = new PatchCore(_headerInfo.newDataSize, Stopwatch.StartNew(), _basePathInput, _basePathOutput, _token);

            CompressionStreamHelper.GetDecompressStreamPlugin(_headerInfo.compMode, patchStream, out Stream decompressedHeadStream,
                _referenceInfo.headDataSize, _referenceInfo.headDataCompressedSize - headerPadding, out _, _useBufferedPatch);

            HDiffPatch.Event.PushLog("[PatchDir::Patch] Initializing stream to binary readers", Verbosity.Debug);

            using (patchStream)
            using (decompressedHeadStream)
            {
                DirectoryReferencePair dirData = InitializeDirPatcher(decompressedHeadStream);
                patchCore.SetDirectoryReferencePair(dirData);

                long newPatchSize = GetNewPatchedFileSize(dirData);
                long samePathSize = GetSameFileSize(dirData);
                long totalSizePatched = newPatchSize + samePathSize;
                patchCore.SetSizeToBePatched(totalSizePatched);

                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Total new size: {totalSizePatched} bytes ({newPatchSize} (new data) + {samePathSize} (same data))", Verbosity.Verbose);

                FileStream[] mergedOldStream = GetRefOldStreams(dirData);
                NewFileCombinedStream[] mergedNewStream = GetRefNewStreams(dirData);
                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Initialized {mergedOldStream.Length} old files and {mergedNewStream.Length} new files into combined stream", Verbosity.Verbose);

                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Seek the patch stream to: {_referenceInfo.hdiffDataOffset}. Jump to read header for clip streams!", Verbosity.Verbose);
                patchStream.Position = _referenceInfo.hdiffDataOffset;
                if (!_headerInfo.isSingleCompressedDiff)
                    _ = Header.TryParseHeaderInfo(patchStream, "", out _headerInfo, out _);
                else
                    HDiffPatch.Event.PushLog("[PatchDir::Patch] This patch is a \"single diff\" type!");

                _padding = _headerInfo.compMode == CompressionMode.zlib ? 1 : 0;

                using (Stream newStream = new CombinedStream(mergedNewStream))
                using (Stream oldStream = new CombinedStream(mergedOldStream))
                {
                    long oldFileSize = GetOldFileSize(dirData);
                    if (oldStream.Length != _headerInfo.oldDataSize)
                        throw new InvalidDataException($"[PatchDir::Patch] The patch directory is expecting old size to be equivalent as: {_headerInfo.oldDataSize} bytes, but the input file has unmatch size: {oldStream.Length} bytes!");

                    HDiffPatch.Event.PushLog($"[PatchDir::Patch] Existing old directory size: {oldFileSize} is matched!", Verbosity.Verbose);

                    long lastPos = patchStream.Position;
                    HDiffPatch.Event.PushLog($"[PatchDir::Patch] Staring patching routine at position: {lastPos}", Verbosity.Verbose);

                    HDiffPatch.DisplayDirPatchInformation(oldFileSize, totalSizePatched, _headerInfo);
                    StartPatchRoutine(oldStream, newStream, _headerInfo.newDataSize, lastPos, patchCore);
                }
            }
        }

        private long GetOldFileSize(DirectoryReferencePair dirData)
        {
            long fileSize = 0;
            for (int i = 0; i < dirData.OldUtf8PathList.Length; i++)
            {
                ref string basePath = ref dirData.OldUtf8PathList[i];
                if (basePath.Length == 0) continue;

                int baseIndexEoc = basePath.Length - 1;
                char endOfChar = basePath[baseIndexEoc];
                if (endOfChar == '/') continue;

                string sourceFullPath = Path.Combine(_basePathInput, basePath);
                if (!File.Exists(sourceFullPath)) continue;

                fileSize += new FileInfo(sourceFullPath).Length;
            }

            return fileSize;
        }

        private long GetSameFileSize(DirectoryReferencePair dirData)
        {
            long fileSize = 0;
            foreach (PairIndexReference pair in dirData.DataSamePairList)
            {
                ref string basePath = ref dirData.NewUtf8PathList[pair.NewIndex];
                bool isPathADir = PatchCore.IsPathADir(basePath);
                if (isPathADir) continue;

                string sourceFullPath = Path.Combine(_basePathInput, basePath);
                if (!File.Exists(sourceFullPath)) continue;

                fileSize += new FileInfo(sourceFullPath).Length;
            }

            return fileSize;
        }

        private static long GetNewPatchedFileSize(DirectoryReferencePair dirData) => dirData.NewRefSizeList.Sum();

        private void StartPatchRoutine(Stream inputStream, Stream outputStream, long newDataSize, long offset, IPatchCore patchCore)
        {
            Stream[] clips = new Stream[_headerInfo.isSingleCompressedDiff ? 1 : 4];
            Stream[] sourceClips = _headerInfo.isSingleCompressedDiff ?
                [ _spawnPatchStream() ] :
                [
                    _spawnPatchStream(),
                    _spawnPatchStream(),
                    _spawnPatchStream(),
                    _spawnPatchStream()
                ];

            try
            {
                if (_headerInfo.isSingleCompressedDiff)
                {
                    sourceClips[0].Position += _referenceInfo.hdiffDataOffset + _headerInfo.singleChunkInfo.diffDataPos;
                    int coverPadding = _headerInfo.singleChunkInfo.compressedSize > 0 ? _padding : 0;
                    offset += _headerInfo.singleChunkInfo.diffDataPos;

                    clips[0] = patchCore.GetBufferStreamFromOffset(_headerInfo.compMode, sourceClips[0], offset + coverPadding,
                        _headerInfo.singleChunkInfo.uncompressedSize, _headerInfo.singleChunkInfo.compressedSize, out long nextLength,
                        _useBufferedPatch, false);
                }
                else
                {
                    int coverPadding = _headerInfo.chunkInfo.compress_cover_buf_size > 0 ? _padding : 0;
                    clips[0] = patchCore.GetBufferStreamFromOffset(_headerInfo.compMode, sourceClips[0], offset + coverPadding,
                        _headerInfo.chunkInfo.cover_buf_size, _headerInfo.chunkInfo.compress_cover_buf_size, out long nextLength, _useBufferedPatch, false);

                    offset += nextLength;
                    int rleCtrlBufPadding = _headerInfo.chunkInfo.compress_rle_ctrlBuf_size > 0 ? _padding : 0;
                    clips[1] = patchCore.GetBufferStreamFromOffset(_headerInfo.compMode, sourceClips[1], offset + rleCtrlBufPadding,
                        _headerInfo.chunkInfo.rle_ctrlBuf_size, _headerInfo.chunkInfo.compress_rle_ctrlBuf_size, out nextLength, _useBufferedPatch, _useFastBuffer);

                    offset += nextLength;
                    int rleCodeBufPadding = _headerInfo.chunkInfo.compress_rle_codeBuf_size > 0 ? _padding : 0;
                    clips[2] = patchCore.GetBufferStreamFromOffset(_headerInfo.compMode, sourceClips[2], offset + rleCodeBufPadding,
                        _headerInfo.chunkInfo.rle_codeBuf_size, _headerInfo.chunkInfo.compress_rle_codeBuf_size, out nextLength, _useBufferedPatch, _useFastBuffer);

                    offset += nextLength;
                    int newDataDiffPadding = _headerInfo.chunkInfo.compress_newDataDiff_size > 0 ? _padding : 0;
                    clips[3] = patchCore.GetBufferStreamFromOffset(_headerInfo.compMode, sourceClips[3], offset + newDataDiffPadding,
                        _headerInfo.chunkInfo.newDataDiff_size, _headerInfo.chunkInfo.compress_newDataDiff_size - _padding, out _, _useBufferedPatch && _useFullBuffer, false);

                    _headerInfo.newDataSize = newDataSize;
                }
                patchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, _headerInfo);
            }
            finally
            {
                foreach (Stream clip in clips) clip?.Dispose();
                foreach (Stream clip in sourceClips) clip?.Dispose();
            }
        }

        private FileStream[] GetRefOldStreams(DirectoryReferencePair dirData)
        {
            FileStream[] streams = new FileStream[dirData.OldRefList.Length];
            for (int i = 0; i < dirData.OldRefList.Length; i++)
            {
                ref string oldPathByIndex = ref PatchCore.NewPathByIndex(dirData.OldUtf8PathList, dirData.OldRefList[i]);
                string combinedOldPath = Path.Combine(_basePathInput, oldPathByIndex);

                HDiffPatch.Event.PushLog($"[PatchDir::GetRefOldStreams] Assigning stream to the old path: {combinedOldPath}", Verbosity.Debug);
                streams[i] = File.Open(combinedOldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }

            return streams;
        }

        private NewFileCombinedStream[] GetRefNewStreams(DirectoryReferencePair dirData)
        {
            NewFileCombinedStream[] streams = new NewFileCombinedStream[dirData.NewRefList.Length];
            for (int i = 0; i < dirData.NewRefList.Length; i++)
            {
                ref string newPathByIndex = ref PatchCore.NewPathByIndex(dirData.NewUtf8PathList, dirData.NewRefList[i]);
                string combinedNewPath = Path.Combine(_basePathOutput, newPathByIndex);
                string newPathDirectory = Path.GetDirectoryName(combinedNewPath);

                if (!string.IsNullOrEmpty(newPathDirectory))
                    Directory.CreateDirectory(newPathDirectory);

                HDiffPatch.Event.PushLog($"[PatchDir::GetRefNewStreams] Assigning stream to the new path: {combinedNewPath}", Verbosity.Debug);

                NewFileCombinedStream @new = new NewFileCombinedStream
                {
                    Stream = new FileStream(combinedNewPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite),
                    Size = dirData.NewRefSizeList[i]
                };
                streams[i] = @new;
            }

            return streams;
        }

        private DirectoryReferencePair InitializeDirPatcher(Stream reader)
        {
            HDiffPatch.Event.PushLog("[PatchDir::InitializeDirPatcher] Reading PatchDir header...", Verbosity.Verbose);
            DirectoryReferencePair returnValue = new DirectoryReferencePair();

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Reading path string buffers -> OldPath: {(int)_referenceInfo.inputSumSize}", Verbosity.Verbose);
            reader.GetPathsFromStream(out returnValue.OldUtf8PathList, (int)_referenceInfo.inputSumSize, (int)_referenceInfo.inputDirCount);

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Reading path string buffers -> NewPath: {(int)_referenceInfo.outputSumSize}", Verbosity.Verbose);
            reader.GetPathsFromStream(out returnValue.NewUtf8PathList, (int)_referenceInfo.outputSumSize, (int)_referenceInfo.outputDirCount);

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Path string counts -> OldPath: {returnValue.OldUtf8PathList.Length} paths & NewPath: {returnValue.NewUtf8PathList.Length} paths", Verbosity.Verbose);
            reader.GetLongsFromStream(out returnValue.OldRefList, _referenceInfo.inputRefFileCount, _referenceInfo.inputDirCount);
            reader.GetLongsFromStream(out returnValue.NewRefList, _referenceInfo.outputRefFileCount, _referenceInfo.outputDirCount);
            reader.GetLongsFromStream(out returnValue.NewRefSizeList, _referenceInfo.outputRefFileCount);
            reader.GetPairIndexReferenceFromStream(out returnValue.DataSamePairList, _referenceInfo.sameFilePairCount, _referenceInfo.outputDirCount, _referenceInfo.inputDirCount);
            reader.GetLongsFromStream(out returnValue.NewExecuteList, _referenceInfo.newExecuteCount, _referenceInfo.outputDirCount);
            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Path refs found! OldRef: {_referenceInfo.inputRefFileCount} paths, NewRef: {_referenceInfo.outputRefFileCount} paths, IdenticalRef: {_referenceInfo.sameFilePairCount} paths", Verbosity.Verbose);

            return returnValue;
        }
    }
}
