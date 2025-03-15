using SharpHDiffPatch.Core.Binary;
using SharpHDiffPatch.Core.Binary.Compression;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable ConvertIfStatementToSwitchStatement

#nullable enable
namespace SharpHDiffPatch.Core.Patch
{
    internal struct RleRefClipStruct
    {
        public long MemCopyLength;
        public long MemSetLength;
        public byte MemSetValue;
    }

    internal readonly struct CoverHeader
    {
        internal readonly long OldPos;
        internal readonly long NewPos;
        internal readonly long CoverLength;
        internal readonly long NextCoverIndex;

        internal CoverHeader(long oldPos, long newPos, long coverLength, long nextCoverIndex)
        {
            OldPos = oldPos;
            NewPos = newPos;
            CoverLength = coverLength;
            NextCoverIndex = nextCoverIndex;
        }
    }

    internal interface IPatchCore
    {
        void SetDirectoryReferencePair(DirectoryReferencePair pair);

        void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0);

        void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo);

        Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed);
    }

    internal sealed class PatchCore : IPatchCore
    {
        internal unsafe delegate void RleProc(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr);
        internal static RleProc RleProcDelegate;

        internal const int KSignTagBit = 1;
        internal const int KByteRleType = 2;
        internal const int MaxMemBufferLen = 7 << 20;
        internal const int MaxMemBufferLimit = 10 << 20;
        internal const int MaxArrayCopyLen = 1 << 18;
        internal const int MaxArrayPoolLen = 4 << 20;
        internal const int MaxArrayPoolSecondOffset = MaxArrayPoolLen / 2;

        internal CancellationToken Token;
        internal long SizeToBePatched;
        internal long SizePatched;
        internal Stopwatch Stopwatch;
        internal string PathInput;
        internal string PathOutput;
        internal DirectoryReferencePair? DirReferencePair;

        static unsafe PatchCore()
        {
            RleProcDelegate =
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                Sse2.IsSupported ?
                    TBytesSetRleVectorSse2Simd :
                    AdvSimd.IsSupported ?
                        TBytesSetRleVectorAdvSimd :
#endif
                        TBytesSetRleVectorSoftware;
        }

        internal PatchCore(long sizeToBePatched, Stopwatch stopwatch, string inputPath, string outputPath, CancellationToken token)
        {
            Token = token;
            SizeToBePatched = sizeToBePatched;
            Stopwatch = stopwatch;
            SizePatched = 0;
            PathInput = inputPath;
            PathOutput = outputPath;
        }

        public void SetDirectoryReferencePair(DirectoryReferencePair pair) => DirReferencePair = pair;

        public void SetSizeToBePatched(long sizeToBePatched, long sizeToPatch = 0)
        {
            SizeToBePatched = sizeToBePatched;
            SizePatched = sizeToPatch;
        }

        public Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered, bool isFastBufferUsed)
        {
            sourceStream.Position = start;

            CompressionStreamHelper.GetDecompressStreamPlugin(compMode, sourceStream, out Stream returnStream, length, compLength, out outLength, isBuffered);

            if (!isBuffered || isFastBufferUsed) return returnStream;
            HDiffPatch.Event.PushLog($"[PatchCore::GetBufferStreamFromOffset] Caching stream from offset: {start} with length: {(compLength > 0 ? compLength : length)}");
            using (returnStream)
            {
                MemoryStream stream = CreateAndCopyToMemoryStream(returnStream);
                stream.Position = 0;
                return stream;
            }

        }

        private MemoryStream CreateAndCopyToMemoryStream(Stream source)
        {
            MemoryStream returnStream = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxArrayPoolLen);

            try
            {
                int read;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                while ((read = source.Read(buffer)) > 0)
#else
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
#endif
                {
                    Token.ThrowIfCancellationRequested();
                    returnStream.Write(buffer, 0, read);
                }

                return returnStream;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, HeaderInfo headerInfo) => WriteCoverStreamToOutput(clips, inputStream, outputStream, headerInfo.chunkInfo.coverCount, headerInfo.chunkInfo.cover_buf_size, headerInfo.newDataSize);

        internal IEnumerable<CoverHeader> EnumerateCoverHeaders(Stream coverReader, long coverSize, long coverCount)
        {
            long lastOldPosBack = 0,
                 lastNewPosBack = 0;

            if (coverSize < MaxMemBufferLen)
            {
                HDiffPatch.Event.PushLog($"[PatchCore::EnumerateCoverHeaders] Enumerate cover counts from buffer with size: {coverSize}", Verbosity.Verbose);
                byte[] buffer = new byte[coverSize];
                coverReader.ReadExactly(buffer, 0, buffer.Length);

                int offset = 0;
                while (coverCount-- > 0)
                {
                    long oldPosBack = lastOldPosBack;
                    long newPosBack = lastNewPosBack;

                    byte pSign = buffer[offset++];

                    byte incOldPosSign = (byte)(pSign >> (8 - KSignTagBit));
                    long incOldPos = buffer.ReadLong7Bit(ref offset, KSignTagBit, pSign);
                    long oldPos = incOldPosSign == 0 ? oldPosBack + incOldPos : oldPosBack - incOldPos;

                    long copyLength = buffer.ReadLong7Bit(ref offset);
                    long coverLength = buffer.ReadLong7Bit(ref offset);
                    oldPosBack   = oldPos;
                    newPosBack  += copyLength;

                    oldPosBack += true ? coverLength : 0;

                    yield return new CoverHeader(oldPos, newPosBack, coverLength, coverCount);
                    newPosBack += coverLength;

                    lastOldPosBack = oldPosBack;
                    lastNewPosBack = newPosBack;
                }
            }
            else
            {
                HDiffPatch.Event.PushLog($"[PatchCore::EnumerateCoverHeaders] Enumerate cover counts directly from stream with size: {coverSize}", Verbosity.Verbose);
                while (coverCount-- > 0)
                {
                    long oldPosBack = lastOldPosBack;
                    long newPosBack = lastNewPosBack;

                    byte pSign = (byte)coverReader.ReadByte();

                    byte incOldPosSign = (byte)(pSign >> (8 - KSignTagBit));
                    long incOldPos = coverReader.ReadLong7Bit(KSignTagBit, pSign);
                    long oldPos = incOldPosSign == 0 ? oldPosBack + incOldPos : oldPosBack - incOldPos;

                    long copyLength = coverReader.ReadLong7Bit();
                    long coverLength = coverReader.ReadLong7Bit();
                    newPosBack += copyLength;
                    oldPosBack = oldPos;

                    oldPosBack += true ? coverLength : 0;

                    yield return new CoverHeader(oldPos, newPosBack, coverLength, coverCount);
                    newPosBack += coverLength;

                    lastOldPosBack = oldPosBack;
                    lastNewPosBack = newPosBack;
                }
            }
        }

        internal void RunCopySimilarFilesRoutine()
        {
            if (DirReferencePair == null) return;
            HDiffPatch.Event.PushLog("Start copying similar data");
            CopyOldSimilarToNewFiles(DirReferencePair);

            TimeSpan timeTaken = Stopwatch.Elapsed;
            HDiffPatch.Event.PushLog($"Copying similar data has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
            HDiffPatch.Event.PushLog("Starting patching process...");
            Stopwatch.Restart();
        }

        private void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, long coverCount, long coverSize, long newDataSize)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(MaxArrayPoolLen);
            MemoryStream cacheOutputStream = new MemoryStream();

            try
            {
                RunCopySimilarFilesRoutine();

                long newPosBack = 0;
                RleRefClipStruct rleStruct = new RleRefClipStruct();
                CoverHeader[] headers = EnumerateCoverHeaders(clips[0], coverSize, coverCount).ToArray();

                for (int i = 0; i < headers.Length; i++)
                {
                    CoverHeader cover = headers[i];

                    Token.ThrowIfCancellationRequested();

                    if (newPosBack < cover.NewPos)
                    {
                        long copyLength = cover.NewPos - newPosBack;
                        inputStream.Position = cover.OldPos;

                        TBytesCopyStreamFromOldClip(cacheOutputStream, clips[3], copyLength, sharedBuffer);
                        TBytesDetermineRleType(ref rleStruct, cacheOutputStream, copyLength, sharedBuffer, clips[1], clips[2]);
                    }

                    TBytesCopyOldClipPatch(cacheOutputStream, inputStream, ref rleStruct, cover.OldPos, cover.CoverLength, sharedBuffer, clips[1], clips[2]);
                    newPosBack = cover.NewPos + cover.CoverLength;

                    if (cacheOutputStream.Length > MaxMemBufferLimit || cover.NextCoverIndex == 0)
                        WriteInMemoryOutputToStream(cacheOutputStream, outputStream);
                }

                if (newPosBack < newDataSize)
                {
                    long copyLength = newDataSize - newPosBack;
                    TBytesCopyStreamFromOldClip(outputStream, clips[3], copyLength, sharedBuffer);
                    TBytesDetermineRleType(ref rleStruct, outputStream, copyLength, sharedBuffer, clips[1], clips[2]);
                    HDiffPatch.UpdateEvent(copyLength, ref SizePatched, ref SizeToBePatched, Stopwatch);
                }

                SpawnCorePatchFinishedMsg();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sharedBuffer);
                Stopwatch?.Stop();
                cacheOutputStream?.Dispose();
                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
                inputStream?.Dispose();
                outputStream?.Dispose();
            }
        }

        internal void WriteInMemoryOutputToStream(Stream cacheOutputStream, Stream outputStream)
        {
            long oldPos = outputStream.Position;

            cacheOutputStream.Position = 0;
            cacheOutputStream.CopyTo(outputStream);
            cacheOutputStream.SetLength(0);

            long newPos = outputStream.Position;
            long read = newPos - oldPos;

            HDiffPatch.UpdateEvent(read, ref SizePatched, ref SizeToBePatched, Stopwatch);
        }

        internal void SpawnCorePatchFinishedMsg()
        {
            TimeSpan timeTaken = Stopwatch.Elapsed;
            HDiffPatch.Event.PushLog($"Patching has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.TotalMilliseconds} ms)");
        }

        private static void TBytesCopyOldClipPatch(Stream outCache, Stream inputStream, ref RleRefClipStruct rleLoader, long oldPos, long addLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            long lastPos = outCache.Position;
            inputStream.Position = oldPos;

            TBytesCopyStreamInner(inputStream, outCache, sharedBuffer, (int)addLength);

            outCache.Position = lastPos;
            TBytesDetermineRleType(ref rleLoader, outCache, addLength, sharedBuffer, rleCtrlStream, rleCodeStream);
        }

        internal static void TBytesCopyStreamFromOldClip(Stream outCache, Stream copyReader, long copyLength, byte[] sharedBuffer)
        {
            long lastPos = outCache.Position;
            TBytesCopyStreamInner(copyReader, outCache, sharedBuffer, (int)copyLength);
            outCache.Position = lastPos;
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

        private static void TBytesDetermineRleType(ref RleRefClipStruct rleLoader, Stream outCache, long copyLength, byte[] sharedBuffer,
            Stream rleCtrlStream, Stream rleCodeStream)
        {
            TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);

            while (copyLength > 0)
            {
                byte pSign = (byte)rleCtrlStream.ReadByte();
                byte type = (byte)(pSign >> (8 - KByteRleType));
                long length = rleCtrlStream.ReadLong7Bit(KByteRleType, pSign);
                ++length;

                if (type == 3)
                {
                    rleLoader.MemCopyLength = length;
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
                    continue;
                }

                rleLoader.MemSetLength = length;
                if (type == 2)
                {
                    rleLoader.MemSetValue = (byte)rleCodeStream.ReadByte();
                    TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
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
                TBytesSetRle(ref rleLoader, outCache, ref copyLength, sharedBuffer, rleCodeStream);
            }
        }

        private static unsafe void TBytesSetRle(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer, Stream rleCodeStream)
        {
            TBytesSetRleSingle(ref rleLoader, outCache, ref copyLength, sharedBuffer);

            if (rleLoader.MemCopyLength == 0) return;
            int decodeStep = (int)(rleLoader.MemCopyLength > copyLength ? copyLength : rleLoader.MemCopyLength);

            long lastPosCopy = outCache.Position;
            rleCodeStream.ReadExactly(sharedBuffer, 0, decodeStep);
            outCache.ReadExactly(sharedBuffer, MaxArrayPoolSecondOffset, decodeStep);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = &sharedBuffer[0], oldPtr = &sharedBuffer[MaxArrayPoolSecondOffset])
            {
                RleProcDelegate(ref rleLoader, outCache, ref copyLength, decodeStep, rlePtr, sharedBuffer, 0, oldPtr);
            }
        }

        internal static void TBytesSetRleSingle(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, byte[] sharedBuffer)
        {
            if (rleLoader.MemSetLength == 0) return;
            long memSetStep = rleLoader.MemSetLength <= copyLength ? rleLoader.MemSetLength : copyLength;
            if (rleLoader.MemSetValue != 0)
            {
                int length = (int)memSetStep;
                long lastPos = outCache.Position;
                _ = outCache.Read(sharedBuffer, 0, length);
                outCache.Position = lastPos;

                SetAddRLESingle:
                sharedBuffer[--length] += rleLoader.MemSetValue;
                if (length > 0) goto SetAddRLESingle;

                outCache.Write(sharedBuffer, 0, (int)memSetStep);
            }
            else
            {
                outCache.Position += memSetStep;
            }

            copyLength -= memSetStep;
            rleLoader.MemSetLength -= memSetStep;
        }

        internal static unsafe void TBytesSetRleVectorSoftware(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
        {
            int index = 0;

        AddRleSoftware:
            *(rlePtr + index) += *(oldPtr + index);
            if (++index < decodeStep) goto AddRleSoftware;

            WriteRleResultToStream(ref rleLoader, outCache, rleBuffer, rleBufferIdx, ref copyLength, decodeStep);
        }

#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        internal static unsafe void TBytesSetRleVectorAdvSimd(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
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

            WriteRemainedRleSimdResultToStream(ref rleLoader, len, outCache, ref copyLength, decodeStep, rlePtr, rleBuffer, rleBufferIdx, oldPtr);
        }

        internal static unsafe void TBytesSetRleVectorSse2Simd(ref RleRefClipStruct rleLoader, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
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

            WriteRemainedRleSimdResultToStream(ref rleLoader, len, outCache, ref copyLength, decodeStep, rlePtr, rleBuffer, rleBufferIdx, oldPtr);
        }

        private static unsafe void WriteRemainedRleSimdResultToStream(ref RleRefClipStruct rleLoader, int len, Stream outCache, ref long copyLength, int decodeStep, byte* rlePtr, byte[] rleBuffer, int rleBufferIdx, byte* oldPtr)
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

        private static void WriteRleResultToStream(ref RleRefClipStruct rleLoader, Stream outCache, byte[] rleBuffer, int rleBufferIdx, ref long copyLength, int decodeStep)
        {
            outCache.Write(rleBuffer, rleBufferIdx, decodeStep);

            rleLoader.MemCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }

        internal static bool IsPathADir(
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
            ReadOnlySpan<char>
#else
            string
#endif
                // ReSharper disable once UseIndexFromEndExpression
                input) => input.Length == 0 || input[input.Length - 1] == '/';

        internal static ref string NewPathByIndex(string[] source, long index) => ref source[index];

        private void CopyOldSimilarToNewFiles(DirectoryReferencePair dirData)
        {
            int curNewRefIndex = 0;
            int curPathIndex = 0;
            int curSamePairIndex = 0;
            int newRefCount = dirData.NewRefList.Length;
            int samePairCount = dirData.DataSamePairList.Length;
            int pathCount = dirData.NewUtf8PathList.Length;

            try
            {
                Parallel.ForEach(dirData.DataSamePairList, new ParallelOptions { CancellationToken = Token }, (pair) =>
                {
                    bool isPathADir = IsPathADir(dirData.NewUtf8PathList[pair.NewIndex]);
                    if (isPathADir) return;

                    CopyFileByPairIndex(dirData.OldUtf8PathList, dirData.NewUtf8PathList, pair.OldIndex, pair.NewIndex);
                });
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions.First();
            }

            while (curPathIndex < pathCount)
            {
                if ((curNewRefIndex < newRefCount)
                    && (curPathIndex == (dirData.NewRefList.Length > 0 ? (int)dirData.NewRefList[curNewRefIndex] : curNewRefIndex)))
                {
                    bool isPathADir = IsPathADir(dirData.NewUtf8PathList[(int)dirData.NewRefList[curNewRefIndex]]);

                    if (isPathADir) ++curPathIndex;
                    ++curNewRefIndex;
                }
                else if (curSamePairIndex < samePairCount
                    && (curPathIndex == (int)dirData.DataSamePairList[curSamePairIndex].NewIndex))
                {
                    ++curSamePairIndex;
                    ++curPathIndex;
                }
                else
                {
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                    ReadOnlySpan<char>
#else
                    string
#endif
                    pathByIndex = NewPathByIndex(dirData.NewUtf8PathList, curPathIndex);
                    string combinedNewPath = Path.Combine(PathOutput, pathByIndex.ToString());
                    bool isPathADir = false;

                    if (pathByIndex.Length > 0)
                    {
                        isPathADir = IsPathADir(pathByIndex);

                        if (isPathADir && !Directory.Exists(combinedNewPath)) Directory.CreateDirectory(combinedNewPath);
                        else if (!isPathADir && !File.Exists(combinedNewPath)) File.Create(combinedNewPath).Dispose();
                    }

                    HDiffPatch.Event.PushLog($"[PatchCore::CopyOldSimilarToNewFiles] Created a new {(isPathADir ? "directory" : "empty file")}: {combinedNewPath}", Verbosity.Debug);

                    ++curPathIndex;
                }
            }
        }

        private void CopyFileByPairIndex(string[] oldList, string[] newList, long oldIndex, long newIndex)
        {
            ref string oldPath = ref oldList[oldIndex];
            ref string newPath = ref newList[newIndex];
            string oldFullPath = Path.Combine(PathInput, oldPath);
            string newFullPath = Path.Combine(PathOutput, newPath);
            string? newDirFullPath = Path.GetDirectoryName(newFullPath);
            if (!string.IsNullOrEmpty(newDirFullPath))
                Directory.CreateDirectory(newDirFullPath);

            HDiffPatch.Event.PushLog($"[PatchCore::CopyFileByPairIndex] Copying similar file to target path: {oldFullPath} -> {newFullPath}", Verbosity.Debug);
            CopyFile(oldFullPath, newFullPath);
        }

        private void CopyFile(string inputPath, string outputPath)
        {
#if NET6_0_OR_GREATER
            byte[] buffer = GC.AllocateUninitializedArray<byte>(MaxArrayCopyLen);
#else
            byte[] buffer = new byte[MaxArrayCopyLen];
#endif
            using FileStream ifs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileStream ofs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Write);
            int read;
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
            while ((read = ifs.Read(buffer)) > 0)
#else
            while ((read = ifs.Read(buffer, 0, buffer.Length)) > 0)
#endif
            {
                Token.ThrowIfCancellationRequested();
                ofs.Write(buffer, 0, read);
                HDiffPatch.UpdateEvent(read, ref SizePatched, ref SizeToBePatched, Stopwatch);
            }
        }
    }
}
