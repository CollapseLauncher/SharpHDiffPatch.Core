using SharpHDiffPatch.Core.Binary;
using SharpHDiffPatch.Core.Binary.Compression;
using System;
using System.IO;

namespace SharpHDiffPatch.Core.Patch
{
    internal sealed class Header
    {
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        private static readonly char[] HDIFF_HEAD = new char[] { 'H', 'D', 'I', 'F', 'F' };
#else
        private const string HDIFF_HEAD = "HDIFF";
#endif

        internal static bool TryParseHeaderInfo(Stream sr, string diffPath,
            out HeaderInfo headerInfo, out DataReferenceInfo referenceInfo)
        {
            headerInfo = new HeaderInfo();
            referenceInfo = new DataReferenceInfo();

            string headerInfoLine = sr.ReadStringToNull();
            bool isPatchDir = true;
            HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Signature info: {headerInfoLine}", Verbosity.Debug);

            if (headerInfoLine.Length > 64 || !headerInfoLine
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                .AsSpan()
#endif
                .StartsWith(HDIFF_HEAD)
                ) throw new FormatException("[Header::TryParseHeaderInfo] This is not a HDiff file format!");

            string[] hInfoArr = headerInfoLine.Split('&');
            if (hInfoArr.Length == 2)
            {
                byte pFileVer = TryGetVersion(hInfoArr[0]);
                if (pFileVer != 13) throw new FormatException("[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the single patch file with version: 13 only!");

                isPatchDir = false;

                headerInfo.headerMagic = hInfoArr[0];

                Enum.TryParse(hInfoArr[1], out headerInfo.compMode);
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {pFileVer} Compression: {headerInfo.compMode}", Verbosity.Debug);
            }
            else if (hInfoArr.Length != 3) throw new IndexOutOfRangeException($"[Header::TryParseHeaderInfo] Header info is incomplete! Expecting 3 parts but got {hInfoArr.Length} part(s) instead (Raw: {headerInfoLine})");

            if (isPatchDir)
            {
                byte hInfoVer = TryGetVersion(hInfoArr[0]);
                if (hInfoVer != 19) throw new FormatException("[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the directory patch file with version: 19 only!");

                if (hInfoArr[1] != "" && !Enum.TryParse(hInfoArr[1], true, out headerInfo.compMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[1]} compression!");
                if (string.IsNullOrEmpty(hInfoArr[2])) headerInfo.checksumMode = ChecksumMode.nochecksum;
                else if (!Enum.TryParse(hInfoArr[2], true, out headerInfo.checksumMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[2]} checksum!");
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {hInfoVer} ChecksumMode: {headerInfo.checksumMode} Compression: {headerInfo.compMode}", Verbosity.Debug);

                TryReadHeaderAndReferenceInfo(sr, ref headerInfo, ref referenceInfo);
                TryReadExternReferenceInfo(sr, diffPath, ref headerInfo, ref referenceInfo);
            }
            else
            {
                TryReadNonSingleFileHeaderInfo(sr, diffPath, ref headerInfo);
            }

            return isPatchDir;
        }

        private static void TryReadExternReferenceInfo(Stream sr, string diffPath, ref HeaderInfo headerInfo, ref DataReferenceInfo referenceInfo)
        {
            long curPos = sr.Position;
            referenceInfo.headDataOffset = curPos;

            curPos += (referenceInfo.headDataCompressedSize > 0 ? referenceInfo.headDataCompressedSize : referenceInfo.headDataSize);
            referenceInfo.privateExternDataOffset = curPos;

            curPos += referenceInfo.privateExternDataSize;
            referenceInfo.externDataOffset = curPos;

            curPos += referenceInfo.externDataSize;
            referenceInfo.hdiffDataOffset = curPos;
            referenceInfo.hdiffDataSize = sr.Length - curPos;

            HDiffPatch.Event.PushLog($"[Header::TryReadExternReferenceInfo] headDataOffset: {referenceInfo.headDataOffset} | privateExternDataOffset: {referenceInfo.privateExternDataOffset} | externDataOffset: {referenceInfo.externDataOffset} | hdiffDataOffset: {referenceInfo.hdiffDataOffset} | hdiffDataSize: {referenceInfo.hdiffDataSize}", Verbosity.Debug);

            TryIdentifyDiffType(sr, diffPath, ref headerInfo, ref referenceInfo);
        }

        private static void TryIdentifyDiffType(Stream sr, string diffPath, ref HeaderInfo headerInfo, ref DataReferenceInfo referenceInfo)
        {
            sr.Position = referenceInfo.hdiffDataOffset;
            string singleCompressedHeaderLine = sr.ReadStringToNull();
            string[] singleCompressedHeaderArr = singleCompressedHeaderLine.Split('&');

            // ReSharper disable once AssignmentInConditionalExpression
            if (headerInfo.isSingleCompressedDiff =
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                singleCompressedHeaderArr[0].AsSpan().SequenceEqual("HDIFFSF20"))
#else
                singleCompressedHeaderArr[0] == "HDIFFSF20")
#endif
            {
                TryReadSingleFileHeaderInfo(sr, diffPath, ref headerInfo, referenceInfo);
                return;
            }

            HDiffPatch.Event.PushLog($"[Header::TryIdentifyDiffType] HDIFF Dir Signature: {singleCompressedHeaderLine}", Verbosity.Debug);

            if (singleCompressedHeaderArr[1] != "" && !Enum.TryParse(singleCompressedHeaderArr[1], true, out headerInfo.compMode)) throw new FormatException($"[Header::TryIdentifyDiffType] The compression chunk has unsupported compression: {singleCompressedHeaderArr[1]}");
            headerInfo.headerMagic = singleCompressedHeaderArr[0];

            TryReadNonSingleFileHeaderInfo(sr, diffPath, ref headerInfo);
        }

        private static void TryReadSingleFileHeaderInfo(Stream sr, string diffPath, ref HeaderInfo headerInfo, DataReferenceInfo referenceInfo)
        {
            headerInfo.patchPath = diffPath;
            headerInfo.singleChunkInfo = new DiffSingleChunkInfo();

            headerInfo.newDataSize = sr.ReadLong7Bit();
            headerInfo.oldDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadSingleFileHeaderInfo] oldDataSize: {headerInfo.oldDataSize} | newDataSize: {headerInfo.newDataSize}", Verbosity.Debug);

            headerInfo.chunkInfo.coverCount = sr.ReadLong7Bit();
            headerInfo.stepMemSize = sr.ReadLong7Bit();
            headerInfo.singleChunkInfo.uncompressedSize = sr.ReadLong7Bit();
            headerInfo.singleChunkInfo.compressedSize = sr.ReadLong7Bit();
            headerInfo.singleChunkInfo.diffDataPos = sr.Position - referenceInfo.hdiffDataOffset;

            headerInfo.compressedCount = headerInfo.singleChunkInfo.compressedSize > 0 ? 1 : 0;

            HDiffPatch.Event.PushLog($"[Header::TryReadSingleFileHeaderInfo] compressedCount: {headerInfo.compressedCount}", Verbosity.Debug);
        }

