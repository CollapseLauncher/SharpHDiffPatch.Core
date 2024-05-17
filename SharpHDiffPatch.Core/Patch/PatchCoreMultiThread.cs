#if USEEXPERIMENTALMULTITHREAD
using SharpHDiffPatch.Core.Binary.Compression;
using SharpHDiffPatch.Core.Binary.Streams;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using System.Threading;
using System.Threading.Tasks;
using static SharpHDiffPatch.Core.Binary.StreamExtension;

namespace SharpHDiffPatch.Core.Patch
{
    internal sealed class PatchCoreMultiThread : IPatchCore
    {
        internal unsafe delegate void RleProc(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr);
        internal static RleProc _rleProcDelegate;

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

        static PatchCoreMultiThread()
        {
            SetRleProcDelegate();
        }

        internal PatchCoreMultiThread(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
        {
            _token = token;
            _sizeToBePatched = sizeToBePatched;
            _stopwatch = stopwatch;
            _sizePatched = 0;
            _pathInput = inputPath;
            _pathOutput = outputPath;
        }

        private static unsafe void SetRleProcDelegate() =>
            _rleProcDelegate =
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
            Sse2.IsSupported ?
            TBytesSetRleVectorSSE2Simd :
            AdvSimd.IsSupported ?
                TBytesSetRleVectorAdvSimd :
#endif
                TBytesSetRleVectorSoftware;

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

            CompressionStreamHelper.GetDecompressStreamPlugin(compMode, sourceStream, out Stream returnStream, length, compLength, out outLength, isBuffered);

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

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo)
        {
            Task copyTask = Task.Run(RunCopySimilarFilesRoutine);
            Task patchTask = Task.Run(() => WriteCoverStreamToOutput(clips, inputStream, outputStream, headerInfo.chunkInfo.coverCount, headerInfo.chunkInfo.cover_buf_size, headerInfo.newDataSize));
            Task.WaitAll(copyTask, patchTask);
        }

        internal CoverHeader[] GetCoverHeaders(Stream coverReader, long coverSize, long coverCount, CancellationToken token)
        {
            long _oldPosBack = 0,
                 _newPosBack = 0;

            HDiffPatch.Event.PushLog($"[PatchCoreMultiThread::GetCoverHeaders] Getting cover headers from buffer with size: {coverSize}", Verbosity.Verbose);
            
            CoverHeader[] headers = new CoverHeader[coverCount];
            for (long i = 0; i < coverCount; i++)
            {
                token.ThrowIfCancellationRequested();
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

                headers[i] = new CoverHeader
                {
                    oldPos = oldPos,
                    newPos = newPosBack,
                    coverLength = coverLength,
                    currentCoverIndex = i,
                    nextCoverIndex = coverCount
                };

                newPosBack += coverLength;
                _oldPosBack = oldPosBack;
                _newPosBack = newPosBack;
            }

            return headers;
        }

        private CombinedStream[] CopyCombinedStream(CombinedStream stream, int count)
        {
            CombinedStream[] returnStreams = new CombinedStream[count];
            for (int i = 0; i < count; i++)
                returnStreams[i] = stream.CopyInstance();

            return returnStreams;
        }

