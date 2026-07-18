using SharpHDiffPatch.Core.Binary;
using System;
using System.IO;

namespace SharpHDiffPatch.Core.Patch
{
    internal sealed class Header
    {
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        private static readonly char[] HdiffHead = ['H', 'D', 'I', 'F', 'F'];
#else
        private const string HdiffHead = "HDIFF";
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
                .StartsWith(HdiffHead)
                ) throw new FormatException("[Header::TryParseHeaderInfo] This is not a HDiff file format!");

            string[] hInfoArr = headerInfoLine.Split('&');
            if (hInfoArr.Length == 2)
            {
                byte pFileVer = TryGetVersion(hInfoArr[0]);
                if (pFileVer != 13) throw new FormatException("[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the single patch file with version: 13 only!");

                isPatchDir = false;

                headerInfo.HeaderMagic = hInfoArr[0];

                Enum.TryParse(hInfoArr[1], true, out headerInfo.CompMode);
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {pFileVer} Compression: {headerInfo.CompMode}", Verbosity.Debug);
            }
            else if (hInfoArr.Length != 3) throw new IndexOutOfRangeException($"[Header::TryParseHeaderInfo] Header info is incomplete! Expecting 3 parts but got {hInfoArr.Length} part(s) instead (Raw: {headerInfoLine})");

            if (isPatchDir)
            {
                byte hInfoVer = TryGetVersion(hInfoArr[0]);
                if (hInfoVer != 19) throw new FormatException("[Header::TryParseHeaderInfo] HDiff version is unsupported. This patcher only supports the directory patch file with version: 19 only!");

                if (hInfoArr[1] != "" && !Enum.TryParse(hInfoArr[1], true, out headerInfo.CompMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[1]} compression!");
                if (string.IsNullOrEmpty(hInfoArr[2])) headerInfo.ChecksumMode = ChecksumMode.NoChecksum;
                else if (!Enum.TryParse(hInfoArr[2], true, out headerInfo.ChecksumMode)) throw new FormatException($"[Header::TryParseHeaderInfo] This patcher doesn't support {hInfoArr[2]} checksum!");
                HDiffPatch.Event.PushLog($"[Header::TryParseHeaderInfo] Version: {hInfoVer} ChecksumMode: {headerInfo.ChecksumMode} Compression: {headerInfo.CompMode}", Verbosity.Debug);

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
            referenceInfo.HeadDataOffset = curPos;

            curPos += referenceInfo.HeadDataCompressedSize > 0 ? referenceInfo.HeadDataCompressedSize : referenceInfo.HeadDataSize;
            referenceInfo.PrivateExternDataOffset = curPos;

            curPos += referenceInfo.PrivateExternDataSize;
            referenceInfo.ExternDataOffset = curPos;

            curPos += referenceInfo.ExternDataSize;
            referenceInfo.HDiffDataOffset = curPos;
            referenceInfo.HDiffDataSize = sr.Length - curPos;

            HDiffPatch.Event.PushLog($"[Header::TryReadExternReferenceInfo] headDataOffset: {referenceInfo.HeadDataOffset} | privateExternDataOffset: {referenceInfo.PrivateExternDataOffset} | externDataOffset: {referenceInfo.ExternDataOffset} | hdiffDataOffset: {referenceInfo.HDiffDataOffset} | hdiffDataSize: {referenceInfo.HDiffDataSize}", Verbosity.Debug);

            TryIdentifyDiffType(sr, diffPath, ref headerInfo, ref referenceInfo);
        }

        private static void TryIdentifyDiffType(Stream sr, string diffPath, ref HeaderInfo headerInfo, ref DataReferenceInfo referenceInfo)
        {
            sr.Position = referenceInfo.HDiffDataOffset;
            string singleCompressedHeaderLine = sr.ReadStringToNull();
            string[] singleCompressedHeaderArr = singleCompressedHeaderLine.Split('&');

            // ReSharper disable once AssignmentInConditionalExpression
            if (headerInfo.IsSingleCompressedDiff =
#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
                singleCompressedHeaderArr[0].AsSpan() is "HDIFFSF20")
#else
                singleCompressedHeaderArr[0] == "HDIFFSF20")
#endif
            {
                TryReadSingleFileHeaderInfo(sr, diffPath, ref headerInfo, referenceInfo);
                return;
            }

            HDiffPatch.Event.PushLog($"[Header::TryIdentifyDiffType] HDIFF Dir Signature: {singleCompressedHeaderLine}", Verbosity.Debug);

            if (singleCompressedHeaderArr[1] != "" && !Enum.TryParse(singleCompressedHeaderArr[1], true, out headerInfo.CompMode)) throw new FormatException($"[Header::TryIdentifyDiffType] The compression chunk has unsupported compression: {singleCompressedHeaderArr[1]}");
            headerInfo.HeaderMagic = singleCompressedHeaderArr[0];

            TryReadNonSingleFileHeaderInfo(sr, diffPath, ref headerInfo);
        }