        private static void TryReadNonSingleFileHeaderInfo(Stream sr, string diffPath, ref HeaderInfo headerInfo)
        {
            headerInfo.patchPath = diffPath;

            long typeEndPos = sr.Position;
            headerInfo.newDataSize = sr.ReadLong7Bit();
            headerInfo.oldDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadNonSingleFileHeaderInfo] oldDataSize: {headerInfo.oldDataSize} | newDataSize: {headerInfo.newDataSize}", Verbosity.Debug);

            GetDiffChunkInfo(sr, out headerInfo.chunkInfo, typeEndPos);

            headerInfo.compressedCount = ((headerInfo.chunkInfo.compress_cover_buf_size > 1) ? 1 : 0)
                                       + ((headerInfo.chunkInfo.compress_rle_ctrlBuf_size > 1) ? 1 : 0)
                                       + ((headerInfo.chunkInfo.compress_rle_codeBuf_size > 1) ? 1 : 0)
                                       + ((headerInfo.chunkInfo.compress_newDataDiff_size > 1) ? 1 : 0);

            HDiffPatch.Event.PushLog($"[Header::TryReadNonSingleFileHeaderInfo] compressedCount: {headerInfo.compressedCount}", Verbosity.Debug);
        }

        private static void GetDiffChunkInfo(Stream sr, out DiffChunkInfo chunkInfo, long typeEndPos)
        {
            chunkInfo = new DiffChunkInfo();

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] typesEndPos: {typeEndPos}", Verbosity.Debug);

