// #if !(NETSTANDARD2_0 || NET461_OR_GREATER)
#if FALSE
using SharpCompress.Compressors.BZip2;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Threading;
using System.Threading.Tasks;
using static SharpHDiffPatch.Core.StreamExtension;

namespace SharpHDiffPatch.Core.Patch
{
    internal struct RLERefClipSingleStruct
    {
        public long len0;
        public long lenv;
        public bool isNeedDecode0;
    }

    internal sealed class PatchCoreSingleCompress : IPatchCore
    {
        internal const int _kSignTagBit = 1;
        internal const int _kByteRleType = 2;
        internal const int _maxMemBufferLen = 7 << 20;
        internal const int _maxMemBufferLimit = 10 << 20;
        internal const int _maxArrayCopyLen = 1 << 18;
        internal const int _maxArrayPoolLen = 4 << 20;
        internal const int _maxArrayPoolSecondOffset = _maxArrayPoolLen / 2;

        internal CancellationToken _token;
        internal long _sizeToBePatched;
        internal long _sizePatched;
        internal Stopwatch _stopwatch;
        internal string _pathInput;
        internal string _pathOutput;
        internal DirectoryReferencePair? _dirReferencePair;

        internal PatchCoreSingleCompress(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
        {
            _token = token;
            _sizeToBePatched = sizeToBePatched;
            _stopwatch = stopwatch;
            _sizePatched = 0;
            _pathInput = inputPath;
            _pathOutput = outputPath;
        }

        public void SetDirectoryReferencePair(DirectoryReferencePair pair) => _dirReferencePair = pair;
        public void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0)
        {
            _sizeToBePatched = sizeToBePatched;
            _sizePatched = sizeToPatch;
        }

        public Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed)
        {
            sourceStream.Position = start;

            GetDecompressStreamPlugin(compMode, sourceStream, out Stream returnStream, length, compLength, out outLength, isBuffered);

            if (isBuffered && !isFastBufferUsed)
            {
                HDiffPatch.Event.PushLog($"[PatchCore::GetBufferStreamFromOffset] Caching stream from offset: {start} with length: {(compLength > 0 ? compLength : length)}");
                using (returnStream)
                {
                    MemoryStream stream = CreateAndCopyToMemoryStream(returnStream);
                    stream.Position = 0;
                    return stream;
                }
            }

            return returnStream;
        }

        public void GetDecompressStreamPlugin(CompressionMode type, Stream sourceStream, out Stream decompStream,
            long length, long compLength, out long outLength, bool isBuffered)
        {
            long toPosition = sourceStream.Position;
            outLength = compLength > 0 ? compLength : length;
            long toCompLength = sourceStream.Position + outLength;

            HDiffPatch.Event.PushLog($"[PatchCore::GetDecompressStreamPlugin] Assigning stream of compression: {type} at start pos: {toPosition} to end pos: {toCompLength}", Verbosity.Verbose);
            Stream rawStream;
            if (isBuffered)
                rawStream = new ChunkStream(sourceStream, toPosition, toCompLength, false);
            else
            {
                sourceStream.Position = toPosition;
                rawStream = sourceStream;
            }

            if (type != CompressionMode.nocomp && compLength == 0)
            {
                decompStream = rawStream;
                return;
            }

            switch (type)
            {
                case CompressionMode.nocomp:
                    decompStream = rawStream; break;
                case CompressionMode.zstd:
                    decompStream = new DecompressionStream(rawStream, new DecompressionOptions(null, new Dictionary<ZSTD_dParameter, int>()
                    {
                        /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
                         * always use 31 as the size_t, which is 8 bytes length.
                         * 
                         * Code Snippets (decompress_plugin_demo.h:963):
                         *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
                         */
                        { ZSTD_dParameter.ZSTD_d_windowLogMax, 31 }
                    }), 0); break;
                case CompressionMode.zlib:
                    decompStream = new DeflateStream(rawStream, System.IO.Compression.CompressionMode.Decompress, true); break;
                case CompressionMode.bz2:
                    decompStream = new CBZip2InputStream(rawStream, false, true); break;
                case CompressionMode.pbz2:
                    decompStream = new CBZip2InputStream(rawStream, true, true); break;
                default:
                    throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: {type} is not supported");
            }
        }

        private MemoryStream CreateAndCopyToMemoryStream(Stream source)
        {
            MemoryStream returnStream = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolLen);

