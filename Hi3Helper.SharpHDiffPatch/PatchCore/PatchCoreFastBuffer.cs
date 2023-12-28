using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static Hi3Helper.SharpHDiffPatch.StreamExtension;

namespace Hi3Helper.SharpHDiffPatch
{
    internal sealed class PatchCoreFastBuffer : IPatchCore
    {
        private const int _maxMemBufferLen = 1 << 20;
        private const int _maxMemBufferLimit = 2 << 20;
        private const int _maxArrayPoolLen = 4 << 20;
        private const int _maxArrayPoolSecondOffset = _maxArrayPoolLen / 2;

        private PatchCore _core;

        internal PatchCoreFastBuffer(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
        {
            _core = new PatchCore(token, sizeToBePatched, stopwatch, inputPath, outputPath);
        }

        public void SetDirectoryReferencePair(DirectoryReferencePair pair) => _core.SetDirectoryReferencePair(pair);

        public void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0) => _core.SetSizeToBePatched(sizeToBePatched, sizeToPatch);

        public void GetDecompressStreamPlugin(CompressionMode type, Stream sourceStream, out Stream decompStream,
            long length, long compLength, out long outLength, bool isBuffered) =>
            _core.GetDecompressStreamPlugin(type, sourceStream, out decompStream, length, compLength, out outLength, isBuffered);