            chunkInfo.coverCount = sr.ReadLong7Bit();
            chunkInfo.compressSizeBeginPos = sr.Position;

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] coverCount: {chunkInfo.coverCount} | compressSizeBeginPos: {chunkInfo.compressSizeBeginPos}", Verbosity.Debug);

            chunkInfo.cover_buf_size = sr.ReadLong7Bit();
            chunkInfo.compress_cover_buf_size = sr.ReadLong7Bit();
            chunkInfo.rle_ctrlBuf_size = sr.ReadLong7Bit();
            chunkInfo.compress_rle_ctrlBuf_size = sr.ReadLong7Bit();
            chunkInfo.rle_codeBuf_size = sr.ReadLong7Bit();
            chunkInfo.compress_rle_codeBuf_size = sr.ReadLong7Bit();
            chunkInfo.newDataDiff_size = sr.ReadLong7Bit();
            chunkInfo.compress_newDataDiff_size = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] cover_buf_size: {chunkInfo.cover_buf_size} |  compress_cover_buf_size: {chunkInfo.compress_cover_buf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] rle_ctrlBuf_size: {chunkInfo.rle_ctrlBuf_size} | compress_rle_ctrlBuf_size: {chunkInfo.compress_rle_ctrlBuf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] rle_codeBuf_size: {chunkInfo.rle_codeBuf_size} | compress_rle_codeBuf_size: {chunkInfo.compress_rle_codeBuf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] newDataDiff_size: {chunkInfo.newDataDiff_size} | compress_newDataDiff_size: {chunkInfo.compress_newDataDiff_size}", Verbosity.Debug);

            chunkInfo.headEndPos = sr.Position;
            chunkInfo.coverEndPos = chunkInfo.headEndPos +
                                   (chunkInfo.compress_cover_buf_size > 0 ?
                                    chunkInfo.compress_cover_buf_size :
                                    chunkInfo.cover_buf_size);

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] headEndPos: {chunkInfo.headEndPos} | coverEndPos: {chunkInfo.coverEndPos}", Verbosity.Debug);
        }

        private static void TryReadHeaderAndReferenceInfo(Stream sr, ref HeaderInfo headerInfo, ref DataReferenceInfo referenceInfo)
        {
            headerInfo.isInputDir = sr.ReadBoolean();
            headerInfo.isOutputDir = sr.ReadBoolean();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] Is In/Out a Dir -> Input: {headerInfo.isInputDir} / Output: {headerInfo.isOutputDir}", Verbosity.Debug);

            referenceInfo.inputDirCount = sr.ReadLong7Bit();
            referenceInfo.inputSumSize = sr.ReadLong7Bit();

            referenceInfo.outputDirCount = sr.ReadLong7Bit();
            referenceInfo.outputSumSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] InDir Count/SumSize: {referenceInfo.inputDirCount}/{referenceInfo.inputSumSize} | OutDir Count/SumSize: {referenceInfo.outputSumSize}/{referenceInfo.inputSumSize}", Verbosity.Debug);

            referenceInfo.inputRefFileCount = sr.ReadLong7Bit();
            referenceInfo.inputRefFileSize = sr.ReadLong7Bit();

            referenceInfo.outputRefFileCount = sr.ReadLong7Bit();
            referenceInfo.outputRefFileSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] InRef Count/Size: {referenceInfo.inputRefFileCount}/{referenceInfo.inputRefFileSize} | OutRef Count/Size: {referenceInfo.outputRefFileCount}/{referenceInfo.outputRefFileSize}", Verbosity.Debug);

            referenceInfo.sameFilePairCount = sr.ReadLong7Bit();
            referenceInfo.sameFileSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] IdenticalPair Count/Size: {referenceInfo.sameFilePairCount}/{referenceInfo.sameFileSize}", Verbosity.Debug);

            referenceInfo.newExecuteCount = sr.ReadInt7Bit();
            referenceInfo.privateReservedDataSize = sr.ReadLong7Bit();
            referenceInfo.privateExternDataSize = sr.ReadLong7Bit();
            referenceInfo.externDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] newExecuteCount: {referenceInfo.newExecuteCount} | privateReservedDataSize: {referenceInfo.privateReservedDataSize} | privateExternDataSize: {referenceInfo.privateExternDataSize} | privateExternDataSize: {referenceInfo.externDataSize}", Verbosity.Debug);

            referenceInfo.compressSizeBeginPos = sr.Position;

            referenceInfo.headDataSize = sr.ReadLong7Bit();
            referenceInfo.headDataCompressedSize = sr.ReadLong7Bit();
            referenceInfo.checksumByteSize = (byte)sr.ReadLong7Bit();
            headerInfo.dirDataIsCompressed = referenceInfo.headDataCompressedSize > 0;
            referenceInfo.checksumOffset = sr.Position;

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] compressSizeBeginPos: {referenceInfo.compressSizeBeginPos} | headDataSize: {referenceInfo.headDataSize} | headDataCompressedSize: {referenceInfo.headDataCompressedSize} | checksumByteSize: {referenceInfo.checksumByteSize}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] checksumOffset: {referenceInfo.checksumOffset} | dirDataIsCompressed: {headerInfo.dirDataIsCompressed}", Verbosity.Debug);

            if (referenceInfo.checksumByteSize > 0)
            {
                HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] Seeking += {referenceInfo.checksumByteSize * 4} bytes from checksum bytes!", Verbosity.Debug);
                TrySeekHeader(sr, referenceInfo.checksumByteSize * 4);
            }
        }

        private static void TrySeekHeader(Stream sr, int skipLongSize)
        {
            int len = Math.Min(4 << 10, skipLongSize);
            HDiffPatch.Event.PushLog($"[Header::TrySeekHeader] Seeking from: {sr.Position} += {skipLongSize} to {sr.Position + skipLongSize}", Verbosity.Debug);
            sr.Seek(len, SeekOrigin.Current);
        }

