using SharpHDiffPatch.Core.Event;
using SharpHDiffPatch.Core.Patch;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpHDiffPatch.Core.Binary.Compression;

namespace SharpHDiffPatch.Core
{
    public enum BufferMode { None, Partial, Full }
    public enum Verbosity { Quiet, Info, Verbose, Debug }

    public enum ChecksumMode
    {
        nochecksum,
        crc32,
        fadler64
    }

    public struct HeaderInfo
    {
        public CompressionMode compMode;
        public ChecksumMode checksumMode;

        public bool isInputDir;
        public bool isOutputDir;
        public bool isSingleCompressedDiff;
        public string patchPath;
        public string headerMagic;

        public long stepMemSize;

        public bool dirDataIsCompressed;

        public long oldDataSize;
        public long newDataSize;
        public long compressedCount;

        public DiffSingleChunkInfo singleChunkInfo;
        public DiffChunkInfo chunkInfo;
    }

    public struct DataReferenceInfo
    {
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

        public long externDataOffset;
        public long externDataSize;

        public long compressSizeBeginPos;

        public byte checksumByteSize;
        public long checksumOffset;

        public long headDataSize;
        public long headDataOffset;
        public long headDataCompressedSize;

        public long hdiffDataOffset;
        public long hdiffDataSize;
    }

    public struct DiffSingleChunkInfo
    {
        public long uncompressedSize;
        public long compressedSize;

        public long diffDataPos;
    }

    public struct DiffChunkInfo
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
        private HeaderInfo headerInfo { get; set; }
        private DataReferenceInfo referenceInfo { get; set; }
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
                isPatchDir = Header.TryParseHeaderInfo(diffStream, diffPath, out HeaderInfo headerInfo, out DataReferenceInfo referenceInfo);

                this.headerInfo = headerInfo;
                this.referenceInfo = referenceInfo;
            }
        }

        public void Patch(string inputPath, string outputPath, bool useBufferedPatch, CancellationToken token = default, bool useFullBuffer = false, bool useFastBuffer = false
#if USEEXPERIMENTALMULTITHREAD
            , bool useMultiThread = false
#endif
            )
        {
            IPatch patcher;
            if (isPatchDir && headerInfo.isInputDir && headerInfo.isOutputDir) patcher = new PatchDir(headerInfo, referenceInfo, diffPath, token
#if USEEXPERIMENTALMULTITHREAD
            useMultiThread
#endif
            );
            else patcher = new PatchSingle(headerInfo, token);
            patcher.Patch(inputPath, outputPath, useBufferedPatch, useFullBuffer, useFastBuffer);
        }
#endregion

        internal static void DisplayDirPatchInformation(long oldFileSize, long newFileSize, HeaderInfo headerInfo)
        {
            Event.PushLog("Patch Information:");
            Event.PushLog($"    Size -> Old: {oldFileSize} bytes | New: {newFileSize} bytes");
            Event.PushLog("Technical Information:");
            if (!headerInfo.isSingleCompressedDiff)
            {
                Event.PushLog($"    Cover Data -> Count: {headerInfo.chunkInfo.coverCount} | Offset: {headerInfo.chunkInfo.headEndPos} | Size: {headerInfo.chunkInfo.cover_buf_size}");
                Event.PushLog($"    RLE Data -> Offset: {headerInfo.chunkInfo.coverEndPos} | Control: {headerInfo.chunkInfo.rle_ctrlBuf_size} | Code: {headerInfo.chunkInfo.rle_codeBuf_size}");
                Event.PushLog($"    Diff Data -> Size: {headerInfo.chunkInfo.newDataDiff_size}");
            }
            else
            {
                Event.PushLog($"    Cover Data -> Count: {headerInfo.chunkInfo.coverCount} | DiffDataPos: {headerInfo.singleChunkInfo.diffDataPos}");
                Event.PushLog($"    RLE Data -> Compressed Size: {headerInfo.singleChunkInfo.compressedSize} | Size: {headerInfo.singleChunkInfo.uncompressedSize}");
            }
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
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                bool isDirPatch = Header.TryParseHeaderInfo(fs, path, out HeaderInfo headerInfo, out DataReferenceInfo _headerInfo);
                return headerInfo.newDataSize;
            }
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
