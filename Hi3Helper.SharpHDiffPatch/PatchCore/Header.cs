using System;
using System.IO;

namespace Hi3Helper.SharpHDiffPatch
{
    internal class Header
    {
        internal static bool TryParseHeaderInfo(BinaryReader sr, string diffPath,
            out TDirDiffInfo tDirDiffInfo, out CompressedHDiffInfo singleHDiffInfo, out HDiffHeaderInfo headerInfo)
        {
            tDirDiffInfo = new TDirDiffInfo();
            singleHDiffInfo = new CompressedHDiffInfo() { headInfo = new THDiffzHead() };
            headerInfo = new HDiffHeaderInfo();

            string headerInfoLine = sr.ReadStringToNull();
            bool isPatchDir = true;

            if (headerInfoLine.Length > 64 || !headerInfoLine.StartsWith("HDIFF")) throw new FormatException("This is not a HDiff file format!");

            string[] hInfoArr = headerInfoLine.Split('&');
            if (hInfoArr.Length == 2)
            {
                byte pFileVer = TryGetVersion(hInfoArr[0]);
                if (pFileVer != 13) throw new FormatException($"HDiff version is unsupported. This patcher only supports the single patch file with version: 13 only!");

                isPatchDir = false;

                singleHDiffInfo.headerMagic = hInfoArr[0];

                TryParseCompressionEnum(hInfoArr[1], out singleHDiffInfo.compMode);
            }
            else if (hInfoArr.Length != 3) throw new IndexOutOfRangeException($"Header info is incomplete! Expecting 3 parts but got {hInfoArr.Length} part(s) instead (Raw: {headerInfoLine})");

            if (isPatchDir)
            {
                byte hInfoVer = TryGetVersion(hInfoArr[0]);
                if (hInfoVer != 19) throw new FormatException($"HDiff version is unsupported. This patcher only supports the directory patch file with version: 19 only!");

                if (hInfoArr[1] != "" && !Enum.TryParse(hInfoArr[1], true, out headerInfo.compMode)) throw new FormatException($"This patcher doesn't support {hInfoArr[1]} compression!");
                if (!Enum.TryParse(hInfoArr[2], true, out headerInfo.checksumMode)) throw new FormatException($"This patcher doesn't support {hInfoArr[2]} checksum!");

                TryReadDirHeaderNumInfo(sr, tDirDiffInfo, headerInfo);
                TryAssignDirHeaderExtents(sr, tDirDiffInfo, headerInfo);
            }
            else
            {
                GetSingleCompressedHDiffInfo(sr, diffPath, singleHDiffInfo);
            }

            return isPatchDir;
        }

        private static bool TryParseCompressionEnum(string input, out CompressionMode compOut) => Enum.TryParse(input, out compOut);

        private static void TryAssignDirHeaderExtents(BinaryReader sr, TDirDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            long curPos = sr.BaseStream.Position;
            headerInfo.headDataOffset = curPos;

            curPos += (headerInfo.headDataCompressedSize > 0 ? headerInfo.headDataCompressedSize : headerInfo.headDataSize);
            headerInfo.privateExternDataOffset = curPos;

            curPos += headerInfo.privateExternDataSize;
            tDirDiffInfo.externDataOffset = curPos;

            curPos += tDirDiffInfo.externDataSize;
            headerInfo.hdiffDataOffset = curPos;
            headerInfo.hdiffDataSize = sr.BaseStream.Length - curPos;

            TryReadTDirHDiffInfo(sr, tDirDiffInfo, headerInfo);
        }

        private static void TryReadTDirHDiffInfo(BinaryReader sr, TDirDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            tDirDiffInfo.isSingleCompressedDiff = false;

            if (IsSingleCompressedHDiff(sr, tDirDiffInfo, headerInfo))
            {
                // TODO
            }
            else
            {
                GetNonSingleCompressedHDiffInfo(sr, tDirDiffInfo, headerInfo);
            }
        }

        private static void GetSingleCompressedHDiffInfo(BinaryReader sr, string diffPath, CompressedHDiffInfo singleHDiffInfo)
        {
            singleHDiffInfo.patchPath = diffPath;
            singleHDiffInfo.headInfo.typesEndPos = sr.BaseStream.Position;
            singleHDiffInfo.newDataSize = sr.ReadLong7bit();
            singleHDiffInfo.oldDataSize = sr.ReadLong7bit();

            singleHDiffInfo.headInfo.coverCount = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.compressSizeBeginPos = sr.BaseStream.Position;
            singleHDiffInfo.headInfo.cover_buf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.compress_cover_buf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.rle_ctrlBuf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.compress_rle_ctrlBuf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.rle_codeBuf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.compress_rle_codeBuf_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.newDataDiff_size = sr.ReadLong7bit();
            singleHDiffInfo.headInfo.compress_newDataDiff_size = sr.ReadLong7bit();

            singleHDiffInfo.headInfo.headEndPos = sr.BaseStream.Position;

            singleHDiffInfo.compressedCount = ((singleHDiffInfo.headInfo.compress_cover_buf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_rle_ctrlBuf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_rle_codeBuf_size > 1) ? 1 : 0)
                                            + ((singleHDiffInfo.headInfo.compress_newDataDiff_size > 1) ? 1 : 0);

            singleHDiffInfo.headInfo.coverEndPos = singleHDiffInfo.headInfo.headEndPos
                                                + (singleHDiffInfo.headInfo.compress_cover_buf_size > 0 ?
                                                   singleHDiffInfo.headInfo.compress_cover_buf_size :
                                                   singleHDiffInfo.headInfo.cover_buf_size);
        }

