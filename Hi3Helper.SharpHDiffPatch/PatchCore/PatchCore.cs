using SharpCompress.Compressors.BZip2;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using ZstdNet;

namespace Hi3Helper.SharpHDiffPatch
{
    internal struct RLERefClipStruct
    {
        public long memCopyLength;
        public long memSetLength;
        public byte memSetValue;
    }

    internal struct CoverHeader
    {
        internal long oldPos;
        internal long newPos;
        internal long coverLength;
        internal int nextCoverIndex;

        internal RLERefClipStruct[] rleRefStruct;
    }

#if !NET7_0_OR_GREATER
    internal static class StreamExtension
    {
        public static int ReadExactly(this Stream stream, Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer.Slice(totalRead));
                if (read == 0)
                {
                    return totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }
    }
#endif

    internal class PatchCore
    {
        protected const int _kSignTagBit = 1;
        protected const int _kByteRleType = 2;
        protected const int _maxArrayCopyLen = 4 << 20;
        protected static int _maxArrayPoolLen = (4 << 20) - Environment.ProcessorCount;
        protected static int _maxArrayPoolSecondOffset = _maxArrayPoolLen / 2;

        protected CancellationToken _token;
        protected long _sizeToBePatched;
        protected long _sizePatched;
        protected Stopwatch _stopwatch;
        protected string _pathInput;
        protected string _pathOutput;
        protected TDirPatcher? _dirPatchInfo;
        protected long _rleCodeOffset;

        internal PatchCore(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
        {
            _token = token;
            _sizeToBePatched = sizeToBePatched;
            _stopwatch = stopwatch;
            _sizePatched = 0;
            _pathInput = inputPath;
            _pathOutput = outputPath;
        }

        internal void SetTDirPatcher(TDirPatcher input) => _dirPatchInfo = input;
        internal void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0)
        {
            _sizeToBePatched = sizeToBePatched;
            _sizePatched = sizeToPatch;
        }

        internal Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
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

        internal void GetDecompressStreamPlugin(CompressionMode type, Stream sourceStream, out Stream decompStream,
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

            decompStream = type switch
            {
                CompressionMode.nocomp => rawStream,
                CompressionMode.zstd => new DecompressionStream(rawStream, new DecompressionOptions(null, new Dictionary<ZSTD_dParameter, int>()
                {
                    /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
                     * always use 31 as the size_t, which is 8 bytes length.
                     * 
                     * Code Snippets (decompress_plugin_demo.h:963):
                     *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
                     */
                    { ZSTD_dParameter.ZSTD_d_windowLogMax, 31 }
                }), 0),
                CompressionMode.zlib => new DeflateStream(rawStream, System.IO.Compression.CompressionMode.Decompress, true),
                CompressionMode.bz2 => new CBZip2InputStream(rawStream, false),
                CompressionMode.pbz2 => new CBZip2InputStream(rawStream, true),
                _ => throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: {type} is not supported")
            };
        }

        internal MemoryStream CreateAndCopyToMemoryStream(Stream source)
        {
            MemoryStream returnStream = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolLen);

            try
            {
                int read;
                while ((read = source.Read(buffer)) > 0)
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

        internal virtual void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HDiffInfo hDiffInfo) => WriteCoverStreamToOutput(clips, inputStream, outputStream, hDiffInfo.headInfo.coverCount, hDiffInfo.newDataSize);

        protected IEnumerable<CoverHeader> EnumerateCoverHeaders(Stream coverReader, int coverCount)
        {
            long _oldPosBack = 0,
                 _newPosBack = 0;

            while (coverCount-- > 0)
            {
                long oldPosBack = _oldPosBack;
                long newPosBack = _newPosBack;

                byte pSign = (byte)coverReader.ReadByte();
                long oldPos, copyLength, coverLength;

                byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
                long inc_oldPos = coverReader.ReadLong7bit(_kSignTagBit, pSign);
                oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

                copyLength = coverReader.ReadLong7bit();
                coverLength = coverReader.ReadLong7bit();
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

        protected void RunCopySimilarFilesRoutine()
        {
            if (_dirPatchInfo.HasValue)
            {
                HDiffPatch.Event.PushLog("Start copying similar data");
                CopyOldSimilarToNewFiles(_dirPatchInfo.Value);

                TimeSpan timeTaken = _stopwatch.Elapsed;
                HDiffPatch.Event.PushLog($"Copying similar data has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
                HDiffPatch.Event.PushLog($"Starting patching process...");
            }
        }

        private void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, long count, long newDataSize)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolLen);
            MemoryStream cacheOutputStream = new MemoryStream();

            try
            {
                RunCopySimilarFilesRoutine();

                long newPosBack = 0;
                RLERefClipStruct rleStruct = new RLERefClipStruct();

                foreach (CoverHeader cover in EnumerateCoverHeaders(clips[0], (int)count))
                {
                    _token.ThrowIfCancellationRequested();

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputStream.Position = cover.oldPos;

                        TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                        TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, clips[1], clips[2]);
                    }

                    TBytesCopyOldClipPatch(cacheOutputStream, inputStream, ref rleStruct, cover.oldPos, cover.coverLength, sharedBuffer, clips[1], clips[2]);
                    newPosBack = cover.newPos + cover.coverLength;

                    WriteInMemoryOutputToStream(cacheOutputStream, outputStream, cover);
                }

