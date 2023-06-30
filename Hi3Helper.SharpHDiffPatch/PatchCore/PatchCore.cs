using System;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
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

        internal static void UncoverBufferClips(ref byte[][] bufferClips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverToOutputNew(ref bufferClips, uncoverCount, inputStream, outputStream, hDiffInfo.newDataSize);
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
                    if (count == 2)
                    {
                        Console.WriteLine();
                    }

                    ReadCover(out CoverHeader cover, coverReader);
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif
                    CoverHeader coverUse = cover;

                    MemoryStream outCache = new MemoryStream();

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputReader.BaseStream.Position = cover.oldPos;

                        _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                        _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                    }

                    _patch_add_old_with_rle(outputStream, ref rleStruct, cover.oldPos, cover.coverLength);
                    newPosBack = cover.newPos + cover.coverLength;
                    count--;
                }

                if (newPosBack < (long)newDataSize)
                {
                    long copyLength = (long)newDataSize - newPosBack;
                    _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                    _rle_decode_skip(ref rleStruct, outputStream, copyLength);
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

        private static void WriteCoverToOutputNew(ref byte[][] bufferClips, ulong count, Stream inputStream, Stream outputStream, ulong newDataSize)
        {
            using (MemoryStream coverStream = new MemoryStream(bufferClips[0]))
            using (MemoryStream ctrlStream = new MemoryStream(bufferClips[1]))
            using (MemoryStream codeStream = new MemoryStream(bufferClips[2]))
            using (MemoryStream copyStream = new MemoryStream(bufferClips[3]))
            {
                BinaryReader coverReader = new BinaryReader(coverStream);
                BinaryReader ctrlReader = new BinaryReader(ctrlStream);
                BinaryReader codeReader = new BinaryReader(codeStream);
                BinaryReader copyReader = new BinaryReader(copyStream);
                BinaryReader inputReader = new BinaryReader(inputStream);
                BinaryWriter outputWriter = new BinaryWriter(outputStream);
                long newPosBack = 0;

                RLERefClipStruct rleStruct = new RLERefClipStruct();

                rleStruct.rleCtrlClip = ctrlReader;
                rleStruct.rleCodeClip = codeReader;
                rleStruct.rleCopyClip = copyReader;
                rleStruct.rleInputClip = inputReader;

                while (count > 0)
                {
                    ReadCover(out CoverHeader cover, coverReader);
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif
                    CoverHeader coverUse = cover;

                    MemoryStream outCache = new MemoryStream();

                    if (newPosBack < cover.newPos)
                    {
                        long copyLength = cover.newPos - newPosBack;
                        inputReader.BaseStream.Position = cover.oldPos;

                        _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                        _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                    }
                    _patch_add_old_with_rle(outputStream, ref rleStruct, cover.oldPos, cover.coverLength);
                    newPosBack = cover.newPos + cover.coverLength;
                    count--;
                }

                if (newPosBack < (long)newDataSize)
                {
                    long copyLength = (long)newDataSize - newPosBack;
                    _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                    _rle_decode_skip(ref rleStruct, outputStream, copyLength);
                }
            }
            outputStream.Dispose();
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