        private static void GetNonSingleCompressedHDiffInfo(BinaryReader sr, TDirDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            if (!tDirDiffInfo.hdiffinfo.headerMagic.StartsWith("HDIFF")) throw new InvalidDataException("The header chunk magic is not valid!");
            byte magicVersion = TryGetVersion(tDirDiffInfo.hdiffinfo.headerMagic);

            if (magicVersion != 13) throw new InvalidDataException($"The header chunk format: v{magicVersion} is not supported!");

            tDirDiffInfo.hdiffinfo.headInfo.typesEndPos = sr.BaseStream.Position;
            tDirDiffInfo.newDataSize = sr.ReadLong7bit();
            tDirDiffInfo.oldDataSize = sr.ReadLong7bit();

            tDirDiffInfo.hdiffinfo.headInfo.coverCount = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.compressSizeBeginPos = sr.BaseStream.Position;
            tDirDiffInfo.hdiffinfo.headInfo.cover_buf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.rle_ctrlBuf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.rle_codeBuf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.newDataDiff_size = sr.ReadLong7bit();
            tDirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size = sr.ReadLong7bit();

            tDirDiffInfo.hdiffinfo.headInfo.headEndPos = sr.BaseStream.Position;
            tDirDiffInfo.compressedCount = ((tDirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_rle_ctrlBuf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_rle_codeBuf_size > 1) ? 1 : 0)
                                         + ((tDirDiffInfo.hdiffinfo.headInfo.compress_newDataDiff_size > 1) ? 1 : 0);

            tDirDiffInfo.hdiffinfo.headInfo.coverEndPos = tDirDiffInfo.hdiffinfo.headInfo.headEndPos
                                                        + (tDirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size > 0 ?
                                                           tDirDiffInfo.hdiffinfo.headInfo.compress_cover_buf_size :
                                                           tDirDiffInfo.hdiffinfo.headInfo.cover_buf_size);
        }

        private static bool IsSingleCompressedHDiff(BinaryReader sr, TDirDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            sr.BaseStream.Position = (long)headerInfo.hdiffDataOffset;
            string singleCompressedHeaderLine = sr.ReadStringToNull();
            string[] singleCompressedHeaderArr = singleCompressedHeaderLine.Split('&');

            if (singleCompressedHeaderArr[0].Equals("HDIFFSF20"))
            {
                // TODO
            }
            else
            {
                tDirDiffInfo.hdiffinfo = new CompressedHDiffInfo();
                tDirDiffInfo.hdiffinfo.headInfo = new THDiffzHead();

                if (singleCompressedHeaderArr[1] != "" && !Enum.TryParse(singleCompressedHeaderArr[1], true, out tDirDiffInfo.hdiffinfo.compMode)) throw new FormatException($"The compression chunk has unsupported compression: {singleCompressedHeaderArr[1]}");
                tDirDiffInfo.hdiffinfo.headerMagic = singleCompressedHeaderArr[0];
                return false;
            }

            return true;
        }

        private static void TryReadDirHeaderNumInfo(BinaryReader sr, TDirDiffInfo tDirDiffInfo, HDiffHeaderInfo headerInfo)
        {
            tDirDiffInfo.isInputDir = sr.ReadBoolean();
            tDirDiffInfo.isOutputDir = sr.ReadBoolean();

            headerInfo.inputDirCount = sr.ReadLong7bit();
            headerInfo.inputSumSize = sr.ReadLong7bit();

            headerInfo.outputDirCount = sr.ReadLong7bit();
            headerInfo.outputSumSize = sr.ReadLong7bit();

            headerInfo.inputRefFileCount = sr.ReadLong7bit();
            headerInfo.inputRefFileSize = sr.ReadLong7bit();

            headerInfo.outputRefFileCount = sr.ReadLong7bit();
            headerInfo.outputRefFileSize = sr.ReadLong7bit();

            headerInfo.sameFilePairCount = sr.ReadLong7bit();
            headerInfo.sameFileSize = sr.ReadLong7bit();

            headerInfo.newExecuteCount = sr.ReadInt7bit();
            headerInfo.privateReservedDataSize = sr.ReadLong7bit();
            headerInfo.privateExternDataSize = sr.ReadLong7bit();
            tDirDiffInfo.externDataSize = sr.ReadLong7bit();

            headerInfo.compressSizeBeginPos = sr.BaseStream.Position;

            headerInfo.headDataSize = sr.ReadLong7bit();
            headerInfo.headDataCompressedSize = sr.ReadLong7bit();
            tDirDiffInfo.checksumByteSize = (byte)sr.ReadLong7bit();

            tDirDiffInfo.checksumOffset = sr.BaseStream.Position;
            tDirDiffInfo.dirDataIsCompressed = headerInfo.headDataCompressedSize > 0;

            if (tDirDiffInfo.checksumByteSize > 0)
            {
                TrySeekHeader(sr, tDirDiffInfo.checksumByteSize * 4);
            }
        }

        private static void TrySeekHeader(BinaryReader sr, int skipLongSize)
        {
            int len = 4096;
            if (len > skipLongSize)
            {
                len = skipLongSize;
            }

            sr.BaseStream.Seek(len, SeekOrigin.Current);
        }

        private static byte TryGetVersion(string str)
        {
            string num = str.Substring(5);
            if (byte.TryParse(num, out byte ret)) return ret;

            throw new InvalidDataException($"Version string is invalid! Value: {num} (Raw: {str})");
        }
    }
}