                if (newPosBack < newDataSize)
                {
                    long copyLength = newDataSize - newPosBack;
                    TBytesCopyStreamFromOldClip(outputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, outputStream, copyLength, sharedBuffer, clips[1], clips[2]);
                    HDiffPatch.UpdateEvent(copyLength, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
                }

                SpawnCorePatchFinishedMsg();
            }
            catch { throw; }
            finally
            {
                if (sharedBuffer != null) ArrayPool<byte>.Shared.Return(sharedBuffer);
                _stopwatch?.Stop();
                cacheOutputStream?.Dispose();
                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
            }
        }

        protected void WriteInMemoryOutputToStream(Stream cacheOutputStream, Stream outputStream, CoverHeader cover)
        {
            if (cacheOutputStream.Length > 8 << 20 || cover.nextCoverIndex == 0)
            {
                long oldPos = outputStream.Position;

                cacheOutputStream.Position = 0;
                cacheOutputStream.CopyTo(outputStream);
                cacheOutputStream.SetLength(0);

                long newPos = outputStream.Position;
                long read = newPos - oldPos;

                HDiffPatch.UpdateEvent(read, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
            }
        }

        protected void SpawnCorePatchFinishedMsg()
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

        protected void TBytesCopyStreamFromOldClip(Stream outCache, Stream copyReader, long copyLength, byte[] sharedBuffer)
        {
            long lastPos = outCache.Position;
            TBytesCopyStreamInner(copyReader, outCache, sharedBuffer, (int)copyLength);
            outCache.Position = lastPos;
        }

        protected void TBytesCopyStreamInner(Stream input, Stream output, byte[] sharedBuffer, int readLen)
        {
            while (readLen > 0)
            {
                int toRead = Math.Min(sharedBuffer.Length, readLen);
                input.ReadExactly(sharedBuffer, 0, toRead);
                output.Write(sharedBuffer, 0, toRead);
                readLen -= toRead;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);

            while (copyLength > 0)
            {
                byte pSign = (byte)rleCtrlStream.ReadByte();
                byte type = (byte)((pSign) >> (8 - _kByteRleType));
                long length = rleCtrlStream.ReadLong7bit(_kByteRleType, pSign);
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected void TBytesSetRleSingle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, Span<byte> sharedBuffer)
        {
            if (rleLoader.memSetLength != 0)
            {
                long memSetStep = rleLoader.memSetLength <= copyLength ? rleLoader.memSetLength : copyLength;
                if (rleLoader.memSetValue != 0)
                {
                    int length = (int)memSetStep;
                    long lastPos = outCache.Position;
                    outCache.Read(sharedBuffer.Slice(0, length));
                    outCache.Position = lastPos;

                    while (length-- > 0) sharedBuffer[length] += rleLoader.memSetValue;

                    outCache.Write(sharedBuffer.Slice(0, (int)memSetStep));
                }
                else
                {
                    outCache.Position += memSetStep;
                }

                copyLength -= memSetStep;
                rleLoader.memSetLength -= memSetStep;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected unsafe void TBytesSetRleVector(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int offset;
            long offsetRemained = decodeStep % Vector128<byte>.Count;
            for (offset = 0; offset < decodeStep - offsetRemained; offset += Vector128<byte>.Count)
            {
                Vector128<byte> rleVector = Sse2.LoadVector128(rlePtr + offset);
                Vector128<byte> oldVector = Sse2.LoadVector128(oldPtr + offset);
                Vector128<byte> resultVector = Sse2.Add(rleVector, oldVector);

                Sse2.Store(rlePtr + offset, resultVector);
            }

            while (offset < decodeStep) *(rlePtr + offset) += *(oldPtr + offset++);

            outCache.Write(rleBuffer, rleBufferIdx, decodeStep);

            rleLoader.memCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }

        internal static bool IsPathADir(ReadOnlySpan<char> input) => input[input.Length - 1] == '/';

        internal static ref string NewPathByIndex(string[] source, long index) => ref source[index];

        protected void CopyOldSimilarToNewFiles(TDirPatcher dirData)
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
