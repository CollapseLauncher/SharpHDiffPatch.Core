using Hi3Helper.EncTool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
        private Func<Stream> spawnPatchStream;
        private string basePathInput;
        private string basePathOutput;

        public PatchDir(TDirDiffInfo dirDiffInfo, HDiffHeaderInfo hdiffHeaderInfo, string patchPath)
        {
            this.dirDiffInfo = dirDiffInfo;
            this.hdiffHeaderInfo = hdiffHeaderInfo;
            this.spawnPatchStream = new Func<Stream>(() => new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch)
        {
            basePathInput = input;
            basePathOutput = output;

            using (Stream patchStream = spawnPatchStream())
            using (BinaryReader patchReader = new BinaryReader(patchStream))
            {
                TDirPatcher dirData = InitializeDirPatcher(patchReader, (long)hdiffHeaderInfo.headDataOffset);
                // bool IsChecksumPassed = CheckDiffDataIntegration(patchReader, dirDiffInfo.checksumOffset);

                // if (!IsChecksumPassed) throw new InvalidDataException("Checksum has failed and the patch file might be corrupted!");

                patchStream.Position = (long)hdiffHeaderInfo.hdiffDataOffset;
                CopyOldSimilarToNewFiles(dirData);

                FileStream[] mergedOldStream = GetRefOldStreams(dirData).ToArray();
                NewFileCombinedStreamStruct[] mergedNewStream = GetRefNewStreams(dirData).ToArray();

                using (Stream newStream = new CombinedStream(mergedNewStream))
                using (Stream oldStream = new CombinedStream(mergedOldStream))
                {
                    dirDiffInfo.sdiffInfo = new SingleCompressedHDiffInfo();
                    _ = Header.TryParseHeaderInfo(patchReader, "", dirDiffInfo, new CompressedHDiffInfo() { headInfo = new THDiffzHead() }, new HDiffHeaderInfo());
                    StartPatchRoutine(oldStream, patchStream, newStream, dirDiffInfo.newDataSize);
                }
            }
        }

        private void StartPatchRoutine(Stream inputStream, Stream patchStream, Stream outputStream, ulong newDataSize)
        {
            patchStream.Seek((long)dirDiffInfo.hdiffinfo.headInfo.headEndPos, SeekOrigin.Begin);

            Console.WriteLine("Patch Information:");
            Console.WriteLine($"    Old Size: {dirDiffInfo.oldDataSize} bytes");
            Console.WriteLine($"    New Size: {dirDiffInfo.newDataSize} bytes");

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

            Stopwatch stopwatch = Stopwatch.StartNew();
            ChunkStream[] clips = new ChunkStream[4];
            clips[0] = PatchCore.GetBufferClipsStream(patchStream, (long)dirDiffInfo.hdiffinfo.headInfo.cover_buf_size);
            clips[1] = PatchCore.GetBufferClipsStream(patchStream, (long)dirDiffInfo.hdiffinfo.headInfo.rle_ctrlBuf_size);
            clips[2] = PatchCore.GetBufferClipsStream(patchStream, (long)dirDiffInfo.hdiffinfo.headInfo.rle_codeBuf_size);
            clips[3] = PatchCore.GetBufferClipsStream(patchStream, (long)dirDiffInfo.hdiffinfo.headInfo.newDataDiff_size);

            PatchCore.UncoverBufferClipsStream(clips, inputStream, outputStream, dirDiffInfo.hdiffinfo, newDataSize);
            stopwatch.Stop();

            TimeSpan timeTaken = stopwatch.Elapsed;
            Console.WriteLine($"Patch has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.Milliseconds} ms)");
        }

        private IEnumerable<FileStream> GetRefOldStreams(TDirPatcher dirData)
        {
            for (int i = 0; i < dirData.oldRefList.Length; i++)
            {
                ReadOnlySpan<char> oldPathByIndex = NewPathByIndex(dirData.oldUtf8PathList, (int)dirData.oldRefList[i], true);
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
                ReadOnlySpan<char> newPathByIndex = NewPathByIndex(dirData.newUtf8PathList, (int)dirData.newRefList[i], true);
                string combinedNewPath = Path.Combine(basePathOutput, newPathByIndex.ToString());
                string newPathDirectory = Path.GetDirectoryName(combinedNewPath);
                if (!Directory.Exists(newPathDirectory)) Directory.CreateDirectory(newPathDirectory);

#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetRefNewStreams] Assigning stream to the new path: {combinedNewPath}");
#endif

                NewFileCombinedStreamStruct newStruct = new NewFileCombinedStreamStruct
                {
                    stream = File.Create(combinedNewPath),
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
            int _pathCount = dirData.newUtf8PathList.Count + 1;

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
                    var pair = dirData.dataSamePairList[_curSamePairIndex];
                    CopyFileByPairIndex(dirData.oldUtf8PathList, dirData.newUtf8PathList, (int)pair.oldIndex - 1, (int)pair.newIndex - 1);
                    ++_curSamePairIndex;
                    ++_curPathIndex;
                }
                else
                {
                    ReadOnlySpan<char> pathByIndex = NewPathByIndex(dirData.newUtf8PathList, _curPathIndex, true);
                    string combinedNewPath = Path.Combine(basePathOutput, pathByIndex.ToString());

                    bool isPathADir = IsPathADir(pathByIndex);

                    if (isPathADir && !Directory.Exists(combinedNewPath)) Directory.CreateDirectory(combinedNewPath);
                    else if (!isPathADir && !File.Exists(combinedNewPath)) File.Create(combinedNewPath).Dispose();

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

        private ReadOnlySpan<char> NewPathByIndex(IList<string> source, int index, bool returnBasePathIf0 = false) => returnBasePathIf0 && index == 0 ? basePathOutput + '/' : source[returnBasePathIf0 ? index - 1 : index];

        private void CopyFileByPairIndex(IList<string> oldList, IList<string> newList, int oldIndex, int newIndex)
        {
            string oldPath = oldList[oldIndex];
            string newPath = newList[newIndex];
            string oldFullPath = Path.Combine(this.basePathInput, oldPath);
            string newFullPath = Path.Combine(this.basePathOutput, newPath);
            string newDirFullPath = Path.GetDirectoryName(newFullPath);

            if (!Directory.Exists(newDirFullPath)) Directory.CreateDirectory(newDirFullPath);

            File.Copy(oldFullPath, newFullPath, true);

#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"[CopyFileByPairIndex] Copied a similar file to target path: {oldFullPath} -> {newFullPath}");
#endif
        }

        /*
        private bool CheckDiffDataIntegration(BinaryReader reader, long checksumDataOffset)
        {
            reader.BaseStream.Position = checksumDataOffset;
            (_, _) = hdiffHeaderInfo.checksumMode switch
            {
                ChecksumMode.fadler64 => (8, 4),
                ChecksumMode.crc32 => (4, 4),
                ChecksumMode.nochecksum => (0, 0),
                _ => throw new NotSupportedException($"Checksum mode {hdiffHeaderInfo.checksumMode} is currently not supported!")
            };

            if (dirDiffInfo.checksumByteSize == 0) return true;

            return true;
        }
        */

        private TDirPatcher InitializeDirPatcher(BinaryReader reader, long startOffset)
        {
            TDirPatcher returnValue = new TDirPatcher();
            reader.BaseStream.Position = startOffset;

            GetListOfPaths(reader, out returnValue.oldUtf8PathList, hdiffHeaderInfo.inputDirCount);
            GetListOfPaths(reader, out returnValue.newUtf8PathList, hdiffHeaderInfo.outputDirCount);

            GetArrayOfIncULongTag(reader, out returnValue.oldRefList, hdiffHeaderInfo.inputRefFileCount, hdiffHeaderInfo.inputDirCount, 0);
            GetArrayOfIncULongTag(reader, out returnValue.newRefList, hdiffHeaderInfo.outputRefFileCount, hdiffHeaderInfo.outputDirCount, 0);
            GetArrayOfULongTag(reader, out returnValue.newRefSizeList, hdiffHeaderInfo.outputRefFileCount, 0);
            GetArrayOfSamePairULongTag(reader, out returnValue.dataSamePairList, hdiffHeaderInfo.sameFilePairCount, hdiffHeaderInfo.outputDirCount, hdiffHeaderInfo.inputDirCount);
            GetArrayOfIncULongTag(reader, out returnValue.newExecuteList, (ulong)hdiffHeaderInfo.newExecuteCount, hdiffHeaderInfo.outputDirCount, 0);

            return returnValue;
        }

        private void GetListOfPaths(BinaryReader reader, out List<string> outlist, ulong count)
        {
            ++reader.BaseStream.Position;
            outlist = new List<string>();

            for (ulong i = 0; i < count - 1; i++)
            {
                string filePath = reader.ReadStringToNull();
                outlist.Add(filePath);
            }
        }

        private void GetArrayOfIncULongTag(BinaryReader reader, out ulong[] outarray, ulong count, ulong checkCount, int tagBit)
        {
            outarray = new ulong[count];
            ulong backValue = ulong.MaxValue;

            for (ulong i = 0; i < count; i++)
            {
                ulong num = reader.ReadUInt64VarInt(tagBit);
                backValue += 1 + num;
                if (backValue > checkCount) throw new InvalidDataException($"[GetArrayOfIncULongTag] Given back value for the reference list is invalid! Having {i} refs while expecting max: {checkCount}");
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"[GetArrayOfIncULongTag] value {i} - {count}: {backValue}");
#endif
                outarray[i] = backValue;
            }
        }

        private void GetArrayOfULongTag(BinaryReader reader, out ulong[] outarray, ulong count, int tagBit)
        {
            outarray = new ulong[count];
            for (ulong i = 0; i < count; i++)
            {
                ulong num = reader.ReadUInt64VarInt(tagBit);
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
            ulong pSign;

            for (ulong i = 0; i < pairCount; ++i)
            {
                ulong incNewValue = reader.ReadUInt64VarInt(0);

                backNewValue += 1 + incNewValue;
                if (backNewValue > check_endNewValue) throw new InvalidDataException($"[GetArrayOfSamePairULongTag] Given back new value for the list is invalid! Having {backNewValue} value while expecting max: {check_endNewValue}");

                pSign = reader.ReadByte();
                --reader.BaseStream.Position;
                ulong incOldValue = reader.ReadUInt64VarInt(1);

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
