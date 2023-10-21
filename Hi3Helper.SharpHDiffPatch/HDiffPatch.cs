using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hi3Helper.SharpHDiffPatch
{
    public enum BufferMode { None, Partial, Full }
    public enum Verbosity { Quiet, Info, Verbose, Debug }

    public enum CompressionMode
    {
        nocomp,
        zstd,
        lzma2,
        zlib,
        bz2,
        pbz2
    }

    public enum ChecksumMode
    {
        nochecksum,
        crc32,
        fadler64
    }

    public class HDiffHeaderInfo
    {
        public CompressionMode compMode;
        public ChecksumMode checksumMode;

        public long inputDirCount;
        public long inputRefFileCount;
        public long inputRefFileSize;
        public long inputSumSize;

        public long outputDirCount;
        public long outputRefFileCount;
        public long outputRefFileSize;
        public long outputSumSize;

        public long sameFilePairCount;
        public long sameFileSize;

        public int newExecuteCount;

        public long privateReservedDataSize;
        public long privateExternDataSize;
        public long privateExternDataOffset;

        public long compressSizeBeginPos;

        public long headDataSize;
        public long headDataOffset;
        public long headDataCompressedSize;

        public long hdiffDataOffset;
        public long hdiffDataSize;
    }

    public class DirectoryHDiffInfo
    {
        public bool isInputDir;
        public bool isOutputDir;
        public bool isSingleCompressedDiff;

        public long externDataOffset;
        public long externDataSize;

        public byte checksumByteSize;
        public long checksumOffset;

        public bool dirDataIsCompressed;

        public long oldDataSize;
        public long newDataSize;
        public long compressedCount;

        public HDiffInfo hdiffinfo;
    }

    public class HDiffInfo
    {
        public CompressionMode compMode;
        public string patchPath;
        public string headerMagic;
        public long stepMemSize;
        public long compressedCount;
        public long oldDataSize;
        public long newDataSize;

        public HDiffDataInfo headInfo;
    }

    public class HDiffDataInfo
    {
        public long typesEndPos;
        public long coverCount;
        public long compressSizeBeginPos;
        public long cover_buf_size;
        public long compress_cover_buf_size;
        public long rle_ctrlBuf_size;
        public long compress_rle_ctrlBuf_size;
        public long rle_codeBuf_size;
        public long compress_rle_codeBuf_size;
        public long newDataDiff_size;
        public long compress_newDataDiff_size;
        public long headEndPos;
        public long coverEndPos;
    }

    public sealed partial class HDiffPatch
    {
        private HDiffInfo singleHDiffInfo { get; set; }
        private DirectoryHDiffInfo tDirDiffInfo { get; set; }
        private HDiffHeaderInfo headerInfo { get; set; }
        private Stream diffStream { get; set; }
        private string diffPath { get; set; }
        private bool isPatchDir { get; set; }

        internal long currentSizePatched { get; set; }
        internal long totalSizePatched { get; set; }

        internal static PatchEvent PatchEvent = new PatchEvent();
        public static EventListener Event = new EventListener();
        public static Verbosity LogVerbosity = Verbosity.Quiet;

        public HDiffPatch()
        {
            isPatchDir = true;
        }

        #region Header Initialization
        public void Initialize(string diff)
        {
            diffPath = diff;

            using (diffStream = new FileStream(diff, FileMode.Open, FileAccess.Read))
            {
                isPatchDir = Header.TryParseHeaderInfo(diffStream, diffPath, out DirectoryHDiffInfo _tDirDiffInfo, out HDiffInfo _singleHDiffInfo, out HDiffHeaderInfo _headerInfo);

                headerInfo = _headerInfo;
                singleHDiffInfo = _singleHDiffInfo;
                tDirDiffInfo = _tDirDiffInfo;
            }
        }

        public void Patch(string inputPath, string outputPath, bool useBufferedPatch, CancellationToken token = default, bool useFullBuffer = false, bool useFastBuffer = false)
        {
            IPatch patcher = isPatchDir && tDirDiffInfo.isInputDir && tDirDiffInfo.isOutputDir ?
                new PatchDir(tDirDiffInfo, headerInfo, diffPath, token) :
                new PatchSingle(singleHDiffInfo, token);
            patcher.Patch(inputPath, outputPath, useBufferedPatch, useFullBuffer, useFastBuffer);
        }
        #endregion

        internal static void DisplayDirPatchInformation(long oldFileSize, long newFileSize, HDiffDataInfo dataInfo)
        {
            Event.PushLog("Patch Information:");
            Event.PushLog($"    Size -> Old: {oldFileSize} bytes | New: {newFileSize} bytes");
            Event.PushLog("Technical Information:");
            Event.PushLog($"    Cover Data -> Count: {dataInfo.coverCount} | Offset: {dataInfo.headEndPos} | Size: {dataInfo.cover_buf_size}");
            Event.PushLog($"    RLE Data -> Offset: {dataInfo.coverEndPos} | Control: {dataInfo.rle_ctrlBuf_size} | Code: {dataInfo.rle_codeBuf_size}");
            Event.PushLog($"    Diff Data -> Size: {dataInfo.newDataDiff_size}");
        }

        internal static void UpdateEvent(long read, ref long currentSizePatched, ref long totalSizePatched, Stopwatch patchStopwatch)
        {
            lock (PatchEvent)
            {
                PatchEvent.UpdateEvent(currentSizePatched += read, totalSizePatched, read, patchStopwatch.Elapsed.TotalSeconds);
                Event.PushEvent(PatchEvent);
            }
        }

        public static long GetHDiffNewSize(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            bool isDirPatch = Header.TryParseHeaderInfo(fs, path, out DirectoryHDiffInfo _tDirDiffInfo, out HDiffInfo _singleHDiffInfo, out HDiffHeaderInfo _headerInfo);
            return (isDirPatch ? _tDirDiffInfo.newDataSize : _singleHDiffInfo.newDataSize);
        }
    }

    public class EventListener
    {
        // Log for external listener
        public static event EventHandler<PatchEvent> PatchEvent;
        public static event EventHandler<LoggerEvent> LoggerEvent;
        public void PushEvent(PatchEvent patchEvent) => PatchEvent?.Invoke(this, patchEvent);
        public void PushLog(in string message, Verbosity logLevel = Verbosity.Info)
        {
            if (logLevel != Verbosity.Quiet)
                LoggerEvent?.Invoke(this, new LoggerEvent(message, logLevel));
        }
    }
}
