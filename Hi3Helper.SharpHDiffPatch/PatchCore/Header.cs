using System;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    internal sealed class Header
    {
        private static readonly char[] HDIFF_HEAD = new char[] { 'H', 'D', 'I', 'F', 'F' };

        internal static bool TryParseHeaderInfo(Stream sr, string diffPath,
            out DirectoryHDiffInfo tDirDiffInfo, out HDiffInfo singleHDiffInfo, out HDiffHeaderInfo headerInfo)
        {
            tDirDiffInfo = new DirectoryHDiffInfo();
            singleHDiffInfo = new HDiffInfo() { headInfo = new HDiffDataInfo() };
            headerInfo = new HDiffHeaderInfo();

            string headerInfoLine = sr.ReadStringToNull();
            bool isPatchDir = true;
            HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Signature info: {headerInfoLine}", Verbosity.Debug);

            if (headerInfoLine.Length > 64 || !headerInfoLine.AsSpan().StartsWith(HDIFF_HEAD)) throw new FormatException("[Header::TryParseHeaderInfo] This is not a HDiff file format!");

            string[] hInfoArr = headerInfoLine.Split('&');
            if (hInfoArr.Length == 2)
            {
                byte pFileVer = TryGetVersion(hInfoArr[0]);
                if (pFileVer != 13) throw new FormatException($"[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the single patch file with version: 13 only!");

                isPatchDir = false;

                singleHDiffInfo.headerMagic = hInfoArr[0];

                TryParseCompressionEnum(hInfoArr[1], out singleHDiffInfo.compMode);
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {pFileVer} Compression: {singleHDiffInfo.compMode}", Verbosity.Debug);
            }
            else if (hInfoArr.Length != 3) throw new IndexOutOfRangeException($"[Header::TryParseHeaderInfo] Header info is incomplete! Expecting 3 parts but got {hInfoArr.Length} part(s) instead (Raw: {headerInfoLine})");

            if (isPatchDir)
            {
                byte hInfoVer = TryGetVersion(hInfoArr[0]);
                if (hInfoVer != 19) throw new FormatException($"[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the directory patch file with version: 19 only!");

                if (hInfoArr[1] != "" && !Enum.TryParse(hInfoArr[1], true, out headerInfo.compMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[1]} compression!");
                if (!Enum.TryParse(hInfoArr[2], true, out headerInfo.checksumMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[2]} checksum!");
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {hInfoVer} ChecksumMode: {headerInfo.checksumMode} Compression: {headerInfo.compMode}", Verbosity.Debug);

                TryReadDirHeaderNumInfo(sr, tDirDiffInfo, headerInfo);
                TryAssignDirHeaderExtents(sr, tDirDiffInfo, headerInfo);
            }
            else
            {
                GetSingleHDiffInfo(sr, diffPath, singleHDiffInfo);
            }

            return isPatchDir;
        }

        private static bool TryParseCompressionEnum(string input, out CompressionMode compOut) => Enum.TryParse(input, out compOut);

        private static void TryAssignDirHeaderExtents(Stream sr, DirectoryHDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            long curPos = sr.Position;
            headerInfo.headDataOffset = curPos;

            curPos += (headerInfo.headDataCompressedSize > 0 ? headerInfo.headDataCompressedSize : headerInfo.headDataSize);
            headerInfo.privateExternDataOffset = curPos;

            curPos += headerInfo.privateExternDataSize;
            tDirDiffInfo.externDataOffset = curPos;

            curPos += tDirDiffInfo.externDataSize;
            headerInfo.hdiffDataOffset = curPos;
            headerInfo.hdiffDataSize = sr.Length - curPos;

            HDiffPatch.Event.PushLog($"[Header::TryAssignDirHeaderExtents] headDataOffset: {headerInfo.headDataOffset} | privateExternDataOffset: {headerInfo.privateExternDataOffset} | externDataOffset: {tDirDiffInfo.externDataOffset} | hdiffDataOffset: {headerInfo.hdiffDataOffset} | hdiffDataSize: {headerInfo.hdiffDataSize}", Verbosity.Debug);

            GetDirHDiffInfo(sr, tDirDiffInfo, headerInfo);
        }

        private static void GetDirHDiffInfo(Stream sr, DirectoryHDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            sr.Position = headerInfo.hdiffDataOffset;
            string singleCompressedHeaderLine = sr.ReadStringToNull();
            string[] singleCompressedHeaderArr = singleCompressedHeaderLine.Split('&');

            if (singleCompressedHeaderArr[0].AsSpan().SequenceEqual("HDIFFSF20"))
                throw new NotSupportedException("[Header::GetDirHDiffInfo] Single compressed file is not yet supported!");

            HDiffPatch.Event.PushLog($"[Header::GetDirHDiffInfo] HDIFF Dir Signature: {singleCompressedHeaderLine}", Verbosity.Debug);

            tDirDiffInfo.hdiffinfo = new HDiffInfo();
            tDirDiffInfo.hdiffinfo.headInfo = new HDiffDataInfo();

            if (singleCompressedHeaderArr[1] != "" && !Enum.TryParse(singleCompressedHeaderArr[1], true, out tDirDiffInfo.hdiffinfo.compMode)) throw new FormatException($"[Header::GetDirHDiffInfo] The compression chunk has unsupported compression: {singleCompressedHeaderArr[1]}");
            tDirDiffInfo.hdiffinfo.headerMagic = singleCompressedHeaderArr[0];

            GetNonSingleDirHDiffInfo(sr, tDirDiffInfo);
            tDirDiffInfo.isSingleCompressedDiff = false;
        }

        private static void GetNonSingleDirHDiffInfo(Stream sr, DirectoryHDiffInfo tDirDiffInfo)
        {
            if (!tDirDiffInfo.hdiffinfo.headerMagic.StartsWith("HDIFF")) throw new InvalidDataException("[Header::GetNonSingleDirHDiffInfo] The header chunk magic is not valid!");
            byte magicVersion = TryGetVersion(tDirDiffInfo.hdiffinfo.headerMagic);

            if (magicVersion != 13) throw new InvalidDataException($"[Header::GetNonSingleDirHDiffInfo] The header chunk format: v{magicVersion} is not supported!");

            long typeEndPos = sr.Position;
            tDirDiffInfo.newDataSize = sr.ReadLong7bit();
            tDirDiffInfo.oldDataSize = sr.ReadLong7bit();

            GetHDiffDataInfo(sr, tDirDiffInfo.hdiffinfo.headInfo, typeEndPos);

            tDirDiffInfo.compressedCount = ((tDirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size > 1) ? 1 : 0);

            HDiffPatch.Event.PushLog($"[Header::GetNonSingleDirHDiffInfo] compressedCount: {tDirDiffInfo.compressedCount}", Verbosity.Debug);
        }

        private static void GetSingleHDiffInfo(Stream sr, string diffPath, HDiffInfo singleHDiffInfo)
        {
            singleHDiffInfo.patchPath = diffPath;
            long typeEndPos = sr.Position;

            singleHDiffInfo.newDataSize = sr.ReadLong7bit();
            singleHDiffInfo.oldDataSize = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::GetSingleHDiffInfo] oldDataSize: {singleHDiffInfo.oldDataSize} | newDataSize: {singleHDiffInfo.newDataSize}", Verbosity.Debug);

            GetHDiffDataInfo(sr, singleHDiffInfo.headInfo, typeEndPos);

            singleHDiffInfo.compressedCount = ((singleHDiffInfo.headInfo.compress_cover_buf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_rle_ctrlBuf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_rle_codeBuf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_newDataDiff_size > 1) ? 1 : 0);

            HDiffPatch.Event.PushLog($"[Header::GetSingleHDiffInfo] compressedCount: {singleHDiffInfo.compressedCount}", Verbosity.Debug);
        }

        private static void GetHDiffDataInfo(Stream sr, HDiffDataInfo headInfo, long typeEndPos)
        {
            headInfo.typesEndPos = typeEndPos;
            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] typesEndPos: {typeEndPos}", Verbosity.Debug);

            headInfo.coverCount = sr.ReadLong7bit();
            headInfo.compressSizeBeginPos = sr.Position;

            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] coverCount: {headInfo.coverCount} | compressSizeBeginPos: {headInfo.compressSizeBeginPos}", Verbosity.Debug);

            headInfo.cover_buf_size = sr.ReadLong7bit();
            headInfo.compress_cover_buf_size = sr.ReadLong7bit();
            headInfo.rle_ctrlBuf_size = sr.ReadLong7bit();
            headInfo.compress_rle_ctrlBuf_size = sr.ReadLong7bit();
            headInfo.rle_codeBuf_size = sr.ReadLong7bit();
            headInfo.compress_rle_codeBuf_size = sr.ReadLong7bit();
            headInfo.newDataDiff_size = sr.ReadLong7bit();
            headInfo.compress_newDataDiff_size = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] cover_buf_size: {headInfo.cover_buf_size} |  compress_cover_buf_size: {headInfo.compress_cover_buf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] rle_ctrlBuf_size: {headInfo.rle_ctrlBuf_size} | compress_rle_ctrlBuf_size: {headInfo.compress_rle_ctrlBuf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] rle_codeBuf_size: {headInfo.rle_codeBuf_size} | compress_rle_codeBuf_size: {headInfo.compress_rle_codeBuf_size}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] newDataDiff_size: {headInfo.newDataDiff_size} | compress_newDataDiff_size: {headInfo.compress_newDataDiff_size}", Verbosity.Debug);

            headInfo.headEndPos = sr.Position;
            headInfo.coverEndPos = headInfo.headEndPos +
                                  (headInfo.compress_cover_buf_size > 0 ?
                                   headInfo.compress_cover_buf_size :
                                   headInfo.cover_buf_size);

            HDiffPatch.Event.PushLog($"[Header::GetHDiffDataInfo] headEndPos: {headInfo.headEndPos} | coverEndPos: {headInfo.coverEndPos}", Verbosity.Debug);
        }

        private static void TryReadDirHeaderNumInfo(Stream sr, DirectoryHDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            tDirDiffInfo.isInputDir = sr.ReadBoolean();
            tDirDiffInfo.isOutputDir = sr.ReadBoolean();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] Is In/Out a Dir -> Input: {tDirDiffInfo.isInputDir} / Output: {tDirDiffInfo.isOutputDir}", Verbosity.Debug);

            headerInfo.inputDirCount = sr.ReadLong7bit();
            headerInfo.inputSumSize = sr.ReadLong7bit();

            headerInfo.outputDirCount = sr.ReadLong7bit();
            headerInfo.outputSumSize = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] InDir Count/SumSize: {headerInfo.inputDirCount}/{headerInfo.inputSumSize} | OutDir Count/SumSize: {headerInfo.outputSumSize}/{headerInfo.inputSumSize}", Verbosity.Debug);

            headerInfo.inputRefFileCount = sr.ReadLong7bit();
            headerInfo.inputRefFileSize = sr.ReadLong7bit();

            headerInfo.outputRefFileCount = sr.ReadLong7bit();
            headerInfo.outputRefFileSize = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] InRef Count/Size: {headerInfo.inputRefFileCount}/{headerInfo.inputRefFileSize} | OutRef Count/Size: {headerInfo.outputRefFileCount}/{headerInfo.outputRefFileSize}", Verbosity.Debug);

            headerInfo.sameFilePairCount = sr.ReadLong7bit();
            headerInfo.sameFileSize = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] IdenticalPair Count/Size: {headerInfo.sameFilePairCount}/{headerInfo.sameFileSize}", Verbosity.Debug);

            headerInfo.newExecuteCount = sr.ReadInt7bit();
            headerInfo.privateReservedDataSize = sr.ReadLong7bit();
            headerInfo.privateExternDataSize = sr.ReadLong7bit();
            tDirDiffInfo.externDataSize = sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] newExecuteCount: {headerInfo.newExecuteCount} | privateReservedDataSize: {headerInfo.privateReservedDataSize} | privateExternDataSize: {headerInfo.privateExternDataSize} | privateExternDataSize: {tDirDiffInfo.externDataSize}", Verbosity.Debug);

            headerInfo.compressSizeBeginPos = sr.Position;

            headerInfo.headDataSize = sr.ReadLong7bit();
            headerInfo.headDataCompressedSize = sr.ReadLong7bit();
            tDirDiffInfo.checksumByteSize = (byte)sr.ReadLong7bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] compressSizeBeginPos: {headerInfo.compressSizeBeginPos} | headDataSize: {headerInfo.headDataSize} | headDataCompressedSize: {headerInfo.headDataCompressedSize} | checksumByteSize: {tDirDiffInfo.checksumByteSize}", Verbosity.Debug);

            tDirDiffInfo.checksumOffset = sr.Position;
            tDirDiffInfo.dirDataIsCompressed = headerInfo.headDataCompressedSize > 0;

            HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] checksumOffset: {tDirDiffInfo.checksumOffset} | dirDataIsCompressed: {tDirDiffInfo.dirDataIsCompressed}", Verbosity.Debug);

            if (tDirDiffInfo.checksumByteSize > 0)
            {
                HDiffPatch.Event.PushLog($"[Header::TryReadDirHeaderNumInfo] Seeking += {tDirDiffInfo.checksumByteSize * 4} bytes from checksum bytes!", Verbosity.Debug);
                TrySeekHeader(sr, tDirDiffInfo.checksumByteSize * 4);
            }
        }

        private static void TrySeekHeader(Stream sr, int skipLongSize)
        {
            int len = 4096;
            if (len > skipLongSize)
            {
                len = skipLongSize;
            }

            HDiffPatch.Event.PushLog($"[Header::TrySeekHeader] Seeking from: {sr.Position} += {skipLongSize} to {sr.Position + skipLongSize}", Verbosity.Debug);
            sr.Seek(len, SeekOrigin.Current);
        }

        private static byte TryGetVersion(ReadOnlySpan<char> str)
        {
            int lastIndexOf = str.IndexOf(HDIFF_HEAD);
            if (lastIndexOf < 0) throw new IndexOutOfRangeException($"[Header::TryGetVersion] Version string is invalid! Cannot find the matching start of \"HDIFF\". Getting: {str} instead");

            ReadOnlySpan<char> numSpan = str.Slice(lastIndexOf + HDIFF_HEAD.Length);
            if (byte.TryParse(numSpan, out byte ret)) return ret;

            throw new InvalidDataException($"[Header::TryGetVersion] Version string is invalid! Value: {numSpan} (Raw: {str})");
        }
    }
}