            try
            {
                int read;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                while ((read = source.Read(buffer)) > 0)
#else
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
#endif
                {
                    _token.ThrowIfCancellationRequested();
                    returnStream.Write(buffer, 0, read);
                }

                return returnStream;
            }
            catch { throw; }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo) => WriteCoverStreamToOutput(clips, inputStream, outputStream, headerInfo.chunkInfo.coverCount, headerInfo.chunkInfo.cover_buf_size, headerInfo.newDataSize);

        internal IEnumerable<CoverHeader> EnumerateCoverHeaders(Stream coverReader, long coverSize, long coverCount)
        {
            long _oldPosBack = 0,
                 _newPosBack = 0;

            if (coverSize < _maxMemBufferLen)
            {
                HDiffPatch.Event.PushLog($"[PatchCore::EnumerateCoverHeaders] Enumerate cover counts from buffer with size: {coverSize}", Verbosity.Verbose);
                byte[] buffer = new byte[coverSize];
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                coverReader.ReadExactly(buffer);
#else
                coverReader.ReadExactly(buffer, 0, buffer.Length);
#endif

                int offset = 0;
                while (coverCount-- > 0)
                {
                    long oldPosBack = _oldPosBack;
                    long newPosBack = _newPosBack;

                    byte pSign = buffer[offset++];
                    long oldPos, copyLength, coverLength;

                    byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
                    long inc_oldPos = ReadLong7bit(buffer, ref offset, _kSignTagBit, pSign);
                    oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

                    copyLength = ReadLong7bit(buffer, ref offset);
                    coverLength = ReadLong7bit(buffer, ref offset);
                    newPosBack += copyLength;
                    oldPosBack = oldPos;

                    oldPosBack += true ? coverLength : 0;

                    yield return new CoverHeader
                    {
                        oldPos = oldPos,
                        newPos = newPosBack,
                        coverLength = coverLength,
                        nextCoverIndex = coverCount
                    };
                    newPosBack += coverLength;

                    _oldPosBack = oldPosBack;
                    _newPosBack = newPosBack;
                }
            }
            else
            {
                HDiffPatch.Event.PushLog($"[PatchCore::EnumerateCoverHeaders] Enumerate cover counts directly from stream with size: {coverSize}", Verbosity.Verbose);
                while (coverCount-- > 0)
                {
                    long oldPosBack = _oldPosBack;
                    long newPosBack = _newPosBack;

                    byte pSign = (byte)coverReader.ReadByte();
                    long oldPos, copyLength, coverLength;

                    byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
                    long inc_oldPos = ReadLong7bit(coverReader, _kSignTagBit, pSign);
                    oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

                    copyLength = ReadLong7bit(coverReader);
                    coverLength = ReadLong7bit(coverReader);
                    newPosBack += copyLength;
                    oldPosBack = oldPos;

                    oldPosBack += true ? coverLength : 0;

                    yield return new CoverHeader
                    {
                        oldPos = oldPos,
                        newPos = newPosBack,
                        coverLength = coverLength,
                        nextCoverIndex = coverCount
                    };
                    newPosBack += coverLength;

                    _oldPosBack = oldPosBack;
                    _newPosBack = newPosBack;
                }
            }
        }

        internal void RunCopySimilarFilesRoutine()
        {
            if (_dirReferencePair.HasValue)
            {
                HDiffPatch.Event.PushLog("Start copying similar data");
                CopyOldSimilarToNewFiles(_dirReferencePair.Value);

                TimeSpan timeTaken = _stopwatch.Elapsed;
                HDiffPatch.Event.PushLog($"Copying similar data has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
                HDiffPatch.Event.PushLog($"Starting patching process...");
                _stopwatch.Restart();
            }
        }

        private void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, long coverCount, long coverSize, long newDataSize)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolSecondOffset);
            try
            {
                RunCopySimilarFilesRoutine();
                ref Stream singleClip = ref clips[0];
                MemoryStream cacheOutputStream = new MemoryStream();

                do
                {
                    long oldStreamSize = inputStream.Length;

                    int bufCover_size = (int)ReadLong7bit(singleClip);
                    int bufRle_size = (int)ReadLong7bit(singleClip);

                    byte[] bufCover = ArrayPool<byte>.Shared.Rent(bufCover_size);
                    byte[] bufRle = ArrayPool<byte>.Shared.Rent(bufRle_size);

                    // singleClip.ReadAtLeast(bufCover, bufCover_size);
                    // singleClip.ReadAtLeast(bufRle, bufRle_size);

                    int bufCover_offset = 0;
                    long lastOldEnd = 0;
                    long lastNewEnd = 0;

                    while (bufCover_offset < bufCover_size)
                    {
                        CoverHeader cover = TryGetNextCover(ref bufCover_offset, bufCover_size, ref lastOldEnd, ref lastNewEnd, bufCover);
                        if (cover.newPos > lastNewEnd)
                        {
                            // TODO
                        }

                        --coverCount;
                        if (cover.coverLength > 0)
                        {
                            if (cover.oldPos > oldStreamSize)
                            {
                                // TODO
                            }

                        }
                    }
                }
                while (coverCount > 0);
            }
            catch { throw; }
            finally
            {
                _stopwatch?.Stop();
                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
                ArrayPool<byte>.Shared.Return(sharedBuffer);
            }
        }

        internal CoverHeader TryGetNextCover(ref int bufCover_offset, long bufCover_Size, ref long lastOldEnd, ref long lastNewEnd, ReadOnlySpan<byte> buffer)
        {
            byte inc_oldPos_sign = (byte)(buffer[bufCover_offset++] >> (8 - 1));
            long oldPos = ReadLong7bit(buffer, ref bufCover_offset, 1, inc_oldPos_sign),
                 newPos = 0,
                 length = 0;

            if (inc_oldPos_sign == 0)
                oldPos = lastOldEnd;
            else
                oldPos = lastOldEnd - oldPos;

            newPos = ReadLong7bit(buffer, ref bufCover_offset);
            newPos += lastNewEnd;
            length = ReadLong7bit(buffer, ref bufCover_offset);

            return new CoverHeader
            {
                oldPos = oldPos,
                newPos = newPos,
                coverLength = length
            };
        }

        internal void WriteInMemoryOutputToStream(MemoryStream cacheOutputStream, Stream outputStream, CoverHeader cover)
        {
            long oldPos = outputStream.Position;

            cacheOutputStream.Position = 0;
            cacheOutputStream.CopyTo(outputStream);
            cacheOutputStream.SetLength(0);

            long newPos = outputStream.Position;
            long read = newPos - oldPos;

            HDiffPatch.UpdateEvent(read, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
        }

        internal void SpawnCorePatchFinishedMsg()
        {
            TimeSpan timeTaken = _stopwatch.Elapsed;
            HDiffPatch.Event.PushLog($"Patching has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
        }

        private void TBytesCopyOldClipPatch(Stream outCache, Stream inputStream, ref RLERefClipStruct rleLoader, long oldPos, long addLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            long lastPos = outCache.Position;
            long decodeStep = addLength;
            inputStream.Position = oldPos;

            TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)decodeStep);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, decodeStep, sharedBuffer, rleCtrlStream, rleCodeStream);
        }

        internal void TBytesCopyStreamFromOldClip(Stream outCache, Stream copyReader, long copyLength, byte[] sharedBuffer)
        {
            long lastPos = outCache.Position;
            TBytesCopyStreamInner(copyReader, outCache, sharedBuffer, (int)copyLength);
            outCache.Position = lastPos;
        }

        internal void TBytesCopyStreamInner(Stream input, Stream output, byte[] sharedBuffer, int readLen)
        {
            if (sharedBuffer.Length >= readLen)
            {
                input.ReadExactly(sharedBuffer, 0, readLen);
                output.Write(sharedBuffer, 0, readLen);
                return;
            }

            while (readLen > 0)
            {
                int toRead = Math.Min(sharedBuffer.Length, readLen);
                input.ReadExactly(sharedBuffer, 0, toRead);
                output.Write(sharedBuffer, 0, toRead);
                readLen -= toRead;
            }
        }

        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);

            while (copyLength > 0)
            {
                byte pSign = (byte)rleCtrlStream.ReadByte();
                byte type = (byte)((pSign) >> (8 - _kByteRleType));
                long length = ReadLong7bit(rleCtrlStream, _kByteRleType, pSign);
                ++length;

                if (type == 3)
                {
                    rleLoader.memCopyLength = length;
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
                    continue;
                }

                rleLoader.memSetLength = length;
                if (type == 2)
                {
                    rleLoader.memSetValue = (byte)rleCodeStream.ReadByte();
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
                    continue;
                }

                /* If the type is 1, then 0 - 1. This should result -1 in int but since
                 * we cast it to byte, then it will underflow and set it to 255.
                 * This method is the same as:
                 * if (type == 0)
                 *     rleLoader.memSetValue = 0x00; // or 0 in byte
                 * else
                 *     rleLoader.memSetValue = 0xFF; // or 255 in byte
                 */
                rleLoader.memSetValue = (byte)(0x00 - type);
                TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
            }
        }

        private unsafe void TBytesSetRle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer, Stream rleCodeStream)
        {
            TBytesSetRleSingle(ref rleLoader, outCache, ref copyLength, sharedBuffer);

            if (rleLoader.memCopyLength == 0) return;
            int decodeStep = (int)(rleLoader.memCopyLength > copyLength ? copyLength : rleLoader.memCopyLength);

            long lastPosCopy = outCache.Position;
            rleCodeStream.ReadExactly(sharedBuffer, 0, decodeStep);
            outCache.ReadExactly(sharedBuffer, _maxArrayPoolSecondOffset, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = sharedBuffer)
            fixed (byte* oldPtr = &sharedBuffer[_maxArrayPoolSecondOffset])
            {
                TBytesSetRleVector(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, sharedBuffer, 0, oldPtr);
            }
        }

        internal void TBytesSetRleSingle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer)
        {
            if (rleLoader.memSetLength != 0)
            {
                long memSetStep = rleLoader.memSetLength <= copyLength ? rleLoader.memSetLength : copyLength;
                if (rleLoader.memSetValue != 0)
                {
                    int length = (int)memSetStep;
                    long lastPos = outCache.Position;
                    outCache.Read(sharedBuffer, 0, length);
                    outCache.Position = lastPos;

                    while (length-- > 0) sharedBuffer[length] += rleLoader.memSetValue;

                    outCache.Write(sharedBuffer, 0, (int)memSetStep);
                }
                else
                {
                    outCache.Position += memSetStep;
                }

                copyLength -= memSetStep;
                rleLoader.memSetLength -= memSetStep;
            }
        }

        internal unsafe void TBytesSetRleVector(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int offset = 0;
            if (Sse2.IsSupported && decodeStep > Vector128<byte>.Count)
            {
                long offsetRemained = decodeStep % Vector128<byte>.Count;
                do
                {
                    Vector128<byte>* rleVector = (Vector128<byte>*)(rlePtr + offset);
                    Vector128<byte>* oldVector = (Vector128<byte>*)(oldPtr + offset);
                    Vector128<byte> resultVector = Sse2.Add(*rleVector, *oldVector);

                    Sse2.Store(rlePtr + offset, resultVector);
                    offset += Vector128<byte>.Count;
                } while (offset < decodeStep - offsetRemained);
            }
            while (offset < decodeStep) *(rlePtr + offset) += *(oldPtr + offset++);

            outCache.Write(rleBuffer, rleBufferIdx, decodeStep);

            rleLoader.memCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }

        internal static bool IsPathADir(ReadOnlySpan<char> input) => input[input.Length - 1] == '/';

        internal static ref string NewPathByIndex(string[] source, long index) => ref source[index];

        private void CopyOldSimilarToNewFiles(DirectoryReferencePair dirData)
        {
            int _curNewRefIndex = 0;
            int _curPathIndex = 0;
            int _curSamePairIndex = 0;
            int _newRefCount = dirData.newRefList.Length;
            int _samePairCount = dirData.dataSamePairList.Length;
            int _pathCount = dirData.newUtf8PathList.Length;

            try
            {
                Parallel.ForEach(dirData.dataSamePairList, new ParallelOptions { CancellationToken = _token }, (pair) =>
                {
                    bool isPathADir = IsPathADir(dirData.newUtf8PathList[pair.newIndex]);
                    if (isPathADir) return;

                    CopyFileByPairIndex(dirData.oldUtf8PathList, dirData.newUtf8PathList, pair.oldIndex, pair.newIndex);
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
                    string combinedNewPath = Path.Combine(_pathOutput, pathByIndex.ToString());
                    bool isPathADir = false;

                    if (pathByIndex.Length > 0)
                    {
                        isPathADir = IsPathADir(pathByIndex);

                        if (isPathADir && !Directory.Exists(combinedNewPath)) Directory.CreateDirectory(combinedNewPath);
                        else if (!isPathADir && !File.Exists(combinedNewPath)) File.Create(combinedNewPath).Dispose();
                    }

                    HDiffPatch.Event.PushLog($"[PatchCore::CopyOldSimilarToNewFiles] Created a new {(isPathADir ? "directory" : "empty file")}: {combinedNewPath}", Verbosity.Debug);

                    ++_curPathIndex;
                }
            }
        }

        private void CopyFileByPairIndex(string[] oldList, string[] newList, long oldIndex, long newIndex)
        {
            ref string oldPath = ref oldList[oldIndex];
            ref string newPath = ref newList[newIndex];
            string oldFullPath = Path.Combine(this._pathInput, oldPath);
            string newFullPath = Path.Combine(this._pathOutput, newPath);
            string newDirFullPath = Path.GetDirectoryName(newFullPath);
            if (!Directory.Exists(newDirFullPath)) Directory.CreateDirectory(newDirFullPath);

            HDiffPatch.Event.PushLog($"[PatchCore::CopyFileByPairIndex] Copying similar file to target path: {oldFullPath} -> {newFullPath}", Verbosity.Debug);
            CopyFile(oldFullPath, newFullPath);
        }

        private void CopyFile(string inputPath, string outputPath)
        {
            byte[] buffer = new byte[_maxArrayCopyLen];
            try
            {
                using (FileStream ifs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream ofs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Write))
                {
                    int read;
                    while ((read = ifs.Read(buffer)) > 0)
                    {
                        _token.ThrowIfCancellationRequested();
                        ofs.Write(buffer, 0, read);
                        HDiffPatch.UpdateEvent(read, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
                    }
                }
            }
            catch { throw; }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
*/
#endif