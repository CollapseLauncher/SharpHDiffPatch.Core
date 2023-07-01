using System;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    internal enum kByteRleType
    {
        rle0 = 0,
        rle255 = 1,
        rle = 2,
        unrle = 3
    }

    internal ref struct RLERefClipStruct
    {
        public ulong memCopyLength;
        public ulong memSetLength;
        public byte memSetValue;
        public kByteRleType type;

        internal BinaryReader rleCodeClip;
        internal BinaryReader rleCtrlClip;
        internal BinaryReader rleCopyClip;
        internal BinaryReader rleInputClip;
    };

    internal struct CoverHeader
    {
        internal long oldPos;
        internal long newPos;
        internal long coverLength;
    }

    internal class PatchCore
    {
        private const int _kSignTagBit = 1;
        private const int _kByteRleType = 2;

        private static long oldPosBack;
        private static long newPosBack;
        private static long coverCount;
        private static long copyReaderOffset;

        internal static ChunkStream GetBufferClipsStream(Stream patchStream, long clipSize)
        {
            long start = patchStream.Position;
            long end = patchStream.Position + clipSize;
            long size = end - start;
#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"Start assigning chunk as Stream: start -> {start} end -> {end} size -> {size}");
#endif

            ChunkStream stream = new ChunkStream(patchStream, patchStream.Position, end, false);
            patchStream.Position += clipSize;
            return stream;
        }

        internal static void FillSingleBufferClip(Stream patchStream, Span<byte> buffer, THDiffzHead headerInfo)
        {
            int readOffset = FillBufferClip(patchStream, buffer, 0, (int)headerInfo.cover_buf_size);
            readOffset += FillBufferClip(patchStream, buffer, readOffset, (int)headerInfo.rle_ctrlBuf_size);
            readOffset += FillBufferClip(patchStream, buffer, readOffset, (int)headerInfo.rle_codeBuf_size);
            _ = FillBufferClip(patchStream, buffer, readOffset, (int)headerInfo.newDataDiff_size);
        }

        private static int FillBufferClip(Stream patchStream, Span<byte> buffer, int offset, int length)
        {
            int remainToRead = length;
            int read;
            int totalRead = 0;
            while ((remainToRead -= read = patchStream.Read(buffer.Slice(offset, remainToRead))) > 0)
            {
                HDiffPatch._token.ThrowIfCancellationRequested();
                offset += read;
                totalRead += read;
            }

            totalRead += read;

            return totalRead;
        }

        internal static byte[] GetBufferClips(Stream patchStream, long clipSize, long clipSizeCompress)
        {
            byte[] returnClip = new byte[clipSize];
            int bufSize = 4 << 10;

            long remainToRead = clipSize;
            int offset = 0;
            int read = 0;
#if DEBUG && SHOWDEBUGINFO
            Console.WriteLine($"Start reading buffer clip with buffer size: {bufSize} to size: {clipSize}");
#endif
            while ((remainToRead -= read = patchStream.Read(returnClip, offset, (int)Math.Min(bufSize, remainToRead))) > 0)
            {
                offset += read;
#if DEBUG && SHOWDEBUGINFO
                Console.WriteLine($"Reading remain {read}: Remain to read: {remainToRead}");
#endif
            }

            return returnClip;
        }

        internal static void UncoverBufferClipsStream(ChunkStream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo, ulong newDataSize)
        {
            hDiffInfo.newDataSize = newDataSize;
            UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);
        }

        internal static void UncoverBufferClipsStream(ChunkStream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverStreamToOutput(clips, uncoverCount, inputStream, outputStream, hDiffInfo.newDataSize);
        }

        internal static void UncoverBufferClips(Span<byte> bufferClip, int[] lengths, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo, ulong newDataSize)
        {
            hDiffInfo.newDataSize = newDataSize;
            UncoverBufferClips(bufferClip, lengths, inputStream, outputStream, hDiffInfo);
        }

        internal static void UncoverBufferClips(Span<byte> bufferClip, int[] lengths, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverToOutputNew(bufferClip, lengths, uncoverCount, inputStream, outputStream, hDiffInfo.newDataSize);
        }

        private static void WriteCoverStreamToOutput(ChunkStream[] clips, ulong count, Stream inputStream, Stream outputStream, ulong newDataSize)
        {
            BinaryReader coverReader = new BinaryReader(clips[0]);
            BinaryReader ctrlReader = new BinaryReader(clips[1]);
            BinaryReader codeReader = new BinaryReader(clips[2]);
            BinaryReader copyReader = new BinaryReader(clips[3]);
            BinaryReader inputReader = new BinaryReader(inputStream);
            BinaryWriter outputWriter = new BinaryWriter(outputStream);

            try
            {
                long newPosBack = 0;

                RLERefClipStruct rleStruct = new RLERefClipStruct();

                rleStruct.rleCtrlClip = ctrlReader;
                rleStruct.rleCodeClip = codeReader;
                rleStruct.rleCopyClip = copyReader;
                rleStruct.rleInputClip = inputReader;

                while (count > 0)
                {
                    HDiffPatch._token.ThrowIfCancellationRequested();
                    ReadCover(out CoverHeader cover, coverReader);
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif
                    CoverHeader coverUse = cover;

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputReader.BaseStream.Position = cover.oldPos;

                        _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                        _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                    }

                    _patch_add_old_with_rle(outputStream, ref rleStruct, cover.oldPos, cover.coverLength);
                    int read = (int)(cover.newPos + cover.coverLength - newPosBack);
                    HDiffPatch.UpdateEvent(read);
                    newPosBack = cover.newPos + cover.coverLength;
                    count--;
                }

                if (newPosBack < (long)newDataSize)
                {
                    long copyLength = (long)newDataSize - newPosBack;
                    _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                    _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                    HDiffPatch.UpdateEvent((int)copyLength);
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                coverReader?.Dispose();
                ctrlReader?.Dispose();
                codeReader?.Dispose();
                copyReader?.Dispose();
                inputReader?.Dispose();
                outputWriter?.Dispose();
            }
        }

        private static unsafe void WriteCoverToOutputNew(Span<byte> clipBuffer, int[] lengths, ulong count, Stream inputStream, Stream outputStream, ulong newDataSize)
        {
            fixed (byte* bufferA = clipBuffer.Slice(0, lengths[0]))
            fixed (byte* bufferB = clipBuffer.Slice(lengths[0], lengths[1]))
            fixed (byte* bufferC = clipBuffer.Slice(lengths[1], lengths[2]))
            fixed (byte* bufferD = clipBuffer.Slice(lengths[2], lengths[3]))
            {
                UnmanagedMemoryStream bufferAStream = new UnmanagedMemoryStream(bufferA, lengths[0]);
                UnmanagedMemoryStream bufferBStream = new UnmanagedMemoryStream(bufferB, lengths[1]);
                UnmanagedMemoryStream bufferCStream = new UnmanagedMemoryStream(bufferC, lengths[2]);
                UnmanagedMemoryStream bufferDStream = new UnmanagedMemoryStream(bufferD, lengths[3]);

                BinaryReader coverReader = new BinaryReader(bufferAStream);
                BinaryReader ctrlReader = new BinaryReader(bufferBStream);
                BinaryReader codeReader = new BinaryReader(bufferCStream);
                BinaryReader copyReader = new BinaryReader(bufferDStream);
                BinaryReader inputReader = new BinaryReader(inputStream);
                BinaryWriter outputWriter = new BinaryWriter(outputStream);

                try
                {
                    long newPosBack = 0;

                    RLERefClipStruct rleStruct = new RLERefClipStruct();

                    rleStruct.rleCtrlClip = ctrlReader;
                    rleStruct.rleCodeClip = codeReader;
                    rleStruct.rleCopyClip = copyReader;
                    rleStruct.rleInputClip = inputReader;

                    while (count > 0)
                    {
                        HDiffPatch._token.ThrowIfCancellationRequested();
                        ReadCover(out CoverHeader cover, coverReader);
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif
                        CoverHeader coverUse = cover;

                        if (newPosBack < cover.newPos)
                        {
                            long copyLength = cover.newPos - newPosBack;
                            inputReader.BaseStream.Position = cover.oldPos;

                            _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                            _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                        }
                        _patch_add_old_with_rle(outputStream, ref rleStruct, cover.oldPos, cover.coverLength);
                        int read = (int)(cover.newPos + cover.coverLength - newPosBack);
                        HDiffPatch.UpdateEvent(read);
                        newPosBack = cover.newPos + cover.coverLength;
                        count--;
                    }

                    if (newPosBack < (long)newDataSize)
                    {
                        long copyLength = (long)newDataSize - newPosBack;
                        _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                        _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                        HDiffPatch.UpdateEvent((int)copyLength);
                    }
                }
                catch { throw; }
                finally
                {
                    coverReader?.Dispose();
                    ctrlReader?.Dispose();
                    codeReader?.Dispose();
                    copyReader?.Dispose();
                    inputReader?.Dispose();
                    outputWriter?.Dispose();
                }
            }
        }

        private static void _patch_add_old_with_rle(Stream outCache, ref RLERefClipStruct rleLoader, long oldPos, long addLength)
        {
            long lastPos = outCache.Position;
            while (addLength > 0)
            {
                long decodeStep = addLength;
                rleLoader.rleInputClip.BaseStream.Position = oldPos;

                byte[] tempBuffer = new byte[decodeStep];
                rleLoader.rleInputClip.BaseStream.Read(tempBuffer);
                outCache.Write(tempBuffer);
                outCache.Position = lastPos;
                _TBytesRle_load_stream_decode_add(ref rleLoader, outCache, decodeStep);

                oldPos += decodeStep;
                addLength -= decodeStep;
            }
        }

        private static void _TOutStreamCache_copyFromClip(Stream outCache, BinaryReader copyReader, long copyLength)
        {
            byte[] buffer = new byte[copyLength];
            copyReaderOffset += copyLength;
            copyReader.BaseStream.Read(buffer);
            copyReader.BaseStream.Position = copyReaderOffset;
            long lastPos = outCache.Position;
            outCache.Write(buffer);
            outCache.Position = lastPos;
        }

        private static void _rle_decode_skip(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            while (copyLength > 0)
            {
                _TBytesRle_load_stream_decode_add(ref rleLoader, outCache, copyLength);
                copyLength -= copyLength;
            }
        }

        private static void _TBytesRle_load_stream_decode_add(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            long num = outCache.Position;

            _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);

            while (copyLength > 0)
            {
                byte type = rleLoader.rleCtrlClip.ReadByte();
                type = (byte)((type) >> (8 - _kByteRleType));
                rleLoader.rleCtrlClip.BaseStream.Position--;
                ulong length = rleLoader.rleCtrlClip.ReadUInt64VarInt(_kByteRleType);
                length++;

                switch (rleLoader.type = (kByteRleType)type)
                {
                    case kByteRleType.rle0:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0x0;
                        break;
                    case kByteRleType.rle255:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0xFF;
                        break;
                    case kByteRleType.rle:
                        byte pSetValue = rleLoader.rleCodeClip.ReadByte();
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = pSetValue;
                        break;
                    case kByteRleType.unrle:
                        rleLoader.memCopyLength = length;
                        break;
                }

#if DEBUG && SHOWDEBUGINFO
                if (rleLoader.type != kByteRleType.unrle)
                {
                    Console.WriteLine($"        RLE Type: {rleLoader.type} -> length: {rleLoader.memSetLength} -> code: {rleLoader.memSetValue}");
                }
                else
                {
                    Console.WriteLine($"        MemCopy length: {rleLoader.memCopyLength}");
                }
#endif
                _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);
            }
        }

        private static void _TBytesRle_load_stream_mem_add(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength)
        {
            if (rleLoader.memSetLength != 0)
            {
                long memSetStep = (long)rleLoader.memSetLength <= copyLength ? (long)rleLoader.memSetLength : copyLength;
                byte byteSetValue = rleLoader.memSetValue;
                if (byteSetValue != 0)
                {
                    byte[] addToSetValueBuffer = new byte[memSetStep];
                    long lastPos = outCache.Position;
                    outCache.Read(addToSetValueBuffer);
                    outCache.Position = lastPos;

                    int length = (int)memSetStep;
                    for (int i = 0; i < length; i++) addToSetValueBuffer[i] += byteSetValue;

                    outCache.Write(addToSetValueBuffer);
                }
                else
                {
                    outCache.Position += memSetStep;
                }

                copyLength -= memSetStep;
                rleLoader.memSetLength -= (ulong)memSetStep;
            }

            while (rleLoader.memCopyLength > 0 && copyLength > 0)
            {
                long decodeStep = (long)rleLoader.memCopyLength > copyLength ? copyLength : (long)rleLoader.memCopyLength;

                byte[] rleData = new byte[decodeStep];
                byte[] oldData = new byte[decodeStep];
                rleLoader.rleCodeClip.BaseStream.Read(rleData);
                long lastPos = outCache.Position;
                outCache.Read(oldData);
                outCache.Position = lastPos;

                int length = (int)decodeStep;
                for (int i = 0; i < length; i++) rleData[i] += oldData[i];

                outCache.Write(rleData);

                copyLength -= decodeStep;
                rleLoader.memCopyLength -= (ulong)decodeStep;
            }
        }

        private static void ReadCover(out CoverHeader coverHeader, BinaryReader coverReader)
        {
            long oldPosBack = PatchCore.oldPosBack;
            long newPosBack = PatchCore.newPosBack;
            long coverCount = PatchCore.coverCount;

            if (coverCount > 0)
            {
                PatchCore.coverCount = coverCount - 1;
            }

            byte pSign = coverReader.ReadByte();
            long oldPos, copyLength, coverLength;

            byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
            coverReader.BaseStream.Position--;
            long inc_oldPos = (long)coverReader.ReadUInt64VarInt(_kSignTagBit);
            oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

            copyLength = (long)coverReader.ReadUInt64VarInt();
            coverLength = (long)coverReader.ReadUInt64VarInt();
            newPosBack += copyLength;
            oldPosBack = oldPos;

            oldPosBack += true ? coverLength : 0;

            coverHeader = new CoverHeader
            {
                oldPos = oldPos,
                newPos = newPosBack,
                coverLength = coverLength
            };
            newPosBack += coverLength;

            PatchCore.oldPosBack = oldPosBack;
            PatchCore.newPosBack = newPosBack;
        }
    }
}