        public Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed) =>
            _core.GetBufferStreamFromOffset(compMode, sourceStream, start, length, compLength, out outLength, isBuffered, isFastBufferUsed);

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo)
        {
            if (_core._dirReferencePair.HasValue)
            {
                Task[] parallelTasks = [
                    Task.Run(() => WriteCoverStreamToOutputFast(clips, inputStream, outputStream, headerInfo)),
                    Task.Run(() => _core.RunCopySimilarFilesRoutine())
                ];

                Task.WaitAll(parallelTasks);
            }
            else
                WriteCoverStreamToOutputFast(clips, inputStream, outputStream, headerInfo);

            _core.SpawnCorePatchFinishedMsg();
        }

        private unsafe void WriteCoverStreamToOutputFast(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolSecondOffset);
            MemoryStream cacheOutputStream = new MemoryStream(_maxMemBufferLimit);
            int poolSizeRemained = _maxArrayPoolLen - sharedBuffer.Length;

            bool isCtrlUseArrayPool = headerInfo.chunkInfo.rle_ctrlBuf_size <= poolSizeRemained;
            byte[] rleCtrlBuffer = isCtrlUseArrayPool ? ArrayPool<byte>.Shared.Rent((int)headerInfo.chunkInfo.rle_ctrlBuf_size) : new byte[headerInfo.chunkInfo.rle_ctrlBuf_size];
            poolSizeRemained -= rleCtrlBuffer.Length;

            bool isRleUseArrayPool = headerInfo.chunkInfo.rle_codeBuf_size <= poolSizeRemained;
            byte[] rleCodeBuffer = isRleUseArrayPool ? ArrayPool<byte>.Shared.Rent((int)headerInfo.chunkInfo.rle_codeBuf_size) : new byte[headerInfo.chunkInfo.rle_codeBuf_size];

            try
            {
                cacheOutputStream = new MemoryStream(_maxMemBufferLimit);
                sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolSecondOffset);

                int rleCtrlIdx = 0, rleCodeIdx = 0;

                using (clips[1])
                using (clips[2])
                {
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Ctrl clip to {(isCtrlUseArrayPool ? "ArrayPool" : "heap buffer")}");
                    clips[1].ReadExactly(rleCtrlBuffer, 0, (int)headerInfo.chunkInfo.rle_ctrlBuf_size);
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Code clip to {(isRleUseArrayPool ? "ArrayPool" : "heap buffer")}");
                    clips[2].ReadExactly(rleCodeBuffer, 0, (int)headerInfo.chunkInfo.rle_codeBuf_size);
                }

                long oldPosBack = 0;
                long newPosBack = 0;
                int cacheSizeWritten = 0;
                RLERefClipStruct rleStruct = new RLERefClipStruct();
                IEnumerator<CoverHeader> coverIEnumerator = _core
                    .EnumerateCoverHeaders(clips[0], headerInfo.chunkInfo.cover_buf_size, headerInfo.chunkInfo.coverCount)
                    .GetEnumerator();

            StartCoverRead:
                if (!coverIEnumerator.MoveNext()) goto EndCoverRead;

                CoverHeader cover = coverIEnumerator.Current;
                _core._token.ThrowIfCancellationRequested();

                if (newPosBack < cover.newPos)
                {
                    long copyLength = cover.newPos - newPosBack;
                    inputStream.Position = cover.oldPos;

                    _core.TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                }

                TBytesCopyOldClipPatch(cacheOutputStream, inputStream, ref rleStruct, cover.oldPos, cover.coverLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                oldPosBack = newPosBack;
                newPosBack = cover.newPos + cover.coverLength;
                cacheSizeWritten += (int)(newPosBack - oldPosBack);

                if (cacheOutputStream.Length > _maxMemBufferLen || cover.nextCoverIndex == 0)
                    _core.WriteInMemoryOutputToStream(cacheOutputStream, outputStream);

                goto StartCoverRead;

            EndCoverRead:
                if (newPosBack < headerInfo.newDataSize)
                {
                    long copyLength = headerInfo.newDataSize - newPosBack;
                    _core.TBytesCopyStreamFromOldClip(outputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, outputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                    HDiffPatch.UpdateEvent(copyLength, ref _core._sizePatched, ref _core._sizeToBePatched, _core._stopwatch);
                }
            }
            catch { throw; }
            finally
            {
                if (sharedBuffer != null) ArrayPool<byte>.Shared.Return(sharedBuffer);
                if (rleCtrlBuffer != null && isCtrlUseArrayPool) ArrayPool<byte>.Shared.Return(rleCtrlBuffer);
                if (rleCodeBuffer != null && isRleUseArrayPool) ArrayPool<byte>.Shared.Return(rleCodeBuffer);
                _core._stopwatch?.Stop();
                cacheOutputStream?.Dispose();
                clips[0]?.Dispose();
                clips[3]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
            }
        }

        private void TBytesCopyOldClipPatch(Stream outCache, Stream inputStream, ref RLERefClipStruct rleLoader, long oldPos, long addLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            long lastPos = outCache.Position;
            long decodeStep = addLength;
            inputStream.Position = oldPos;

            _core.TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)decodeStep);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, decodeStep, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
        }

        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);

            while (copyLength > 0)
            {
                byte pSign = rleCtrlBuffer[rleCtrlIdx++];
                byte type = (byte)((pSign) >> (8 - PatchCore._kByteRleType));
                long length = ReadLong7bit(rleCtrlBuffer, ref rleCtrlIdx, PatchCore._kByteRleType, pSign);
                ++length;

                if (type == 3)
                {
                    rleLoader.memCopyLength = length;
                    TBytesSetRleCopyOnly(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
                    continue;
                }

                rleLoader.memSetLength = length;
                if (type == 2)
                {
                    rleLoader.memSetValue = rleCodeBuffer[rleCodeIdx++];
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
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
                TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
            }
        }

        private unsafe void TBytesSetRle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer,
            byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            _core.TBytesSetRleSingle(ref rleLoader, outCache, ref copyLength, sharedBuffer);

            if (rleLoader.memCopyLength == 0) return;
            TBytesSetRleCopyOnly(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
        }

        private unsafe void TBytesSetRleCopyOnly(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer,
            byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            int decodeStep = (int)(rleLoader.memCopyLength > copyLength ? copyLength : rleLoader.memCopyLength);

            long lastPosCopy = outCache.Position;
            outCache.Read(sharedBuffer, 0, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = &rleCodeBuffer[rleCodeIdx])
            fixed (byte* oldPtr = sharedBuffer)
            {
                _core.TBytesSetRleVectorV2(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, rleCodeBuffer, rleCodeIdx, oldPtr);
            }
            rleCodeIdx += decodeStep;
        }
    }
}
