using System;
using System.Diagnostics;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    public sealed class PatchSingle
    {
        enum kByteRleType
        {
            rle0 = 0,
            rle255 = 1,
            rle = 2,
            unrle = 3
        }

        ref struct RLERefClipStruct
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

        struct CoverHeader
        {
            internal long oldPos;
            internal long newPos;
            internal long coverLength;
        }

        private const int _kSignTagBit = 1;
        private const int _kByteRleType = 2;

        private long oldPosBack;
        private long newPosBack;
        private long coverCount;

        private CompressedHDiffInfo hDiffInfo;
        private Func<Stream> spawnPatchStream;

        private bool isUseBufferedPatch = false;

        public PatchSingle(CompressedHDiffInfo hDiffInfo)
        {
            this.hDiffInfo = hDiffInfo;
            this.spawnPatchStream = new Func<Stream>(() => new FileStream(hDiffInfo.patchPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public void Patch(string input, string output, bool useBufferedPatch = true)
        {
            isUseBufferedPatch = useBufferedPatch;

            using (FileStream inputStream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream outputStream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (Stream patchStream = spawnPatchStream())
            {
                patchStream.Seek((long)hDiffInfo.headInfo.headEndPos, SeekOrigin.Begin);
                TryCheckMatchOldSize(inputStream);

                StartPatchRoutine(inputStream, patchStream, outputStream);
            }
        }

        private void TryCheckMatchOldSize(Stream inputStream)
        {
            if (inputStream.Length != (long)hDiffInfo.oldDataSize)
            {
                throw new InvalidDataException($"The patch file is expecting old size to be equivalent as: {hDiffInfo.oldDataSize} bytes, but the input file has unmatch size: {inputStream.Length} bytes!");
            }

            Console.WriteLine("Patch Information:");
            Console.WriteLine($"    Old Size: {hDiffInfo.oldDataSize} bytes");
            Console.WriteLine($"    New Size: {hDiffInfo.newDataSize} bytes");
            Console.WriteLine($"    Is Use Buffered Patch: {isUseBufferedPatch}");

            Console.WriteLine();
            Console.WriteLine("Technical Information:");
            Console.WriteLine($"    Cover Data Offset: {hDiffInfo.headInfo.headEndPos}");
            Console.WriteLine($"    Cover Data Size: {hDiffInfo.headInfo.cover_buf_size}");
            Console.WriteLine($"    Cover Count: {hDiffInfo.headInfo.coverCount}");

            Console.WriteLine($"    RLE Data Offset: {hDiffInfo.headInfo.coverEndPos}");
            Console.WriteLine($"    RLE Control Data Size: {hDiffInfo.headInfo.rle_ctrlBuf_size}");
            Console.WriteLine($"    RLE Code Data Size: {hDiffInfo.headInfo.rle_codeBuf_size}");

            Console.WriteLine($"    New Diff Data Size: {hDiffInfo.headInfo.newDataDiff_size}");
            Console.WriteLine();
        }

        private void StartPatchRoutine(Stream inputStream, Stream patchStream, Stream outputStream)
        {
            patchStream.Seek((long)hDiffInfo.headInfo.headEndPos, SeekOrigin.Begin);

            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!isUseBufferedPatch)
            {
                ChunkStream[] clips = new ChunkStream[4];
                clips[0] = GetBufferClipsStream(patchStream, (long)hDiffInfo.headInfo.cover_buf_size);
                clips[1] = GetBufferClipsStream(patchStream, (long)hDiffInfo.headInfo.rle_ctrlBuf_size);
                clips[2] = GetBufferClipsStream(patchStream, (long)hDiffInfo.headInfo.rle_codeBuf_size);
                clips[3] = GetBufferClipsStream(patchStream, (long)hDiffInfo.headInfo.newDataDiff_size);

                UncoverBufferClipsStream(clips, inputStream, outputStream);
            }
            else
            {
                byte[][] bufferClips = new byte[4][];
                bufferClips[0] = GetBufferClips(patchStream, (long)hDiffInfo.headInfo.cover_buf_size, (long)hDiffInfo.headInfo.compress_cover_buf_size);
                bufferClips[1] = GetBufferClips(patchStream, (long)hDiffInfo.headInfo.rle_ctrlBuf_size, (long)hDiffInfo.headInfo.compress_rle_ctrlBuf_size);
                bufferClips[2] = GetBufferClips(patchStream, (long)hDiffInfo.headInfo.rle_codeBuf_size, (long)hDiffInfo.headInfo.compress_rle_codeBuf_size);
                bufferClips[3] = GetBufferClips(patchStream, (long)hDiffInfo.headInfo.newDataDiff_size, (long)hDiffInfo.headInfo.compress_newDataDiff_size);

                UncoverBufferClips(ref bufferClips, inputStream, outputStream);
            }
            stopwatch.Stop();

            TimeSpan timeTaken = stopwatch.Elapsed;
            Console.WriteLine($"Patch has been finished in {timeTaken.TotalSeconds} seconds ({timeTaken.Milliseconds} ms)");
        }

        private ChunkStream GetBufferClipsStream(Stream patchStream, long clipSize)
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

        private byte[] GetBufferClips(Stream patchStream, long clipSize, long clipSizeCompress)
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

        private void UncoverBufferClipsStream(ChunkStream[] clips, Stream inputStream, Stream outputStream)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            this.coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverStreamToOutput(clips, uncoverCount, inputStream, outputStream);
        }

        private void UncoverBufferClips(ref byte[][] bufferClips, Stream inputStream, Stream outputStream)
        {
            ulong uncoverCount = hDiffInfo.headInfo.coverCount;
            this.coverCount = (long)hDiffInfo.headInfo.coverCount;
            WriteCoverToOutputNew(ref bufferClips, uncoverCount, inputStream, outputStream);
        }

        private void WriteCoverStreamToOutput(ChunkStream[] clips, ulong count, Stream inputStream, Stream outputStream)
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
                int i = 0;

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

        private void WriteCoverToOutputNew(ref byte[][] bufferClips, ulong count, Stream inputStream, Stream outputStream)
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
                int i = 0;

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
            }
            outputStream.Dispose();
        }

        private void _patch_add_old_with_rle(Stream outCache, ref RLERefClipStruct rleLoader, long oldPos, long addLength)
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

        long copyReaderOffset = 0;
        private void _TOutStreamCache_copyFromClip(Stream outCache, BinaryReader copyReader, long copyLength)
        {
            byte[] buffer = new byte[copyLength];
            copyReaderOffset += copyLength;
            copyReader.BaseStream.Read(buffer);
            copyReader.BaseStream.Position = copyReaderOffset;
            long lastPos = outCache.Position;
            outCache.Write(buffer);
            outCache.Position = lastPos;
        }

        private void _rle_decode_skip(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
        {
            while (copyLength > 0)
            {
                _TBytesRle_load_stream_decode_add(ref rleLoader, outCache, copyLength);
                copyLength -= copyLength;
            }
        }

        private void _TBytesRle_load_stream_decode_add(ref RLERefClipStruct rleLoader, Stream outCache, long copyLength)
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

        private void _TBytesRle_load_stream_mem_add(ref RLERefClipStruct rleLoader, Stream outCache, ref long copyLength)
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

        private void ReadCover(out CoverHeader coverHeader, BinaryReader coverReader)
        {
            long oldPosBack = this.oldPosBack;
            long newPosBack = this.newPosBack;
            long coverCount = this.coverCount;

            if (coverCount > 0)
            {
                this.coverCount = coverCount - 1;
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

            // TODO: Figure out how to get isOldPosBackNeedAddLength on original source and compare it 
            oldPosBack += true ? coverLength : 0;

            coverHeader = new CoverHeader
            {
                oldPos = oldPos,
                newPos = newPosBack,
                coverLength = coverLength
            };
            newPosBack += coverLength;

            this.oldPosBack = oldPosBack;
            this.newPosBack = newPosBack;
        }
    }
}
