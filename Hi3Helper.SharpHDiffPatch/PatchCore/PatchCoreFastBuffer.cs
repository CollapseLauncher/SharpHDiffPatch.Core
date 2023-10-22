using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Hi3Helper.SharpHDiffPatch
{
    internal class PatchCoreFastBuffer : PatchCore
    {
        internal PatchCoreFastBuffer(CancellationToken token, long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath)
            : base(token, sizeToBePatched, stopwatch, inputPath, outputPath) { }

        internal override void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HDiffInfo hDiffInfo) => WriteCoverStreamToOutputFast(clips, inputStream, outputStream, hDiffInfo);

        private void WriteCoverStreamToOutputFast(Stream[] clips, Stream inputStream, Stream outputStream, HDiffInfo hDiffInfo)
        {
            byte[] sharedBuffer = null;
            byte[] rleCtrlBuffer = null;
            MemoryStream cacheOutputStream = null;
            bool isCtrlUseArrayPool = hDiffInfo.headInfo.rle_ctrlBuf_size <= _maxArrayPoolSecondOffset;

            try
            {
                RunCopySimilarFilesRoutine();

                cacheOutputStream = new MemoryStream();
                sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolSecondOffset);

                int rleCtrlIdx = 0, rleCodeIdx = 0;
                rleCtrlBuffer = isCtrlUseArrayPool ? ArrayPool<byte>.Shared.Rent(_maxArrayPoolSecondOffset) : new byte[hDiffInfo.headInfo.rle_ctrlBuf_size];
                byte[] rleCodeBuffer = new byte[hDiffInfo.headInfo.rle_codeBuf_size];

                using (clips[1])
                using (clips[2])
                {
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Ctrl clip to {(isCtrlUseArrayPool ? "ArrayPool" : "heap buffer")}");
                    clips[1].ReadExactly(rleCtrlBuffer, 0, (int)hDiffInfo.headInfo.rle_ctrlBuf_size);
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Code clip to heap buffer");
                    clips[2].ReadExactly(rleCodeBuffer, 0, (int)hDiffInfo.headInfo.rle_codeBuf_size);
                }

                long newPosBack = 0;
                RLERefClipStruct rleStruct = new RLERefClipStruct();

                foreach (CoverHeader cover in EnumerateCoverHeaders(clips[0], (int)hDiffInfo.headInfo.coverCount))
                {
                    _token.ThrowIfCancellationRequested();

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputStream.Position = cover.oldPos;

                        TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                        TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                    }

                    TBytesCopyOldClipPatch(cacheOutputStream, inputStream, ref rleStruct, cover.oldPos, cover.coverLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                    newPosBack = cover.newPos + cover.coverLength;

                    WriteInMemoryOutputToStream(cacheOutputStream, outputStream, cover);
                }

                if (newPosBack < hDiffInfo.newDataSize)
                {
                    long copyLength = hDiffInfo.newDataSize - newPosBack;
                    TBytesCopyStreamFromOldClip(outputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, outputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                    HDiffPatch.UpdateEvent(copyLength, ref _sizePatched, ref _sizeToBePatched, _stopwatch);
                }

                SpawnCorePatchFinishedMsg();
            }
            catch { throw; }
            finally
            {
                if (sharedBuffer != null) ArrayPool<byte>.Shared.Return(sharedBuffer);
                if (rleCtrlBuffer != null && isCtrlUseArrayPool) ArrayPool<byte>.Shared.Return(rleCtrlBuffer);
                _stopwatch?.Stop();
                cacheOutputStream?.Dispose();
                clips[0]?.Dispose();
                clips[3]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void TBytesCopyOldClipPatch(Stream outCache, Stream inputStream, ref RLERefClipStruct rleLoader, long oldPos, long addLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            long lastPos = outCache.Position;
            long decodeStep = addLength;
            inputStream.Position = oldPos;

            TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)decodeStep);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, decodeStep, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);

            while (copyLength > 0)
            {
                byte pSign = rleCtrlBuffer[rleCtrlIdx++];
                byte type = (byte)((pSign) >> (8 - _kByteRleType));
                long length = rleCtrlBuffer.ReadLong7bit(ref rleCtrlIdx, _kByteRleType, pSign);
                ++length;

                if (type == 3)
                {
                    rleLoader.memCopyLength = length;
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
                rleLoader.memSetLength = length;
                rleLoader.memSetValue = type == 2 ? rleCodeBuffer[rleCodeIdx++] : (byte)(0x00 - type);
                TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void TBytesSetRle(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer,
            byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            TBytesSetRleSingle(ref rleLoader, outCache, ref copyLength, sharedBuffer);

            if (rleLoader.memCopyLength == 0) return;
            int decodeStep = (int)(rleLoader.memCopyLength > copyLength ? copyLength : rleLoader.memCopyLength);

            long lastPosCopy = outCache.Position;
            outCache.ReadExactly(sharedBuffer, 0, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = &rleCodeBuffer[rleCodeIdx])
            fixed (byte* oldPtr = sharedBuffer)
            {
                TBytesSetRleVector(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, rleCodeBuffer, rleCodeIdx, oldPtr);
            }
            rleCodeIdx += decodeStep;
        }
    }
}
