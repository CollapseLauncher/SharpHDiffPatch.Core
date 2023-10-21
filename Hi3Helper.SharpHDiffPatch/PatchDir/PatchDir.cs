using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Hi3Helper.SharpHDiffPatch
{
    internal struct pairStruct
    {
        public long oldIndex;
        public long newIndex;
    }

    internal struct TDirPatcher
    {
        internal string[] oldUtf8PathList;
        internal string[] newUtf8PathList;
        internal long[] oldRefList;
        internal long[] newRefList;
        internal long[] newRefSizeList;
        internal pairStruct[] dataSamePairList;
        internal long[] newExecuteList;
    }

    public sealed class PatchDir : IPatch
    {
        private DirectoryHDiffInfo dirDiffInfo;
        private HDiffHeaderInfo hdiffHeaderInfo;
        private Func<FileStream> spawnPatchStream;
        private string basePathInput;
        private string basePathOutput;
        private bool useBufferedPatch;
        private bool useFullBuffer;
        private bool useFastBuffer;
        private int padding;
        private PatchCore patchCore;
        private CancellationToken token;

        public PatchDir(DirectoryHDiffInfo dirDiffInfo, HDiffHeaderInfo hdiffHeaderInfo, string patchPath, CancellationToken token)
        {
            this.token = token;
            this.dirDiffInfo = dirDiffInfo;
            this.hdiffHeaderInfo = hdiffHeaderInfo;
            this.spawnPatchStream = new Func<FileStream>(() => new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer)
        {
            basePathInput = input;
            basePathOutput = output;
            this.useBufferedPatch = useBufferedPatch;
            this.useFullBuffer = useFullBuffer;
            this.useFastBuffer = useFastBuffer;

            using (FileStream patchStream = spawnPatchStream())
            {
                padding = hdiffHeaderInfo.compMode == CompressionMode.zlib ? 1 : 0;
                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Padding applied: {padding} byte(s)", Verbosity.Debug);

                int headerPadding = hdiffHeaderInfo.headDataCompressedSize > 0 ? padding : 0;
                patchStream.Position = hdiffHeaderInfo.headDataOffset + headerPadding;
                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Patch stream seeked from: {patchStream.Position - (hdiffHeaderInfo.headDataOffset + headerPadding)} to: {patchStream.Position}", Verbosity.Debug);

                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Getting stream for header at size: {hdiffHeaderInfo.headDataSize} bytes ({hdiffHeaderInfo.headDataCompressedSize - headerPadding} bytes compressed)", Verbosity.Verbose);

                patchCore = this.useFastBuffer ? new PatchCoreFastBuffer(token, dirDiffInfo.newDataSize, Stopwatch.StartNew(), basePathInput, basePathOutput) :
                                                 new PatchCore(token, dirDiffInfo.newDataSize, Stopwatch.StartNew(), basePathInput, basePathOutput);
                patchCore.GetDecompressStreamPlugin(hdiffHeaderInfo.compMode, patchStream, out Stream decompHeadStream,
                    hdiffHeaderInfo.headDataSize, hdiffHeaderInfo.headDataCompressedSize - headerPadding, out _, this.useBufferedPatch);

                HDiffPatch.Event.PushLog($"[PatchDir::Patch] Initializing stream to binary readers", Verbosity.Debug);

                using (patchStream)
                using (decompHeadStream)
                {
                    TDirPatcher dirData = InitializeDirPatcher(decompHeadStream);
                    patchCore.SetTDirPatcher(dirData);

                    long newPatchSize = GetNewPatchedFileSize(dirData);
                    long samePathSize = GetSameFileSize(dirData);
                    long totalSizePatched = newPatchSize + samePathSize;
                    patchCore.SetSizeToBePatched(totalSizePatched, 0);

                    HDiffPatch.Event.PushLog($"[PatchDir::Patch] Total new size: {totalSizePatched} bytes ({newPatchSize} (new data) + {samePathSize} (same data))", Verbosity.Verbose);

                    FileStream[] mergedOldStream = GetRefOldStreams(dirData);
                    NewFileCombinedStreamStruct[] mergedNewStream = GetRefNewStreams(dirData);
                    HDiffPatch.Event.PushLog($"[PatchDir::Patch] Initialized {mergedOldStream.Length} old files and {mergedNewStream.Length} new files into combined stream", Verbosity.Verbose);

                    HDiffPatch.Event.PushLog($"[PatchDir::Patch] Seek the patch stream to: {hdiffHeaderInfo.hdiffDataOffset}. Jump to read header for clip streams!", Verbosity.Verbose);
                    patchStream.Position = hdiffHeaderInfo.hdiffDataOffset;
                    _ = Header.TryParseHeaderInfo(patchStream, "", out _, out dirDiffInfo.hdiffinfo, out _);
                    padding = hdiffHeaderInfo.compMode == CompressionMode.zlib ? 1 : 0;

                    using (Stream newStream = new CombinedStream(mergedNewStream))
                    using (Stream oldStream = new CombinedStream(mergedOldStream))
                    {
                        long oldFileSize = GetOldFileSize(dirData);
                        if (oldStream.Length != dirDiffInfo.oldDataSize)
                            throw new InvalidDataException($"[PatchDir::Patch] The patch directory is expecting old size to be equivalent as: {dirDiffInfo.oldDataSize} bytes, but the input file has unmatch size: {oldStream.Length} bytes!");

                        HDiffPatch.Event.PushLog($"[PatchDir::Patch] Existing old directory size: {oldFileSize} is matched!", Verbosity.Verbose);

                        long lastPos = patchStream.Position;
                        HDiffPatch.Event.PushLog($"[PatchDir::Patch] Staring patching routine at position: {lastPos}", Verbosity.Verbose);

                        HDiffPatch.DisplayDirPatchInformation(oldFileSize, totalSizePatched, dirDiffInfo.hdiffinfo.headInfo);
                        StartPatchRoutine(oldStream, newStream, dirDiffInfo.hdiffinfo.newDataSize, lastPos);
                    }
                }
            }
        }

        private long GetOldFileSize(TDirPatcher dirData)
        {
            long fileSize = 0;
            for (int i = 0; i < dirData.oldUtf8PathList.Length; i++)
            {
                ref string basePath = ref dirData.oldUtf8PathList[i];
                if (basePath.Length == 0) continue;

                char endOfChar = basePath[basePath.Length - 1];
                if (endOfChar == '/') continue;

                string sourceFullPath = Path.Combine(basePathInput, basePath);
                if (!File.Exists(sourceFullPath)) continue;

                fileSize += new FileInfo(sourceFullPath).Length;
            }

            return fileSize;
        }

        private long GetSameFileSize(TDirPatcher dirData)
        {
            long fileSize = 0;
            foreach (pairStruct pair in dirData.dataSamePairList)
            {
                ref string basePath = ref dirData.newUtf8PathList[pair.newIndex];
                bool isPathADir = PatchCore.IsPathADir(basePath);
                if (isPathADir) continue;

                string sourceFullPath = Path.Combine(basePathInput, basePath);
                if (!File.Exists(sourceFullPath)) continue;

                fileSize += new FileInfo(sourceFullPath).Length;
            }

            return fileSize;
        }

        private long GetNewPatchedFileSize(TDirPatcher dirData) => dirData.newRefSizeList.Sum();

        private void StartPatchRoutine(Stream inputStream, Stream outputStream, long newDataSize, long offset)
        {
            bool isCompressed = dirDiffInfo.hdiffinfo.compMode != CompressionMode.nocomp;
            Stream[] clips = new Stream[4];
            Stream[] sourceClips = new Stream[4]
            {
                spawnPatchStream(),
                spawnPatchStream(),
                spawnPatchStream(),
                spawnPatchStream()
            };

            try
            {
                int coverPadding = dirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size > 0 ? padding : 0;
                clips[0] = patchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, sourceClips[0], offset + coverPadding,
                    dirDiffInfo.hdiffinfo.headInfo.cover_buf_size, dirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size, out long nextLength, this.useBufferedPatch);

                offset += nextLength;
                int rle_ctrlBufPadding = dirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size > 0 ? padding : 0;
                clips[1] = patchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, sourceClips[1], offset + rle_ctrlBufPadding,
                    dirDiffInfo.hdiffinfo.headInfo.rle_ctrlBuf_size, dirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size, out nextLength, this.useBufferedPatch);

                offset += nextLength;
                int rle_codeBufPadding = dirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size > 0 ? padding : 0;
                clips[2] = patchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, sourceClips[2], offset + rle_codeBufPadding,
                    dirDiffInfo.hdiffinfo.headInfo.rle_codeBuf_size, dirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size, out nextLength, this.useBufferedPatch);

                offset += nextLength;
                int newDataDiffPadding = dirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size > 0 ? padding : 0;
                clips[3] = patchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, sourceClips[3], offset + newDataDiffPadding,
                    dirDiffInfo.hdiffinfo.headInfo.newDataDiff_size, dirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size - padding, out _, this.useBufferedPatch && this.useFullBuffer);

                dirDiffInfo.hdiffinfo.newDataSize = newDataSize;
                patchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, dirDiffInfo.hdiffinfo);
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

        private FileStream[] GetRefOldStreams(TDirPatcher dirData)
        {
            FileStream[] streams = new FileStream[dirData.oldRefList.Length];
            for (int i = 0; i < dirData.oldRefList.Length; i++)
            {
                ref string oldPathByIndex = ref PatchCore.NewPathByIndex(dirData.oldUtf8PathList, dirData.oldRefList[i]);
                string combinedOldPath = Path.Combine(basePathInput, oldPathByIndex);

                HDiffPatch.Event.PushLog($"[PatchDir::GetRefOldStreams] Assigning stream to the old path: {combinedOldPath}", Verbosity.Debug);
                streams[i] = File.OpenRead(combinedOldPath);
            }

            return streams;
        }

        private NewFileCombinedStreamStruct[] GetRefNewStreams(TDirPatcher dirData)
        {
            NewFileCombinedStreamStruct[] streams = new NewFileCombinedStreamStruct[dirData.newRefList.Length];
            for (int i = 0; i < dirData.newRefList.Length; i++)
            {
                ref string newPathByIndex = ref PatchCore.NewPathByIndex(dirData.newUtf8PathList, dirData.newRefList[i]);
                string combinedNewPath = Path.Combine(basePathOutput, newPathByIndex);
                string newPathDirectory = Path.GetDirectoryName(combinedNewPath);
                if (!Directory.Exists(newPathDirectory)) Directory.CreateDirectory(newPathDirectory);

                HDiffPatch.Event.PushLog($"[PatchDir::GetRefNewStreams] Assigning stream to the new path: {combinedNewPath}", Verbosity.Debug);

                NewFileCombinedStreamStruct newStruct = new NewFileCombinedStreamStruct
                {
                    stream = new FileStream(combinedNewPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096),
                    size = dirData.newRefSizeList[i]
                };
                streams[i] = newStruct;
            }

            return streams;
        }

        private TDirPatcher InitializeDirPatcher(Stream reader)
        {
            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Reading PatchDir header...", Verbosity.Verbose);
            TDirPatcher returnValue = new TDirPatcher();

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Reading path string buffers -> OldPath: {(int)hdiffHeaderInfo.inputSumSize}", Verbosity.Verbose);
            byte[] pathListInput = new byte[(int)hdiffHeaderInfo.inputSumSize];
            reader.ReadExactly(pathListInput);
            GetListOfPaths(pathListInput, out returnValue.oldUtf8PathList, hdiffHeaderInfo.inputDirCount);

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Reading path string buffers -> NewPath: {(int)hdiffHeaderInfo.outputSumSize}", Verbosity.Verbose);
            byte[] pathListOutput = new byte[(int)hdiffHeaderInfo.outputSumSize];
            reader.ReadExactly(pathListOutput);
            GetListOfPaths(pathListOutput, out returnValue.newUtf8PathList, hdiffHeaderInfo.outputDirCount);

            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Path string counts -> OldPath: {returnValue.oldUtf8PathList.Length} paths & NewPath: {returnValue.newUtf8PathList.Length} paths", Verbosity.Verbose);
            GetArrayOfIncULongTag(reader, out returnValue.oldRefList, hdiffHeaderInfo.inputRefFileCount, hdiffHeaderInfo.inputDirCount);
            GetArrayOfIncULongTag(reader, out returnValue.newRefList, hdiffHeaderInfo.outputRefFileCount, hdiffHeaderInfo.outputDirCount);
            GetArrayOfULongTag(reader, out returnValue.newRefSizeList, hdiffHeaderInfo.outputRefFileCount);
            GetArrayOfSamePairULongTag(reader, out returnValue.dataSamePairList, hdiffHeaderInfo.sameFilePairCount, hdiffHeaderInfo.outputDirCount, hdiffHeaderInfo.inputDirCount);
            GetArrayOfIncULongTag(reader, out returnValue.newExecuteList, hdiffHeaderInfo.newExecuteCount, hdiffHeaderInfo.outputDirCount);
            HDiffPatch.Event.PushLog($"[PatchDir::InitializeDirPatcher] Path refs found! OldRef: {hdiffHeaderInfo.inputRefFileCount} paths, NewRef: {hdiffHeaderInfo.outputRefFileCount} paths, IdenticalRef: {hdiffHeaderInfo.sameFilePairCount} paths", Verbosity.Verbose);

            return returnValue;
        }

        private unsafe void GetListOfPaths(ReadOnlySpan<byte> input, out string[] outlist, long count)
        {
            outlist = new string[count];
            int inLen = input.Length;

            int idx = 0, strIdx = 0;
            fixed (byte* inputPtr = input)
            {
                sbyte* inputSignedPtr = (sbyte*)inputPtr;
                do
                {
                    ReadOnlySpan<byte> inputSpanned = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(inputPtr + idx);
                    idx += inputSpanned.Length + 1;
                    outlist[strIdx++] = Encoding.UTF8.GetString(inputSpanned);
                } while (idx < inLen);
            }
        }

        private void GetArrayOfIncULongTag(Stream reader, out long[] outarray, long count, long checkCount)
        {
            outarray = new long[count];
            long backValue = -1;

            for (long i = 0; i < count; i++)
            {
                long num = reader.ReadLong7bit();
                backValue += 1 + num;
                if (backValue > checkCount) throw new InvalidDataException($"[PatchDir::GetArrayOfIncULongTag] Given back value for the reference list is invalid! Having {i} refs while expecting max: {checkCount}");
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetArrayOfIncULongTag] value {i} - {count}: {backValue}", Verbosity.Debug);
#endif
                outarray[i] = backValue;
            }
        }

        private void GetArrayOfULongTag(Stream reader, out long[] outarray, long count)
        {
            outarray = new long[count];
            for (long i = 0; i < count; i++)
            {
                long num = reader.ReadLong7bit();
                outarray[i] = num;
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetArrayOfIncULongTag] value {i} - {count}: {num}", Verbosity.Debug);
#endif
            }
        }

        private void GetArrayOfSamePairULongTag(Stream reader, out pairStruct[] outPair, long pairCount, long check_endNewValue, long check_endOldValue)
        {
            outPair = new pairStruct[pairCount];
            long backNewValue = -1;
            long backOldValue = -1;

            for (long i = 0; i < pairCount; ++i)
            {
                long incNewValue = reader.ReadLong7bit();

                backNewValue += 1 + incNewValue;
                if (backNewValue > check_endNewValue) throw new InvalidDataException($"[PatchDir::GetArrayOfSamePairULongTag] Given back new value for the list is invalid! Having {backNewValue} value while expecting max: {check_endNewValue}");

                byte pSign = (byte)reader.ReadByte();
                long incOldValue = reader.ReadLong7bit(1, pSign);

                if (pSign >> (8 - 1) == 0)
                    backOldValue += 1 + incOldValue;
                else
                    backOldValue = backOldValue + 1 - incOldValue;

                if (backOldValue > check_endOldValue) throw new InvalidDataException($"[PatchDir::GetArrayOfSamePairULongTag] Given back old value for the list is invalid! Having {backOldValue} value while expecting max: {check_endOldValue}");
#if DEBUG && SHOWDEBUGINFO
                HDiffPatch.Event.PushLog($"[PatchDir::GetArrayOfSamePairULongTag] value {i} - {pairCount}: newIndex -> {backNewValue} oldIndex -> {backOldValue}", Verbosity.Debug);
#endif
                outPair[i] = new pairStruct { newIndex = backNewValue, oldIndex = backOldValue };
            }
        }
    }
}
