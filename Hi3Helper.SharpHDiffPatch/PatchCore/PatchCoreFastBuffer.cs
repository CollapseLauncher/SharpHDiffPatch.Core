using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
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
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(_maxArrayPoolLen);
            MemoryStream cacheOutputStream = new MemoryStream();

            int rleCtrlIdx = 0, rleCodeIdx = 0;
            byte[] rleCtrlBuffer = new byte[hDiffInfo.headInfo.rle_ctrlBuf_size];
            byte[] rleCodeBuffer = new byte[hDiffInfo.headInfo.rle_codeBuf_size];

            using (clips[1])
            using (clips[2])
            {
                clips[1].ReadExactly(rleCtrlBuffer);
                clips[2].ReadExactly(rleCodeBuffer);
            }

            try
            {
                // RunCopySimilarFilesRoutine();

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
                _stopwatch?.Stop();
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

            TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)decodeStep);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, decodeStep, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
        }

        private int idx = 0;
        private void TBytesDetermineRleType(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);

            while (copyLength > 0)
            {
                byte pSign = rleCtrlBuffer[rleCtrlIdx];
                byte type = (byte)((pSign) >> (8 - _kByteRleType));
                long length = rleCtrlBuffer.ReadLong7bit(rleCtrlIdx, out int readCtrl, _kByteRleType, pSign);
                ++length;
                ++idx;
                rleCtrlIdx += readCtrl;

                if (idx == 13965839)
                {
                    Console.WriteLine();
                }

                if (type == 3)
                {
                    rleLoader.memCopyLength = length;
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
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

            if (rleLoader.memCopyLength == 0) return;
            int decodeStep = (int)(rleLoader.memCopyLength > copyLength ? copyLength : rleLoader.memCopyLength);

            long lastPosCopy = outCache.Position;
            outCache.ReadExactly(sharedBuffer, _maxArrayPoolSecondOffset, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = &rleCodeBuffer[rleCodeIdx])
            fixed (byte* oldPtr = &sharedBuffer[_maxArrayPoolSecondOffset])
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

                outCache.Write(rleCodeBuffer, rleCodeIdx, decodeStep);

                rleLoader.memCopyLength -= decodeStep;
                copyLength -= decodeStep;
            }
        }
    }
}