        private static void TryReadSingleFileHeaderInfo(Stream sr, string diffPath, ref HeaderInfo headerInfo, DataReferenceInfo referenceInfo)
        {
            headerInfo.PatchPath = diffPath;
            headerInfo.SingleChunkInfo = new DiffSingleChunkInfo();

            headerInfo.NewDataSize = sr.ReadLong7Bit();
            headerInfo.OldDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadSingleFileHeaderInfo] oldDataSize: {headerInfo.OldDataSize} | newDataSize: {headerInfo.NewDataSize}", Verbosity.Debug);

            headerInfo.ChunkInfo.CoverCount = sr.ReadLong7Bit();
            headerInfo.StepMemSize = sr.ReadLong7Bit();
            headerInfo.SingleChunkInfo.UncompressedSize = sr.ReadLong7Bit();
            headerInfo.SingleChunkInfo.CompressedSize = sr.ReadLong7Bit();
            headerInfo.SingleChunkInfo.DiffDataPos = sr.Position - referenceInfo.HDiffDataOffset;

            headerInfo.CompressedCount = headerInfo.SingleChunkInfo.CompressedSize > 0 ? 1 : 0;

            HDiffPatch.Event.PushLog($"[Header::TryReadSingleFileHeaderInfo] compressedCount: {headerInfo.CompressedCount}", Verbosity.Debug);
        }

        private static void TryReadNonSingleFileHeaderInfo(Stream sr, string diffPath, ref HeaderInfo headerInfo)
        {
            headerInfo.PatchPath = diffPath;

            long typeEndPos = sr.Position;
            headerInfo.NewDataSize = sr.ReadLong7Bit();
            headerInfo.OldDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadNonSingleFileHeaderInfo] oldDataSize: {headerInfo.OldDataSize} | newDataSize: {headerInfo.NewDataSize}", Verbosity.Debug);

            GetDiffChunkInfo(sr, out headerInfo.ChunkInfo, typeEndPos);

            headerInfo.CompressedCount = (headerInfo.ChunkInfo.CompressCoverBufSize > 1 ? 1 : 0)
                                       + (headerInfo.ChunkInfo.CompressRleCtrlBufSize > 1 ? 1 : 0)
                                       + (headerInfo.ChunkInfo.CompressRleCodeBufSize > 1 ? 1 : 0)
                                       + (headerInfo.ChunkInfo.CompressNewDataDiffSize > 1 ? 1 : 0);

            HDiffPatch.Event.PushLog($"[Header::TryReadNonSingleFileHeaderInfo] compressedCount: {headerInfo.CompressedCount}", Verbosity.Debug);
        }

        private static void GetDiffChunkInfo(Stream sr, out DiffChunkInfo chunkInfo, long typeEndPos)
        {
            chunkInfo = new DiffChunkInfo();

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] typesEndPos: {typeEndPos}", Verbosity.Debug);

            chunkInfo.CoverCount = sr.ReadLong7Bit();
            chunkInfo.CompressSizeBeginPos = sr.Position;

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] coverCount: {chunkInfo.CoverCount} | compressSizeBeginPos: {chunkInfo.CompressSizeBeginPos}", Verbosity.Debug);

            chunkInfo.CoverBufSize = sr.ReadLong7Bit();
            chunkInfo.CompressCoverBufSize = sr.ReadLong7Bit();
            chunkInfo.RleCtrlBufSize = sr.ReadLong7Bit();
            chunkInfo.CompressRleCtrlBufSize = sr.ReadLong7Bit();
            chunkInfo.RleCodeBufSize = sr.ReadLong7Bit();
            chunkInfo.CompressRleCodeBufSize = sr.ReadLong7Bit();
            chunkInfo.NewDataDiffSize = sr.ReadLong7Bit();
            chunkInfo.CompressNewDataDiffSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] cover_buf_size: {chunkInfo.CoverBufSize} |  compress_cover_buf_size: {chunkInfo.CompressCoverBufSize}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] rle_ctrlBuf_size: {chunkInfo.RleCtrlBufSize} | compress_rle_ctrlBuf_size: {chunkInfo.CompressRleCtrlBufSize}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] rle_codeBuf_size: {chunkInfo.RleCodeBufSize} | compress_rle_codeBuf_size: {chunkInfo.CompressRleCodeBufSize}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] newDataDiff_size: {chunkInfo.NewDataDiffSize} | compress_newDataDiff_size: {chunkInfo.CompressNewDataDiffSize}", Verbosity.Debug);

            chunkInfo.HeadEndPos = sr.Position;
            chunkInfo.CoverEndPos = chunkInfo.HeadEndPos +
                                   (chunkInfo.CompressCoverBufSize > 0 ?
                                    chunkInfo.CompressCoverBufSize :
                                    chunkInfo.CoverBufSize);

            HDiffPatch.Event.PushLog($"[Header::GetDiffChunkInfo] headEndPos: {chunkInfo.HeadEndPos} | coverEndPos: {chunkInfo.CoverEndPos}", Verbosity.Debug);
        }

        private static void TryReadHeaderAndReferenceInfo(Stream sr, ref HeaderInfo headerInfo, ref DataReferenceInfo referenceInfo)
        {
            headerInfo.IsInputDir = sr.ReadBoolean();
            headerInfo.IsOutputDir = sr.ReadBoolean();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] Is In/Out a Dir -> Input: {headerInfo.IsInputDir} / Output: {headerInfo.IsOutputDir}", Verbosity.Debug);

            referenceInfo.InputDirCount = sr.ReadLong7Bit();
            referenceInfo.InputSumSize = sr.ReadLong7Bit();

            referenceInfo.OutputDirCount = sr.ReadLong7Bit();
            referenceInfo.OutputSumSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] InDir Count/SumSize: {referenceInfo.InputDirCount}/{referenceInfo.InputSumSize} | OutDir Count/SumSize: {referenceInfo.OutputSumSize}/{referenceInfo.InputSumSize}", Verbosity.Debug);

            referenceInfo.InputRefFileCount = sr.ReadLong7Bit();
            referenceInfo.InputRefFileSize = sr.ReadLong7Bit();

            referenceInfo.OutputRefFileCount = sr.ReadLong7Bit();
            referenceInfo.OutputRefFileSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] InRef Count/Size: {referenceInfo.InputRefFileCount}/{referenceInfo.InputRefFileSize} | OutRef Count/Size: {referenceInfo.OutputRefFileCount}/{referenceInfo.OutputRefFileSize}", Verbosity.Debug);

            referenceInfo.SameFilePairCount = sr.ReadLong7Bit();
            referenceInfo.SameFileSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] IdenticalPair Count/Size: {referenceInfo.SameFilePairCount}/{referenceInfo.SameFileSize}", Verbosity.Debug);

            referenceInfo.NewExecuteCount = sr.ReadInt7Bit();
            referenceInfo.PrivateReservedDataSize = sr.ReadLong7Bit();
            referenceInfo.PrivateExternDataSize = sr.ReadLong7Bit();
            referenceInfo.ExternDataSize = sr.ReadLong7Bit();

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] newExecuteCount: {referenceInfo.NewExecuteCount} | privateReservedDataSize: {referenceInfo.PrivateReservedDataSize} | privateExternDataSize: {referenceInfo.PrivateExternDataSize} | privateExternDataSize: {referenceInfo.ExternDataSize}", Verbosity.Debug);

            referenceInfo.CompressSizeBeginPos = sr.Position;

            referenceInfo.HeadDataSize = sr.ReadLong7Bit();
            referenceInfo.HeadDataCompressedSize = sr.ReadLong7Bit();
            referenceInfo.ChecksumByteSize = (byte)sr.ReadLong7Bit();
            headerInfo.DirDataIsCompressed = referenceInfo.HeadDataCompressedSize > 0;
            referenceInfo.ChecksumOffset = sr.Position;

            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] compressSizeBeginPos: {referenceInfo.CompressSizeBeginPos} | headDataSize: {referenceInfo.HeadDataSize} | headDataCompressedSize: {referenceInfo.HeadDataCompressedSize} | checksumByteSize: {referenceInfo.ChecksumByteSize}", Verbosity.Debug);
            HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] checksumOffset: {referenceInfo.ChecksumOffset} | dirDataIsCompressed: {headerInfo.DirDataIsCompressed}", Verbosity.Debug);

            if (referenceInfo.ChecksumByteSize > 0)
            {
                HDiffPatch.Event.PushLog($"[Header::TryReadHeaderAndReferenceInfo] Seeking += {referenceInfo.ChecksumByteSize * 4} bytes from checksum bytes!", Verbosity.Debug);
                TrySeekHeader(sr, referenceInfo.ChecksumByteSize * 4);
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
            int lastIndexOf = str.IndexOf(HdiffHead);
            if (lastIndexOf < 0) throw new IndexOutOfRangeException($"[Header::TryGetVersion] Version string is invalid! Cannot find the matching start of \"HDIFF\". Getting: {str.ToString()} instead");

            ReadOnlySpan<char> numSpan = str.Slice(lastIndexOf + HdiffHead.Length);
            if (byte.TryParse(numSpan, out byte ret)) return ret;

            throw new InvalidDataException($"[Header::TryGetVersion] Version string is invalid! Value: {numSpan.ToString()} (Raw: {str.ToString()})");
        }
#else
        private static byte TryGetVersion(string str)
        {
            int lastIndexOf = str.IndexOf(HdiffHead, StringComparison.OrdinalIgnoreCase);
            if (lastIndexOf < 0) throw new IndexOutOfRangeException($"[Header::TryGetVersion] Version string is invalid! Cannot find the matching start of \"HDIFF\". Getting: {str} instead");

            string numStr = str.Substring(lastIndexOf + HdiffHead.Length);
            if (byte.TryParse(numStr, out byte ret)) return ret;

            throw new InvalidDataException($"[Header::TryGetVersion] Version string is invalid! Value: {numStr} (Raw: {str})");
        }
#endif
    }
}
