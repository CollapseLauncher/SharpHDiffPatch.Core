using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Hi3Helper.SharpHDiffPatch
{
    internal struct pairStruct
    {
        public ulong oldIndex;
        public ulong newIndex;
    }

    internal class TDirPatcher
    {
        internal List<string> oldUtf8PathList;
        internal List<string> newUtf8PathList;
        internal ulong[] oldRefList;
        internal ulong[] newRefList;
        internal ulong[] newRefSizeList;
        internal pairStruct[] dataSamePairList;
        internal ulong[] newExecuteList;
    }

    public sealed class PatchDir : IPatch
    {
        private TDirDiffInfo dirDiffInfo;
        private HDiffHeaderInfo hdiffHeaderInfo;
        private Func<FileStream> spawnPatchStream;
        private string basePathInput;
        private string basePathOutput;
        private bool useBufferedPatch;
        private int padding;

        public PatchDir(TDirDiffInfo dirDiffInfo, HDiffHeaderInfo hdiffHeaderInfo, string patchPath)
        {
            this.dirDiffInfo = dirDiffInfo;
            this.hdiffHeaderInfo = hdiffHeaderInfo;
            this.spawnPatchStream = new Func<FileStream>(() => new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch)
        {
            basePathInput = input;
            basePathOutput = output;
            this.useBufferedPatch = useBufferedPatch;

            FileStream patchStream = spawnPatchStream();
            patchStream.Position = (long)hdiffHeaderInfo.headDataOffset + padding;
            PatchCore.GetDecompressStreamPlugin(hdiffHeaderInfo.compMode, patchStream, out Stream decompHeadStream,
                (long)hdiffHeaderInfo.headDataSize, (long)hdiffHeaderInfo.headDataCompressedSize - padding, out _);

            using (BinaryReader patchReader = new BinaryReader(patchStream))
            using (BinaryReader patchDecompReader = new BinaryReader(decompHeadStream))
            using (patchDecompReader)
            {
                TDirPatcher dirData = InitializeDirPatcher(patchDecompReader);

                HDiffPatch.currentSizePatched = 0;
                HDiffPatch.totalSizePatched = GetSameFileSize(dirData) + GetNewPatchedFileSize(dirData);

                FileStream[] mergedOldStream = GetRefOldStreams(dirData).ToArray();
                NewFileCombinedStreamStruct[] mergedNewStream = GetRefNewStreams(dirData).ToArray();

                patchStream.Position = (long)hdiffHeaderInfo.hdiffDataOffset;
                _ = Header.TryParseHeaderInfo(patchReader, "", out _, out dirDiffInfo.hdiffinfo, out _);
                padding = hdiffHeaderInfo.compMode == CompressionMode.zlib ? 1 : 0;

                Stopwatch stopwatch = Stopwatch.StartNew();
                using (Stream newStream = new CombinedStream(mergedNewStream))
                using (Stream oldStream = new CombinedStream(mergedOldStream))
                {
                    long oldFileSize = GetOldFileSize(dirData);
                    TryCheckMatchOldSize(oldStream, oldFileSize);

                    long lastPos = patchStream.Position;
                    StartPatchRoutine(oldStream, newStream, dirDiffInfo.hdiffinfo.newDataSize, lastPos);
                }

                TimeSpan timeTaken = stopwatch.Elapsed;
                Console.WriteLine($"Patch has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");

                stopwatch.Restart();

                Console.Write("Start copying similar data... ");
                CopyOldSimilarToNewFiles(dirData);
                Console.WriteLine("Done!");

                timeTaken = stopwatch.Elapsed;
                Console.WriteLine($"Copying similar data has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
            }
        }

        private long GetOldFileSize(TDirPatcher dirData)
        {
            long fileSize = 0;
            for (int i = 0; i < dirData.oldUtf8PathList.Count; i++)
            {
                ReadOnlySpan<char> basePath = dirData.oldUtf8PathList[i];
                if (basePath.Length == 0) continue;
                bool isPathADir = IsPathADir(basePath);
                if (isPathADir) continue;

                string sourceFullPath = Path.Combine(basePathInput, basePath.ToString());
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
                ReadOnlySpan<char> basePath = dirData.newUtf8PathList[(int)pair.newIndex];
                bool isPathADir = IsPathADir(basePath);
                if (isPathADir) continue;

                string sourceFullPath = Path.Combine(basePathInput, basePath.ToString());
                if (!File.Exists(sourceFullPath)) continue;

                fileSize += new FileInfo(sourceFullPath).Length;
            }

            return fileSize;
        }

        private long GetNewPatchedFileSize(TDirPatcher dirData) => dirData.newRefSizeList.Sum(x => (long)x);

        private void StartPatchRoutine(Stream inputStream, Stream outputStream, ulong newDataSize, long offset)
        {
            bool isCompressed = dirDiffInfo.hdiffinfo.compMode != CompressionMode.nocomp;
            Stream[] clips = new Stream[4];

            clips[0] = PatchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, spawnPatchStream, offset,
                (long)dirDiffInfo.hdiffinfo.headInfo.cover_buf_size, (long)dirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size, out long nextLength, this.useBufferedPatch);

            offset += nextLength;
            clips[1] = PatchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, spawnPatchStream, offset,
                (long)dirDiffInfo.hdiffinfo.headInfo.rle_ctrlBuf_size, (long)dirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size, out nextLength, this.useBufferedPatch);

            offset += nextLength;
            clips[2] = PatchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, spawnPatchStream, offset,
                (long)dirDiffInfo.hdiffinfo.headInfo.rle_codeBuf_size, (long)dirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size, out nextLength, this.useBufferedPatch);

            offset += nextLength;
            clips[3] = PatchCore.GetBufferStreamFromOffset(dirDiffInfo.hdiffinfo.compMode, spawnPatchStream, offset,
                (long)dirDiffInfo.hdiffinfo.headInfo.newDataDiff_size, (long)dirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size, out _, false);

            PatchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, dirDiffInfo.hdiffinfo, newDataSize);
        }

        private void TryCheckMatchOldSize(Stream inputStream, long oldFileSize)
        {
            if (inputStream.Length != (long)dirDiffInfo.oldDataSize)
            {
                throw new InvalidDataException($"The patch file is expecting old size to be equivalent as: {dirDiffInfo.oldDataSize} bytes, but the input file has unmatch size: {inputStream.Length} bytes!");
            }

            Console.WriteLine("Patch Information:");
            Console.WriteLine($"    Old Size: {oldFileSize} bytes");
            Console.WriteLine($"    New Size: {HDiffPatch.totalSizePatched} bytes");

            Console.WriteLine();
            Console.WriteLine("Technical Information:");
            Console.WriteLine($"    Cover Data Offset: {dirDiffInfo.hdiffinfo.headInfo.headEndPos}");
            Console.WriteLine($"    Cover Data Size: {dirDiffInfo.hdiffinfo.headInfo.cover_buf_size}");
            Console.WriteLine($"    Cover Count: {dirDiffInfo.hdiffinfo.headInfo.coverCount}");

            Console.WriteLine($"    RLE Data Offset: {dirDiffInfo.hdiffinfo.headInfo.coverEndPos}");
            Console.WriteLine($"    RLE Control Data Size: {dirDiffInfo.hdiffinfo.headInfo.rle_ctrlBuf_size}");
            Console.WriteLine($"    RLE Code Data Size: {dirDiffInfo.hdiffinfo.headInfo.rle_codeBuf_size}");

            Console.WriteLine($"    New Diff Data Size: {dirDiffInfo.hdiffinfo.headInfo.newDataDiff_size}");
            Console.WriteLine();
        }

        private IEnumerable<FileStream> GetRefOldStreams(TDirPatcher dirData)
        {
            for (int i = 0; i < dirData.oldRefList.Length; i++)
            {
                ReadOnlySpan<char> oldPathByIndex = NewPathByIndex(dirData.oldUtf8PathList, (int)dirData.oldRefList[i]);
                string combinedOldPath = Path.Combine(basePathInput, oldPathByIndex.ToString());

#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetRefOldStreams] Assigning stream to the old path: {combinedOldPath}");
#endif
                yield return File.OpenRead(combinedOldPath);
            }
        }

        private IEnumerable<NewFileCombinedStreamStruct> GetRefNewStreams(TDirPatcher dirData)
        {
            for (int i = 0; i < dirData.newRefList.Length; i++)
            {
                ReadOnlySpan<char> newPathByIndex = NewPathByIndex(dirData.newUtf8PathList, (int)dirData.newRefList[i]);
                string combinedNewPath = Path.Combine(basePathOutput, newPathByIndex.ToString());
                string newPathDirectory = Path.GetDirectoryName(combinedNewPath);
                if (!Directory.Exists(newPathDirectory)) Directory.CreateDirectory(newPathDirectory);

#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetRefNewStreams] Assigning stream to the new path: {combinedNewPath}");
#endif

                NewFileCombinedStreamStruct newStruct = new NewFileCombinedStreamStruct
                {
                    stream = new FileStream(combinedNewPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096),
                    size = (long)dirData.newRefSizeList[i]
                };
                yield return newStruct;
            }
        }

        private void CopyOldSimilarToNewFiles(TDirPatcher dirData)
        {
            int _curNewRefIndex = 0;
            int _curPathIndex = 0;
            int _curSamePairIndex = 0;
            int _newRefCount = dirData.newRefList.Length;
            int _samePairCount = dirData.dataSamePairList.Length;
            int _pathCount = dirData.newUtf8PathList.Count;

            try
            {
                Parallel.ForEach(dirData.dataSamePairList, (pair) =>
                {
                    bool isPathADir = IsPathADir(dirData.newUtf8PathList[(int)pair.newIndex]);
                    if (isPathADir) return;

                    CopyFileByPairIndex(dirData.oldUtf8PathList, dirData.newUtf8PathList, (int)pair.oldIndex, (int)pair.newIndex);
                });
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions.First();
            }

            while (_curPathIndex < _pathCount)
            {
                if ((_curNewRefIndex < _newRefCount)
                    && (_curPathIndex == (dirData.newRefList.Length > 0 ? (int)dirData.newRefList[_curNewRefIndex] : _curNewRefIndex)))
                {
                    bool isPathADir = IsPathADir(dirData.newUtf8PathList[(int)dirData.newRefList[_curNewRefIndex]]);

                    if (isPathADir) ++_curPathIndex;
                    ++_curNewRefIndex;
                }
                else if (_curSamePairIndex < _samePairCount
                    && (_curPathIndex == (int)dirData.dataSamePairList[_curSamePairIndex].newIndex))
                {
                    ++_curSamePairIndex;
                    ++_curPathIndex;
                }
                else
                {
                    ReadOnlySpan<char> pathByIndex = NewPathByIndex(dirData.newUtf8PathList, _curPathIndex);
                    string combinedNewPath = Path.Combine(basePathOutput, pathByIndex.ToString());

                    if (pathByIndex.Length > 0)
                    {
                        bool isPathADir = IsPathADir(pathByIndex);

                        if (isPathADir && !Directory.Exists(combinedNewPath)) Directory.CreateDirectory(combinedNewPath);
                        else if (!isPathADir && !File.Exists(combinedNewPath)) File.Create(combinedNewPath).Dispose();
                    }

#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"[CopyOldSimilarToNewFiles] Created a new {(isPathADir ? "directory" : "empty file")}: {combinedNewPath}");
#endif

                    ++_curPathIndex;
                }
            }
        }

        private bool IsPathADir(ReadOnlySpan<char> input)
        {
            char endOfChar = input[input.Length - 1];
            return endOfChar == '/';
        }

        private ReadOnlySpan<char> NewPathByIndex(IList<string> source, int index) => source[index];

        private void CopyFileByPairIndex(IList<string> oldList, IList<string> newList, int oldIndex, int newIndex)
        {
            string oldPath = oldList[oldIndex];
            string newPath = newList[newIndex];
            string oldFullPath = Path.Combine(this.basePathInput, oldPath);
            string newFullPath = Path.Combine(this.basePathOutput, newPath);
            string newDirFullPath = Path.GetDirectoryName(newFullPath);
            if (!Directory.Exists(newDirFullPath)) Directory.CreateDirectory(newDirFullPath);

            CopyFile(oldFullPath, newFullPath);

#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"[CopyFileByPairIndex] Copied a similar file to target path: {oldFullPath} -> {newFullPath}");
#endif
        }

        private void CopyFile(string inputPath, string outputPath)
        {
            Span<byte> buffer = stackalloc byte[8 << 10];

            using (FileStream ifs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream ofs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Write))
            {
                int read;
                while ((read = ifs.Read(buffer)) > 0)
                {
                    HDiffPatch._token.ThrowIfCancellationRequested();
                    ofs.Write(buffer.Slice(0, read));
                    lock (HDiffPatch.PatchEvent)
                    {
                        HDiffPatch.UpdateEvent(read);
                    }
                }
            }
        }

        private TDirPatcher InitializeDirPatcher(BinaryReader reader)
        {
            TDirPatcher returnValue = new TDirPatcher();

            GetListOfPaths(reader, out returnValue.oldUtf8PathList, hdiffHeaderInfo.inputDirCount);
            GetListOfPaths(reader, out returnValue.newUtf8PathList, hdiffHeaderInfo.outputDirCount);

            GetArrayOfIncULongTag(reader, out returnValue.oldRefList, hdiffHeaderInfo.inputRefFileCount, hdiffHeaderInfo.inputDirCount);
            GetArrayOfIncULongTag(reader, out returnValue.newRefList, hdiffHeaderInfo.outputRefFileCount, hdiffHeaderInfo.outputDirCount);
            GetArrayOfULongTag(reader, out returnValue.newRefSizeList, hdiffHeaderInfo.outputRefFileCount);
            GetArrayOfSamePairULongTag(reader, out returnValue.dataSamePairList, hdiffHeaderInfo.sameFilePairCount, hdiffHeaderInfo.outputDirCount, hdiffHeaderInfo.inputDirCount);
            GetArrayOfIncULongTag(reader, out returnValue.newExecuteList, (ulong)hdiffHeaderInfo.newExecuteCount, hdiffHeaderInfo.outputDirCount);

            return returnValue;
        }

        private void GetListOfPaths(BinaryReader reader, out List<string> outlist, ulong count)
        {
            outlist = new List<string>();

            for (ulong i = 0; i < count; i++)
            {
                string filePath = reader.ReadStringToNull();
                outlist.Add(filePath);
            }
        }

        private void GetArrayOfIncULongTag(BinaryReader reader, out ulong[] outarray, ulong count, ulong checkCount)
        {
            outarray = new ulong[count];
            ulong backValue = ulong.MaxValue;

            for (ulong i = 0; i < count; i++)
            {
                ulong num = reader.ReadUInt64VarInt();
                backValue += 1 + num;
                if (backValue > checkCount) throw new InvalidDataException($"[GetArrayOfIncULongTag] Given back value for the reference list is invalid! Having {i} refs while expecting max: {checkCount}");
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetArrayOfIncULongTag] value {i} - {count}: {backValue}");
#endif
                outarray[i] = backValue;
            }
        }

        private void GetArrayOfULongTag(BinaryReader reader, out ulong[] outarray, ulong count)
        {
            outarray = new ulong[count];
            for (ulong i = 0; i < count; i++)
            {
                ulong num = reader.ReadUInt64VarInt();
                outarray[i] = num;
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetArrayOfIncULongTag] value {i} - {count}: {num}");
#endif
            }
        }

        private void GetArrayOfSamePairULongTag(BinaryReader reader, out pairStruct[] outPair, ulong pairCount, ulong check_endNewValue, ulong check_endOldValue)
        {
            outPair = new pairStruct[pairCount];
            ulong backNewValue = ulong.MaxValue;
            ulong backOldValue = ulong.MaxValue;

            for (ulong i = 0; i < pairCount; ++i)
            {
                ulong incNewValue = reader.ReadUInt64VarInt();

                backNewValue += 1 + incNewValue;
                if (backNewValue > check_endNewValue) throw new InvalidDataException($"[GetArrayOfSamePairULongTag] Given back new value for the list is invalid! Having {backNewValue} value while expecting max: {check_endNewValue}");

                byte pSign = reader.ReadByte();
                ulong incOldValue = reader.ReadUInt64VarInt(1, pSign);

                if (pSign >> (8 - 1) == 0)
                    backOldValue += 1 + incOldValue;
                else
                    backOldValue = backOldValue + 1 - incOldValue;

                if (backOldValue > check_endOldValue) throw new InvalidDataException($"[GetArrayOfSamePairULongTag] Given back old value for the list is invalid! Having {backOldValue} value while expecting max: {check_endOldValue}");
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetArrayOfSamePairULongTag] value {i} - {pairCount}: newIndex -> {backNewValue} oldIndex -> {backOldValue}");
#endif
                outPair[i] = new pairStruct { newIndex = backNewValue, oldIndex = backOldValue };
            }
        }
    }
}
