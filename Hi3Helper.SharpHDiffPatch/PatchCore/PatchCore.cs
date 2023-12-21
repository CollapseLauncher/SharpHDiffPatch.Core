using master._7zip.Legacy;
using SharpCompress.Compressors.BZip2;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Intrinsics;
#if IsARM64
using System.Runtime.Intrinsics.Arm;
#else
using System.Runtime.Intrinsics.X86;
#endif
using System.Threading;
using System.Threading.Tasks;
#if UseZSTD
using ZstdNet;
#endif
using static Hi3Helper.SharpHDiffPatch.StreamExtension;

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
        internal long nextCoverIndex;
    }

    internal interface IPatchCore
    {
        void SetDirectoryReferencePair(DirectoryReferencePair pair);

        void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0);

        void GetDecompressStreamPlugin(CompressionMode type, Stream sourceStream, out Stream decompStream,
            long length, long compLength, out long outLength, bool isBuffered);

        void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo);

        Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed);
    }

    internal sealed class PatchCore : IPatchCore
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

        internal PatchCore(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
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

            decompStream = type switch
            {
                CompressionMode.nocomp => rawStream,
                CompressionMode.zstd =>
#if UseZSTD
                new DecompressionStream(rawStream, new DecompressionOptions(null, new Dictionary<ZSTD_dParameter, int>()
                {
                    /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
                     * always use 31 as the size_t, which is 8 bytes length.
                     * 
                     * Code Snippets (decompress_plugin_demo.h:963):
                     *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
                     */
                    { ZSTD_dParameter.ZSTD_d_windowLogMax, 31 }
                }), 0),
#else
                throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: zstd is not supported in this build of SharpHDiffPatch!"),
#endif
                CompressionMode.zlib => new DeflateStream(rawStream, System.IO.Compression.CompressionMode.Decompress, true),
                CompressionMode.bz2 => new CBZip2InputStream(rawStream, false, true),
                CompressionMode.pbz2 => new CBZip2InputStream(rawStream, true, true),
                CompressionMode.lzma => CreateLzmaStream(rawStream),
                CompressionMode.lzma2 => CreateLzmaStream(rawStream),
                _ => throw new NotSupportedException($"[PatchCore::GetDecompressStreamPlugin] Compression Type: {type} is not supported")
            };;
        }

        private Stream CreateLzmaStream(Stream rawStream)
        {
            int propLen = rawStream.ReadByte();

            if (propLen != 5)
            {
                uint dicSize = propLen == 40 ? 0xFFFFFFFF : (uint)(((uint)2 | ((propLen) & 1)) << ((propLen) / 2 + 11));
                byte[] props = new byte[5]
                {
                    4,
                    (byte)dicSize,
                    (byte)(dicSize >> 8),
                    (byte)(dicSize >> 16),
                    (byte)(dicSize >> 24)
                };

                return new Lzma2DecoderStream(rawStream, (byte)propLen, long.MaxValue);
            }
            else
            {
                // byte[] props = new byte[propLen];
                // rawStream.Read(props);

                // return new LzmaDecoderStream(rawStream, props, long.MaxValue);
                throw new NotSupportedException($"[PatchCore::CreateLzmaStream] LZMA compression is not supported! only LZMA2 is currently supported!");
            }
        }

        private MemoryStream CreateAndCopyToMemoryStream(Stream source)
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

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo) => WriteCoverStreamToOutput(clips, inputStream, outputStream, headerInfo.chunkInfo.coverCount, headerInfo.chunkInfo.cover_buf_size, headerInfo.newDataSize);

        internal IEnumerable<CoverHeader> EnumerateCoverHeaders(Stream coverReader, long coverSize, long coverCount)
        {
            long _oldPosBack = 0,
                 _newPosBack = 0;

            if (coverSize < _maxMemBufferLen)
            {
                HDiffPatch.Event.PushLog($"[PatchCore::EnumerateCoverHeaders] Enumerate cover counts from buffer with size: {coverSize}", Verbosity.Verbose);
                byte[] buffer = new byte[coverSize];
                coverReader.ReadExactly(buffer);

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
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolLen);
            MemoryStream cacheOutputStream = new MemoryStream();

            try
            {
                RunCopySimilarFilesRoutine();

                long newPosBack = 0;
                RLERefClipStruct rleStruct = new RLERefClipStruct();

                foreach (CoverHeader cover in EnumerateCoverHeaders(clips[0], coverSize, coverCount))
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

                    if (cacheOutputStream.Length > _maxMemBufferLimit || cover.nextCoverIndex == 0)
                        WriteInMemoryOutputToStream(cacheOutputStream, outputStream);
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

        internal unsafe void TBytesSetRleVectorV2(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int len = decodeStep;
#if IsARM64
            if (Vector128.IsHardwareAccelerated && len >= Vector128<byte>.Count)
            {
            AddVectorArm64_128:
                len -= Vector128<byte>.Count;
                Vector128<byte> resultVector = AdvSimd.Add(*(Vector128<byte>*)(rlePtr + len), *(Vector128<byte>*)(oldPtr + len));
                AdvSimd.Store(rlePtr + len, resultVector);
                if (len > Vector128<byte>.Count) goto AddVectorArm64_128;
            }
            else if (Vector64.IsHardwareAccelerated && len >= Vector64<byte>.Count)
            {
            AddVectorArm64_64:
                len -= Vector64<byte>.Count;
                Vector64<byte> resultVector = AdvSimd.Add(*(Vector64<byte>*)(rlePtr + len), *(Vector64<byte>*)(oldPtr + len));
                AdvSimd.Store(rlePtr + len, resultVector);
                if (len > Vector64<byte>.Count) goto AddVectorArm64_64;
            }
#else
            if (Sse2.IsSupported && len >= Vector128<byte>.Count)
            {
            AddVectorSse2:
                len -= Vector128<byte>.Count;
                Vector128<byte> resultVector = Sse2.Add(*(Vector128<byte>*)(rlePtr + len), *(Vector128<byte>*)(oldPtr + len));
                Sse2.Store(rlePtr + len, resultVector);
                if (len > Vector128<byte>.Count) goto AddVectorSse2;
            }
#endif

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
            outCache.Write(rleBuffer.AsSpan(rleBufferIdx, decodeStep));

            rleLoader.memCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }

        internal unsafe void TBytesSetRleVector(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int offset = 0;
            long offsetRemained = 0;

#if IsARM64
            if (Vector128.IsHardwareAccelerated && decodeStep >= Vector128<byte>.Count)
            {
                offsetRemained = decodeStep % Vector128<byte>.Count;

            AddVectorArm64_128:
                Vector128<byte>* rleVector = (Vector128<byte>*)(rlePtr + offset);
                Vector128<byte>* oldVector = (Vector128<byte>*)(oldPtr + offset);
                Vector128<byte> resultVector = AdvSimd.Add(*rleVector, *oldVector);

                AdvSimd.Store(rlePtr + offset, resultVector);
                offset += Vector128<byte>.Count;
                if (offset < decodeStep - offsetRemained) goto AddVectorArm64_128;
            }
            else if (Vector64.IsHardwareAccelerated && decodeStep >= Vector64<byte>.Count)
            {
                offsetRemained = decodeStep % Vector64<byte>.Count;

            AddVectorArm64_64:
                Vector64<byte>* rleVector = (Vector64<byte>*)(rlePtr + offset);
                Vector64<byte>* oldVector = (Vector64<byte>*)(oldPtr + offset);
                Vector64<byte> resultVector = AdvSimd.Add(*rleVector, *oldVector);

                AdvSimd.Store(rlePtr + offset, resultVector);
                offset += Vector64<byte>.Count;
                if (offset < decodeStep - offsetRemained) goto AddVectorArm64_64;
            }
#else
            if (Sse2.IsSupported && decodeStep >= Vector128<byte>.Count)
            {
                offsetRemained = decodeStep % Vector128<byte>.Count;

            AddVectorSse2:
                Vector128<byte>* rleVector = (Vector128<byte>*)(rlePtr + offset);
                Vector128<byte>* oldVector = (Vector128<byte>*)(oldPtr + offset);
                Vector128<byte> resultVector = Sse2.Add(*rleVector, *oldVector);

                Sse2.Store(rlePtr + offset, resultVector);
                offset += Vector128<byte>.Count;
                if (offset < decodeStep - offsetRemained) goto AddVectorSse2;
            }
#endif

            if (offsetRemained != 0 && (offsetRemained % 4) == 0)
            {
            AddRemainsFourStep:
                *(rlePtr + offset) += *(oldPtr + offset);
                *(rlePtr + offset + 1) += *(oldPtr + offset + 1);
                *(rlePtr + offset + 2) += *(oldPtr + offset + 2);
                *(rlePtr + offset + 3) += *(oldPtr + offset + 3);
                offset += 4;
                if (decodeStep != offset) goto AddRemainsFourStep;
            }

        AddRemainsVectorRLE:
            if (decodeStep == offset) goto WriteAllVectorRLE;
            *(rlePtr + offset) += *(oldPtr + offset++);
            goto AddRemainsVectorRLE;

        WriteAllVectorRLE:
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