        private void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, long coverCount, long coverSize, long newDataSize)
        {
            int threadCount = Environment.ProcessorCount;
            byte[][] sharedBuffer = new byte[threadCount][];
            for (int i = 0; i < threadCount; i++)
                sharedBuffer[i] = new byte[_maxArrayPoolLen];

            if (inputStream.GetType() != typeof(CombinedStream)
             || outputStream.GetType() != typeof(CombinedStream))
                throw new InvalidOperationException("[PatchCoreMultiThread::WriteCoverStreamToOutput] Type of inputStream or outputStream is not a \"CombinedStream\" type!");

            try
            {
                RLERefClipStruct rleStruct = new RLERefClipStruct();

                CoverHeader[] coverHeaders = GetCoverHeaders(clips[0], coverSize, coverCount, _token);

                HDiffPatch.Event.PushLog($"[PatchCoreMultiThread::WriteCoverStreamToOutput] Copying CombinedStream instance into {threadCount} instances!", Verbosity.Verbose);
                CombinedStream[] inputCombinedStreamIns = CopyCombinedStream((CombinedStream)inputStream, threadCount);
                CombinedStream[] outputCombinedStreamIns = CopyCombinedStream((CombinedStream)outputStream, threadCount);
                HDiffPatch.Event.PushLog($"[PatchCoreMultiThread::WriteCoverStreamToOutput] Done copying CombinedStream instances!", Verbosity.Verbose);

                // goto SingleThreadMode;
                Parallel.ForEach(coverHeaders, new ParallelOptions { CancellationToken = _token, MaxDegreeOfParallelism = threadCount }, (cover) =>
                {
                    int currentIndex = (int)cover.currentCoverIndex;
                    int currentThread = currentIndex % threadCount;

                    _token.ThrowIfCancellationRequested();
                    long newPosBack = cover.newPos + cover.coverLength;
                    lock (outputCombinedStreamIns[currentThread])
                    {
                        outputCombinedStreamIns[currentThread].Position = newPosBack - cover.coverLength;
                    }

                    if (newPosBack - cover.coverLength < cover.newPos)
                    {
                        int copyLength = (int)(cover.newPos - newPosBack - cover.coverLength);

                        inputCombinedStreamIns[currentThread].Position = cover.oldPos;

                        TBytesCopyStreamFromOldClip(outputCombinedStreamIns[currentThread], clips[3], copyLength, sharedBuffer[currentThread]);
                        TBytesDetermineRleType(ref rleStruct, outputCombinedStreamIns[currentThread], copyLength, sharedBuffer[currentThread], clips[1], clips[2]);
                    }

                    TBytesCopyOldClipPatch(outputCombinedStreamIns[currentThread], inputCombinedStreamIns[currentThread], ref rleStruct, cover.oldPos, cover.coverLength, sharedBuffer[currentThread], clips[1], clips[2]);
                });

                long newPosBackLast = 0;
                if (newPosBackLast < newDataSize)
                {
                    long copyLength = newDataSize - newPosBackLast;
                    TBytesCopyStreamFromOldClip(outputStream, clips[3], copyLength, sharedBuffer[0]);
                    TBytesDetermineRleType(ref rleStruct, outputStream, copyLength, sharedBuffer[0], clips[1], clips[2]);
                    HDiffPatch.UpdateEvent(copyLength, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
                }

                SpawnCorePatchFinishedMsg();
            }
            catch { throw; }
            finally
            {
                _stopwatch?.Stop();
                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
            }
        }

        internal void UpdateEventExternal(long read) => HDiffPatch.UpdateEvent(read, ref _sizePatched, ref _sizeToBePatched, _stopwatch);

        internal void WriteInMemoryOutputToStream(Stream cacheOutputStream, Stream outputStream)
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
            lock (rleCtrlStream)
            {
                lock (rleCodeStream)
                {
                    long lastPos = outCache.Position;
                    long decodeStep = addLength;
                    inputStream.Position = oldPos;

                    TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)decodeStep);

                    outCache.Position = lastPos;
                    TBytesDetermineRleType(ref rleLoader, outCache, decodeStep, sharedBuffer, rleCtrlStream, rleCodeStream);
                }
            }
        }

        internal static void TBytesCopyStreamFromOldClip(Stream outCache, Stream copyReader, long copyLength, byte[] sharedBuffer)
        {
            lock (copyReader)
            {
                long lastPos = outCache.Position;
                TBytesCopyStreamInner(copyReader, outCache, sharedBuffer, (int)copyLength);
                outCache.Position = lastPos;
            }
        }

        internal static void TBytesCopyStreamInner(Stream input, Stream output, byte[] sharedBuffer, int readLen)
        {
        AddBytesCopy:
            int toRead = Math.Min(sharedBuffer.Length, readLen);
            input.ReadExactly(sharedBuffer, 0, toRead);
            output.Write(sharedBuffer, 0, toRead);
            readLen -= toRead;
            if (toRead != 0) goto AddBytesCopy;
        }

        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            lock (rleCtrlStream)
            {
                lock (rleCodeStream)
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

            fixed (byte* rlePtr = &sharedBuffer[0], oldPtr = &sharedBuffer[_maxArrayPoolSecondOffset])
            {
                _rleProcDelegate(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, sharedBuffer, 0, oldPtr);
            }
        }

        internal static void TBytesSetRleSingle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer)
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

                SetAddRLESingle:
                    sharedBuffer[--length] += rleLoader.memSetValue;
                    if (length > 0) goto SetAddRLESingle;

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

        internal static unsafe void TBytesSetRleVectorSoftware(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int len = decodeStep;
            int index = 0;

        AddRleSoftware:
            *(rlePtr + index) += *(oldPtr + index);
            if (++index < len) goto AddRleSoftware;

            WriteRleResultToStream(ref rleLoader, outCache, rleBuffer, rleBufferIdx, ref copyLength, decodeStep);
        }

