using SharpHDiffPatch.Core.Binary;
using SharpHDiffPatch.Core.Binary.Compression;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHDiffPatch.Core.Patch
{
    internal sealed class PatchCoreFastBuffer : IPatchCore
    {
        private const int MaxMemBufferLenBig       = 32 << 20;
        private const int MaxArrayPoolLen          = 4 << 20;
        private const int MaxArrayPoolSecondOffset = MaxArrayPoolLen / 2;
#if NET6_0_OR_GREATER
        private const int MinUninitializedArrayLen = 2 << 10;
#endif
        private readonly PatchCore _core;

        internal PatchCoreFastBuffer(long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath, Action<long> writeBytesDelegate, CancellationToken token)
        {
            _core = new PatchCore(sizeToBePatched, stopwatch, inputPath, outputPath, writeBytesDelegate, token);
        }

        public void SetDirectoryReferencePair(DirectoryReferencePair pair) => _core.SetDirectoryReferencePair(pair);

        public void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0) => _core.SetSizeToBePatched(sizeToBePatched, sizeToPatch);

        public Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed) =>
            _core.GetBufferStreamFromOffset(compMode, sourceStream, start, length, compLength, out outLength, isBuffered, isFastBufferUsed);

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo)
        {
            if (!PatchSizeHelper.CanUseFastBuffer(headerInfo))
            {
                HDiffPatch.Event.PushLog("[PatchCoreFastBuffer::UncoverBufferClipsStream] Fast buffer requirements exceeded; delegating to streaming patch core.");
                _core.UncoverBufferClipsStream(clips, inputStream, outputStream, headerInfo);
                return;
            }

            if (_core.DirReferencePair != null)
            {
                Task[] parallelTasks =
                [
                    Task.Run(() => WriteCoverStreamToOutputFast(clips, inputStream, outputStream, headerInfo)),
                    Task.Run(_core.RunCopySimilarFilesRoutine)
                ];

                Task.WaitAll(parallelTasks);
            }
            else
                WriteCoverStreamToOutputFast(clips, inputStream, outputStream, headerInfo);

            _core.SpawnCorePatchFinishedMsg();
        }

        internal static void CreateCoverHeaderAsOutputBuffer(Stream coverStream, byte[] outputBuffer, long coverSize, long coverCount)
        {
            const int sizeOfLong  = sizeof(long) * 4;
            const int kSignTagBit = 1;

            if (coverSize == 0)
            {
                return;
            }

            byte[] sevenBitCoverBuffer = ArrayPool<byte>.Shared.Rent(PatchSizeHelper.ToCheckedInt32(coverSize, nameof(coverSize)));
            ref byte sevenBitBufferRef = ref sevenBitCoverBuffer[0];

            try
            {
                int coverSizeInt = PatchSizeHelper.ToCheckedInt32(coverSize, nameof(coverSize));
                coverStream.ReadExactly(sevenBitCoverBuffer, 0, coverSizeInt);

                long lastOldPosBack = 0;
                long lastNewPosBack = 0;

                ref long outBufferOldPosRef       = ref outputBuffer.AsRef<long>();
                ref long outBufferNewPosRef       = ref outputBuffer.AsRef<long>(8);
                ref long outBufferCoverLengthRef  = ref outputBuffer.AsRef<long>(16);
                ref long outBufferNextCoverPosRef = ref outputBuffer.AsRef<long>(24);

                while (coverCount-- > 0)
                {
                    long oldPosBack = lastOldPosBack;
                    long newPosBack = lastNewPosBack;

                    byte pSign = sevenBitBufferRef;
                    sevenBitBufferRef = ref Unsafe.AddByteOffset(ref sevenBitBufferRef, 1);

                    byte incOldPosSign = (byte)(pSign >> (8 - kSignTagBit));

                    sevenBitBufferRef = ref sevenBitBufferRef.ReadLong7Bit(out long incOldPos, kSignTagBit, pSign);
                    sevenBitBufferRef = ref sevenBitBufferRef.ReadLong7Bit(out long copyLength);
                    sevenBitBufferRef = ref sevenBitBufferRef.ReadLong7Bit(out long coverLength);

                    long oldPos = incOldPosSign == 0 ? oldPosBack + incOldPos : oldPosBack - incOldPos;

                    oldPosBack  = oldPos + coverLength;
                    newPosBack += copyLength;

                    outBufferOldPosRef       = oldPos;
                    outBufferNewPosRef       = newPosBack;
                    outBufferCoverLengthRef  = coverLength;
                    outBufferNextCoverPosRef = coverCount;

                    outBufferOldPosRef       = ref Unsafe.AddByteOffset(ref outBufferOldPosRef,       sizeOfLong);
                    outBufferNewPosRef       = ref Unsafe.AddByteOffset(ref outBufferNewPosRef,       sizeOfLong);
                    outBufferCoverLengthRef  = ref Unsafe.AddByteOffset(ref outBufferCoverLengthRef,  sizeOfLong);
                    outBufferNextCoverPosRef = ref Unsafe.AddByteOffset(ref outBufferNextCoverPosRef, sizeOfLong);

                    newPosBack     += coverLength;
                    lastOldPosBack  = oldPosBack;
                    lastNewPosBack  = newPosBack;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sevenBitCoverBuffer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetRoundUpToPowerOf2(ulong value)
        {
#if NET6_0_OR_GREATER
            return BitOperations.RoundUpToPowerOf2(value);
#else
            --value;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value |= value >> 32;
            return value + 1;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MemoryStream AllocCacheMemoryStream(long newDataSize)
        {
            ulong roundedSize = GetRoundUpToPowerOf2((ulong)newDataSize);
            int bufferSizePow2 = roundedSize > int.MaxValue
                ? MaxMemBufferLenBig
                : PatchSizeHelper.ToCheckedInt32((long)roundedSize, nameof(newDataSize));
            if (bufferSizePow2 > MaxMemBufferLenBig)
                bufferSizePow2 = MaxMemBufferLenBig;

            return new MemoryStream(bufferSizePow2);
        }

        private unsafe void WriteCoverStreamToOutputFast(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo)
        {
            int rleCtrlIdx = 0, rleCodeIdx = 0;

#if !NET6_0_OR_GREATER
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(MaxArrayPoolSecondOffset);
            MemoryStream cacheOutputStream = AllocCacheMemoryStream(headerInfo.newDataSize);
            int poolSizeRemained = MaxArrayPoolLen - sharedBuffer.Length;

            bool isCtrlUseArrayPool = headerInfo.chunkInfo.rle_ctrlBuf_size <= poolSizeRemained;
            int rleCtrlBufSize = PatchSizeHelper.ToCheckedInt32(headerInfo.chunkInfo.rle_ctrlBuf_size, nameof(headerInfo.chunkInfo.rle_ctrlBuf_size));
            byte[] rleCtrlBuffer = isCtrlUseArrayPool ? ArrayPool<byte>.Shared.Rent(rleCtrlBufSize)
            : new byte[rleCtrlBufSize];
            poolSizeRemained -= rleCtrlBuffer.Length;

            bool isRleUseArrayPool = headerInfo.chunkInfo.rle_codeBuf_size <= poolSizeRemained;
            int rleCodeBufSize = PatchSizeHelper.ToCheckedInt32(headerInfo.chunkInfo.rle_codeBuf_size, nameof(headerInfo.chunkInfo.rle_codeBuf_size));
            byte[] rleCodeBuffer = isRleUseArrayPool ? ArrayPool<byte>.Shared.Rent(rleCodeBufSize)
            : new byte[rleCodeBufSize];
#else
            byte[] sharedBuffer = GC.AllocateUninitializedArray<byte>(MaxArrayPoolSecondOffset);
            MemoryStream cacheOutputStream = AllocCacheMemoryStream(headerInfo.newDataSize);

            int rleCtrlBufSize = PatchSizeHelper.ToCheckedInt32(headerInfo.chunkInfo.rle_ctrlBuf_size, nameof(headerInfo.chunkInfo.rle_ctrlBuf_size));
            bool isCtrlUseArrayPool = MinUninitializedArrayLen > rleCtrlBufSize;
            byte[] rleCtrlBuffer = isCtrlUseArrayPool ? GC.AllocateUninitializedArray<byte>(rleCtrlBufSize)
            : new byte[rleCtrlBufSize];

            int rleCodeBufSize = PatchSizeHelper.ToCheckedInt32(headerInfo.chunkInfo.rle_codeBuf_size, nameof(headerInfo.chunkInfo.rle_codeBuf_size));
            bool isRleUseArrayPool = MinUninitializedArrayLen > rleCodeBufSize;
            byte[] rleCodeBuffer = isRleUseArrayPool ? GC.AllocateUninitializedArray<byte>(rleCodeBufSize)
            : new byte[rleCodeBufSize];
#endif

            RleRefClipStruct rleStruct      = new();
            int              coverBufferLen = checked(sizeof(CoverHeader) * PatchSizeHelper.ToCheckedInt32(headerInfo.chunkInfo.coverCount, nameof(headerInfo.chunkInfo.coverCount)));
            byte[] coverBuffer =
#if NET6_0_OR_GREATER
                GC.AllocateUninitializedArray<byte>(coverBufferLen);
#else
                new byte[coverBufferLen];
#endif

            CreateCoverHeaderAsOutputBuffer(clips[0], coverBuffer, headerInfo.chunkInfo.cover_buf_size, headerInfo.chunkInfo.coverCount);

            const int sizeOfCoverHeader = sizeof(long) * 4;
            try
            {
                using (clips[1])
                using (clips[2])
                {
                    string ctrlStats = 
                        isCtrlUseArrayPool ?
#if !NET6_0_OR_GREATER
                        "ArrayPool"
#else
                        "UninitializedArray"
#endif
                        : "heap buffer";
                    string rleStats =
                        isRleUseArrayPool ?
#if !NET6_0_OR_GREATER
                        "ArrayPool"
#else
                        "UninitializedArray"
#endif
                        : "heap buffer";
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Ctrl clip to {ctrlStats}");
                    clips[1].ReadExactly(rleCtrlBuffer, 0, rleCtrlBufSize);
                    HDiffPatch.Event.PushLog($"[PatchCoreFastBuffer::WriteCoverStreamToOutputFast] Buffering RLE Code clip to {rleStats}");
                    clips[2].ReadExactly(rleCodeBuffer, 0, rleCodeBufSize);
                }

                long copyLength;
                long newPosBack = 0;
                if (coverBuffer.Length == 0)
                {
                    goto EndCoverRead;
                }

                ref CoverHeader cover = ref coverBuffer.AsRef<CoverHeader>();
                ref CoverHeader lastCover = ref coverBuffer.AsRef<CoverHeader>(coverBufferLen - sizeOfCoverHeader);

                StartCoverRead:
                if (Unsafe.IsAddressGreaterThan(ref cover, ref lastCover))
                    goto EndCoverRead;

                _core.Token.ThrowIfCancellationRequested();

                if (newPosBack < cover.NewPos)
                {
                    copyLength = cover.NewPos - newPosBack;
                    inputStream.Position = cover.OldPos;

                    PatchCore.TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                }

                TBytesCopyOldClipPatch(cacheOutputStream, inputStream, ref rleStruct, cover.OldPos, cover.CoverLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                newPosBack = cover.NewPos + cover.CoverLength;

                if (cacheOutputStream.Length > MaxMemBufferLenBig || cover.NextCoverIndex == 0)
                {
                    _core.WriteInMemoryOutputToStream(cacheOutputStream, outputStream);
                }

                cover = ref Unsafe.AddByteOffset(ref cover, sizeOfCoverHeader);
                goto StartCoverRead;

            EndCoverRead:
                if (newPosBack >= headerInfo.newDataSize) return;

                copyLength = headerInfo.newDataSize - newPosBack;
                PatchCore.TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
                _core.WriteInMemoryOutputToStream(cacheOutputStream, outputStream);
            }
            finally
            {
#if !NET6_0_OR_GREATER
                if (sharedBuffer != null) ArrayPool<byte>.Shared.Return(sharedBuffer);
                if (rleCtrlBuffer != null && isCtrlUseArrayPool) ArrayPool<byte>.Shared.Return(rleCtrlBuffer);
                if (rleCodeBuffer != null && isRleUseArrayPool) ArrayPool<byte>.Shared.Return(rleCodeBuffer);
#endif
                _core.Stopwatch.Stop();
                cacheOutputStream.Dispose();
                clips[0].Dispose();
                clips[3].Dispose();
                inputStream.Dispose();
                outputStream.Dispose();
            }
        }

        private static void TBytesCopyOldClipPatch(MemoryStream outCache, Stream inputStream, ref RleRefClipStruct rleLoader, long oldPos, long addLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            long lastPos = outCache.Position;
            inputStream.Position = oldPos;

            PatchCore.TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)addLength);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, addLength, sharedBuffer, rleCtrlBuffer, ref rleCtrlIdx, rleCodeBuffer, ref rleCodeIdx);
        }

        private static void TBytesDetermineRleType(ref RleRefClipStruct rleLoader, MemoryStream outCache, long copyLength, byte[] sharedBuffer,
            ReadOnlySpan<byte> rleCtrlBuffer, ref int rleCtrlIdx, byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);

            while (copyLength > 0)
            {
                byte pSign = rleCtrlBuffer[rleCtrlIdx++];
                byte type = (byte)(pSign >> (8 - PatchCore.KByteRleType));
                long length = rleCtrlBuffer.ReadLong7Bit(ref rleCtrlIdx, PatchCore.KByteRleType, pSign);
                ++length;

                if (type == 3)
                {
                    rleLoader.MemCopyLength = length;
                    TBytesSetRleCopyOnly(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
                    continue;
                }

                rleLoader.MemSetLength = length;
                if (type == 2)
                {
                    rleLoader.MemSetValue = rleCodeBuffer[rleCodeIdx++];
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
                    continue;
                }

                /* If the type is 1, then 0 - 1. This should result -1 in int but since
                 * we cast it to byte, then it underflow and set it to 255.
                 * This method is the same as:
                 * if (type == 0)
                 *     rleLoader.memSetValue = 0x00; // or 0 in byte
                 * else
                 *     rleLoader.memSetValue = 0xFF; // or 255 in byte
                 */
                rleLoader.MemSetValue = (byte)(0x00 - type);
                TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
            }
        }

        private static void TBytesSetRle(ref RleRefClipStruct rleLoader, MemoryStream outCache, ref long copyLength, byte[] sharedBuffer,
            byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            PatchCore.TBytesSetRleSingle(ref rleLoader, outCache, ref copyLength, sharedBuffer);

            if (rleLoader.MemCopyLength == 0) return;
            TBytesSetRleCopyOnly(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeBuffer, ref rleCodeIdx);
        }

        private static unsafe void TBytesSetRleCopyOnly(ref RleRefClipStruct rleLoader, MemoryStream outCache, ref long copyLength, byte[] sharedBuffer,
            byte[] rleCodeBuffer, ref int rleCodeIdx)
        {
            int decodeStep = (int)(rleLoader.MemCopyLength > copyLength ? copyLength : rleLoader.MemCopyLength);

            long lastPosCopy = outCache.Position;
            _ = outCache.Read(sharedBuffer, 0, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = &rleCodeBuffer[rleCodeIdx], oldPtr = &sharedBuffer[0])
            {
                PatchCore.RleProcDelegate(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, rleCodeBuffer, rleCodeIdx, oldPtr);
            }
            rleCodeIdx += decodeStep;
        }
    }
}