#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        private static byte TryGetVersion(ReadOnlySpan<char> str)
        {
            int lastIndexOf = str.IndexOf(HDIFF_HEAD);
            if (lastIndexOf < 0) throw new IndexOutOfRangeException($"[Header::TryGetVersion] Version string is invalid! Cannot find the matching start of \"HDIFF\". Getting: {str.ToString()} instead");

            ReadOnlySpan<char> numSpan = str.Slice(lastIndexOf + HDIFF_HEAD.Length);
            if (byte.TryParse(numSpan, out byte ret)) return ret;

            throw new InvalidDataException($"[Header::TryGetVersion] Version string is invalid! Value: {numSpan.ToString()} (Raw: {str.ToString()})");
        }
#else
        private static byte TryGetVersion(string str)
        {
            int lastIndexOf = str.IndexOf(HDIFF_HEAD, StringComparison.OrdinalIgnoreCase);
            if (lastIndexOf < 0) throw new IndexOutOfRangeException($"[Header::TryGetVersion] Version string is invalid! Cannot find the matching start of \"HDIFF\". Getting: {str} instead");

            string numStr = str.Substring(lastIndexOf + HDIFF_HEAD.Length);
            if (byte.TryParse(numStr, out byte ret)) return ret;

            throw new InvalidDataException($"[Header::TryGetVersion] Version string is invalid! Value: {numStr} (Raw: {str})");
        }
#endif
    }
}
