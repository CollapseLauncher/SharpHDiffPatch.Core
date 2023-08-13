using SharpCompress.Compressors.BZip2;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ZstdNet;

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
        public long memCopyLength;
        public long memSetLength;
        public byte memSetValue;
        public kByteRleType type;

        internal BinaryReader rleCodeClip;
        internal BinaryReader rleCtrlClip;
        internal BinaryReader rleInputClip;
    };

    internal struct CoverHeader
    {
        internal long oldPos;
        internal long newPos;
        internal long coverLength;
        internal int nextCoverIndex;
    }

    internal class PatchCore
    {
        private const int _kSignTagBit = 1;
        private const int _kByteRleType = 2;

        internal static Stream GetBufferStreamFromOffset(CompressionMode compMode, Stream sourceStream,
            long start, long length, long compLength, out long outLength, bool isBuffered)
        {
            sourceStream.Position = start;

            GetDecompressStreamPlugin(compMode, sourceStream, out Stream returnStream, length, compLength, out outLength);

            if (isBuffered)
            {
                Console.Write($"Caching stream from offset: {start} with length: {(compLength > 0 ? compLength : length)}...");
                MemoryStream stream = CreateAndCopyToMemoryStream(returnStream);
                stream.Position = 0;
                returnStream?.Dispose();
                Console.WriteLine($" Done! {stream.Length} bytes written");
                return stream;
            }

            return returnStream;
        }

        internal static void GetDecompressStreamPlugin(CompressionMode type, Stream sourceStream, out Stream decompStream,
            long length, long compLength, out long outLength)
        {
            long toPosition = sourceStream.Position;
            outLength = compLength > 0 ? compLength : length;
            long toCompLength = sourceStream.Position + outLength;

            ChunkStream rawStream = new ChunkStream(sourceStream, toPosition, toCompLength, false);

            if (type != CompressionMode.nocomp && compLength == 0)
            {
                decompStream = rawStream;
                return;
            }

            decompStream = type switch
            {
                CompressionMode.nocomp => rawStream,
                CompressionMode.zstd =>
                    compLength > 0 ?
                    new DecompressionStream(new ChunkStream(sourceStream, sourceStream.Position, sourceStream.Position + compLength, false), new DecompressionOptions(null, new Dictionary<ZSTD_dParameter, int>()
                    {
                        /* HACK: The default window log max size is 30. This is unacceptable since the native HPatch implementation
                         * always use 31 as the size_t, which is 8 bytes length.
                         * 
                         * Code Snippets (decompress_plugin_demo.h:963):
                         *     #define _ZSTD_WINDOWLOG_MAX ((sizeof(size_t)<=4)?30:31)
                         */
                        { ZSTD_dParameter.ZSTD_d_windowLogMax, 31 }
                    }), 0)
                    : sourceStream,
                CompressionMode.zlib => new DeflateStream(rawStream, System.IO.Compression.CompressionMode.Decompress, true),
                CompressionMode.bz2 => new CBZip2InputStream(rawStream, false),
                CompressionMode.pbz2 => new CBZip2InputStream(rawStream, true),
                _ => throw new NotSupportedException($"Compression Type: {type} is not supported")
            };
        }

        internal static MemoryStream CreateAndCopyToMemoryStream(Stream source)
        {
            MemoryStream returnStream = new MemoryStream();
            byte[] buffer = new byte[16 << 10];

            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                HDiffPatch._token.ThrowIfCancellationRequested();
                returnStream.Write(buffer, 0, read);
            }

            return returnStream;
        }

        internal static void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo, long newDataSize)
        {
            hDiffInfo.newDataSize = newDataSize;
            UncoverBufferClipsStream(clips, inputStream, outputStream, hDiffInfo);
        }

        internal static void UncoverBufferClipsStream(Stream[] clips, Stream inputStream, Stream outputStream, CompressedHDiffInfo hDiffInfo) => WriteCoverStreamToOutput(clips, inputStream, outputStream, hDiffInfo.headInfo.coverCount, hDiffInfo.newDataSize);

        private static CoverHeader[] GetCoverHeaders(BinaryReader coverReader, int coverCount)
        {
            CoverHeader[] returnCover = new CoverHeader[coverCount];
            long _oldPosBack = 0,
                 _newPosBack = 0;

            int i = 0;
            while (coverCount-- > 0)
            {
                long oldPosBack = _oldPosBack;
                long newPosBack = _newPosBack;

                byte pSign = coverReader.ReadByte();
                long oldPos, copyLength, coverLength;

                byte inc_oldPos_sign = (byte)(pSign >> (8 - _kSignTagBit));
                long inc_oldPos = coverReader.ReadLong7bit(_kSignTagBit, pSign);
                oldPos = inc_oldPos_sign == 0 ? oldPosBack + inc_oldPos : oldPosBack - inc_oldPos;

                copyLength = coverReader.ReadLong7bit();
                coverLength = coverReader.ReadLong7bit();
                newPosBack += copyLength;
                oldPosBack = oldPos;

                oldPosBack += true ? coverLength : 0;

                returnCover[i++] = new CoverHeader
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

            return returnCover;
        }

        private static void WriteCoverStreamToOutput(Stream[] clips, Stream inputStream, Stream outputStream, long count, long newDataSize)
        {
            byte[] bufferCacheOutput = new byte[16 << 10];

            MemoryStream cacheOutputStream = new MemoryStream();
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
                rleStruct.rleInputClip = inputReader;

                CoverHeader[] cover = GetCoverHeaders(coverReader, (int)count);
                for (int i = 0; i < cover.Length; i++)
                {
                    HDiffPatch._token.ThrowIfCancellationRequested();
#if DEBUG && SHOWDEBUGINFO
                    Console.WriteLine($"Cover {i++}: oldPos -> {cover.oldPos} newPos -> {cover.newPos} length -> {cover.coverLength}");
#endif

                    if (newPosBack < cover[i].newPos)
                    {
                        long copyLength = cover[i].newPos - newPosBack;
                        inputReader.BaseStream.Position = cover[i].oldPos;

                        _TOutStreamCache_copyFromClip(cacheOutputStream, copyReader, copyLength);
                        _TBytesRle_load_stream_decode_add(ref rleStruct, cacheOutputStream, copyLength);
                    }

                    _patch_add_old_with_rle(cacheOutputStream, ref rleStruct, cover[i].oldPos, cover[i].coverLength);
                    int read = (int)(cover[i].newPos + cover[i].coverLength - newPosBack);
                    HDiffPatch.UpdateEvent(read);
                    newPosBack = cover[i].newPos + cover[i].coverLength;

                    if (cacheOutputStream.Length > 20 << 20 || cover[i].nextCoverIndex == 0)
                    {
                        int readCache;
                        cacheOutputStream.Position = 0;
                        while ((readCache = cacheOutputStream.Read(bufferCacheOutput, 0, bufferCacheOutput.Length)) > 0)
                        {
                            outputStream.Write(bufferCacheOutput, 0, readCache);
                        }
                        cacheOutputStream.Position = 0;
                        cacheOutputStream.SetLength(0);
                    }
                }

                if (newPosBack < newDataSize)
                {
                    long copyLength = newDataSize - newPosBack;
                    _TOutStreamCache_copyFromClip(outputStream, copyReader, copyLength);
                    _TBytesRle_load_stream_decode_add(ref rleStruct, outputStream, copyLength);
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
                cacheOutputStream?.Dispose();

                for (int i = 0; i < clips.Length; i++) clips[i]?.Dispose();
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
            copyReader.BaseStream.Read(buffer, 0, buffer.Length);
            long lastPos = outCache.Position;
            outCache.Write(buffer);
            outCache.Position = lastPos;
        }

        private static void _TBytesRle_load_stream_decode_add(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            long num = outCache.Position;

            _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);

            while (copyLength > 0)
            {
                byte pSign = rleLoader.rleCtrlClip.ReadByte();
                byte type = (byte)((pSign) >> (8 - _kByteRleType));
                long length = rleLoader.rleCtrlClip.ReadLong7bit(_kByteRleType, pSign);
                length++;

                switch (type)
                {
                    case 0:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0x0;
                        break;
                    case 1:
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = 0xFF;
                        break;
                    case 2:
                        byte pSetValue = rleLoader.rleCodeClip.ReadByte();
                        rleLoader.memSetLength = length;
                        rleLoader.memSetValue = pSetValue;
                        break;
                    case 3:
                        rleLoader.memCopyLength = length;
                        break;
                }

#if DEBUG && SHOWDEBUGINFO
                if (type != 3)
                {
                    Console.WriteLine($"        RLE Type: {(kByteRleType)type} -> length: {rleLoader.memSetLength} -> code: {rleLoader.memSetValue}");
                }
                else
                {
                    Console.WriteLine($"        MemCopy length: {rleLoader.memCopyLength}");
                }
#endif
                _TBytesRle_load_stream_mem_add(ref rleLoader, outCache, ref copyLength);
            }
        }

        private static unsafe void _TBytesRle_load_stream_mem_add(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength)
        {
            if (rleLoader.memSetLength != 0)
            {
                long memSetStep = rleLoader.memSetLength <= copyLength ? rleLoader.memSetLength : copyLength;
                if (rleLoader.memSetValue != 0)
                {
                    int length = (int)memSetStep;
                    Span<byte> addToSetValueBuffer = stackalloc byte[length];
                    long lastPos = outCache.Position;
                    outCache.Read(addToSetValueBuffer);
                    outCache.Position = lastPos;

                    while (length-- > 0) addToSetValueBuffer[length] += rleLoader.memSetValue;

                    outCache.Write(addToSetValueBuffer);
                }
                else
                {
                    outCache.Position += memSetStep;
                }

                copyLength -= memSetStep;
                rleLoader.memSetLength -= memSetStep;
            }

            int decodeStep = (int)(rleLoader.memCopyLength > copyLength ? copyLength : rleLoader.memCopyLength);
            if (decodeStep == 0) return;

            Span<byte> rleData = stackalloc byte[decodeStep];
            Span<byte> oldData = stackalloc byte[decodeStep];
            rleLoader.rleCodeClip.BaseStream.Read(rleData);

            long lastPosCopy = outCache.Position;
            outCache.Read(oldData);
            outCache.Position = lastPosCopy;

            fixed (byte* rlePtr = rleData)
            fixed (byte* oldPtr = oldData)
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

                while (offset < decodeStep) rleData[offset] += oldData[offset++];

                outCache.Write(rleData);
            }

            rleLoader.memCopyLength -= decodeStep;
            copyLength -= decodeStep;
        }
    }
}