#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        internal static unsafe void TBytesSetRleVectorAdvSimd(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int len = decodeStep;

            if (
#if !NET7_0_OR_GREATER
                AdvSimd.IsSupported
#else
                Vector128.IsHardwareAccelerated
#endif
                && len >= Vector128<byte>.Count)
            {
            AddVectorArm64_128:
                len -= Vector128<byte>.Count;
                Vector128<byte> resultVector = AdvSimd.Add(*(Vector128<byte>*)(rlePtr + len), *(Vector128<byte>*)(oldPtr + len));
                AdvSimd.Store(rlePtr + len, resultVector);
                if (len > Vector128<byte>.Count) goto AddVectorArm64_128;
            }
            else if (
#if !NET7_0_OR_GREATER
                AdvSimd.IsSupported
#else
                Vector64.IsHardwareAccelerated
#endif
                && len >= Vector64<byte>.Count)
            {
            AddVectorArm64_64:
                len -= Vector64<byte>.Count;
                Vector64<byte> resultVector = AdvSimd.Add(*(Vector64<byte>*)(rlePtr + len), *(Vector64<byte>*)(oldPtr + len));
                AdvSimd.Store(rlePtr + len, resultVector);
                if (len > Vector64<byte>.Count) goto AddVectorArm64_64;
            }

            WriteRemainedRleSIMDResultToStream(ref rleLoader, len, outCache, ref copyLength, decodeStep, rlePtr, rleBuffer, rleBufferIdx, oldPtr);
        }

        internal static unsafe void TBytesSetRleVectorSSE2Simd(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int len = decodeStep;

            if (Sse2.IsSupported && len >= Vector128<byte>.Count)
            {
            AddVectorSse2:
                len -= Vector128<byte>.Count;
                Vector128<byte> resultVector = Sse2.Add(*(Vector128<byte>*)(rlePtr + len), *(Vector128<byte>*)(oldPtr + len));
                Sse2.Store(rlePtr + len, resultVector);
                if (len > Vector128<byte>.Count) goto AddVectorSse2;
            }

            WriteRemainedRleSIMDResultToStream(ref rleLoader, len, outCache, ref copyLength, decodeStep, rlePtr, rleBuffer, rleBufferIdx, oldPtr);
        }

        private unsafe static void WriteRemainedRleSIMDResultToStream(ref RLERefClipStruct rleLoader, int len, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            if (len >= 4)
            {
            AddRemainsFourStep:
                len -= 4;
                *(rlePtr + len) += *(oldPtr + len);
                *(rlePtr + 1 + len) += *(oldPtr + 1 + len);
                *(rlePtr + 2 + len) += *(oldPtr + 2 + len);
                *(rlePtr + 3 + len) += *(oldPtr + 3 + len);
                if (len >= 4) goto AddRemainsFourStep;
            }

        AddRemainsVectorRLE:
            if (len == 0) goto WriteAllVectorRLE;
            *(rlePtr + --len) += *(oldPtr + len);
            goto AddRemainsVectorRLE;

        WriteAllVectorRLE:
            WriteRleResultToStream(ref rleLoader, outCache, rleBuffer, rleBufferIdx, ref copyLength, decodeStep);
        }
#endif

        private static void WriteRleResultToStream(ref RLERefClipStruct rleLoader, Stream outCache, byte[] rleBuffer, int rleBufferIdx, ref long copyLength, int decodeStep)
        {
            outCache.Write(rleBuffer, rleBufferIdx, decodeStep);

            rleLoader.memCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }

        internal static bool IsPathADir(
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
            ReadOnlySpan<char>
#else
            string
#endif
            input) => input[input.Length - 1] == '/';

        internal static ref string NewPathByIndex(string[] source, long index) => ref source[index];

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
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                    ReadOnlySpan<char>
#else
                    string
#endif
                    pathByIndex = NewPathByIndex(dirData.newUtf8PathList, _curPathIndex);
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
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                    while ((read = ifs.Read(buffer)) > 0)
#else
                    while ((read = ifs.Read(buffer, 0, buffer.Length)) > 0)
#endif
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
#endif