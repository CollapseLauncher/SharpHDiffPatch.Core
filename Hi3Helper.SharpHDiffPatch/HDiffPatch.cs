using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hi3Helper.SharpHDiffPatch
{
    public enum CompressionMode
    {
        nocomp,
        lzma,
        zstd
    }
    public enum ChecksumMode
    {
        nochecksum,
        crc32,
        fadler64
    }

    public class THDiffzHead
    {
        public ulong typesEndPos;
        public ulong coverCount;
        public ulong compressSizeBeginPos;
        public ulong cover_buf_size;
        public ulong compress_cover_buf_size;
        public ulong rle_ctrlBuf_size;
        public ulong compress_rle_ctrlBuf_size;
        public ulong rle_codeBuf_size;
        public ulong compress_rle_codeBuf_size;
        public ulong newDataDiff_size;
        public ulong compress_newDataDiff_size;
        public ulong headEndPos;
        public ulong coverEndPos;
    }

    public class TDirDiffInfo
    {
        public bool isInputDir;
        public bool isOutputDir;
        public bool isSingleCompressedDiff;

        public ulong externDataOffset;
        public ulong externDataSize;

        public byte checksumByteSize;
        public long checksumOffset;

        public bool dirDataIsCompressed;

        public ulong oldDataSize;
        public ulong newDataSize;
        public ulong compressedCount;

        public SingleCompressedHDiffInfo sdiffInfo;
        public CompressedHDiffInfo hdiffinfo;
    }

    public class SingleCompressedHDiffInfo
    {
        public CompressionMode compMode;
        public string headerMagic;
        public ulong stepMemSize;

        public THDiffzHead headInfo;
    }

    public class CompressedHDiffInfo
    {
        public CompressionMode compMode;
        public string patchPath;
        public string headerMagic;
        public ulong stepMemSize;
        public ulong compressedCount;
        public ulong oldDataSize;
        public ulong newDataSize;

        public THDiffzHead headInfo;
    }

    public class HDiffHeaderInfo
    {
        public CompressionMode compMode;
        public ChecksumMode checksumMode;

        public ulong inputDirCount;
        public ulong inputRefFileCount;
        public ulong inputRefFileSize;
        public ulong inputSumSize;


        public ulong outputDirCount;
        public ulong outputRefFileCount;
        public ulong outputRefFileSize;
        public ulong outputSumSize;

        public ulong sameFilePairCount;
        public ulong sameFileSize;

        public int newExecuteCount;

        public ulong privateReservedDataSize;
        public ulong privateExternDataSize;
        public ulong privateExternDataOffset;

        public long compressSizeBeginPos;

        public ulong headDataSize;
        public ulong headDataOffset;
        public ulong headDataCompressedSize;

        public ulong hdiffDataOffset;
        public ulong hdiffDataSize;
    }

    public sealed partial class HDiffPatch
    {
        internal static CancellationToken _token;
        internal static Stopwatch _patchStopwatch;

        private CompressedHDiffInfo singleHDiffInfo { get; set; }
        private TDirDiffInfo tDirDiffInfo { get; set; }
        private HDiffHeaderInfo headerInfo { get; set; }
        private Stream diffStream { get; set; }
        private string diffPath { get; set; }
        private bool isPatchDir { get; set; }

        internal static long currentSizePatched { get; set; }
        internal static long totalSizePatched { get; set; }

        internal static PatchEvent PatchEvent = new PatchEvent();
        public static EventListener Event = new EventListener();

        public HDiffPatch()
        {
            isPatchDir = true;
        }

        #region Header Initialization
        public void Initialize(string diff)
        {
            tDirDiffInfo = new TDirDiffInfo() { sdiffInfo = new SingleCompressedHDiffInfo() };
            headerInfo = new HDiffHeaderInfo();
            diffPath = diff;
            diffStream = new FileStream(diff, FileMode.Open, FileAccess.Read);

            using (BinaryReader sr = new BinaryReader(diffStream))
            {
                isPatchDir = Header.TryParseHeaderInfo(sr, this.diffPath, this.tDirDiffInfo, this.singleHDiffInfo, this.headerInfo);
            }
        }

        public void Patch(string inputPath, string outputPath, bool useBufferedPatch, CancellationToken token = default)
        {
            _patchStopwatch = Stopwatch.StartNew();
            _token = token;

            IPatch patcher = isPatchDir && tDirDiffInfo.isInputDir && tDirDiffInfo.isOutputDir ?
                new PatchDir(tDirDiffInfo, headerInfo, diffPath) :
                new PatchSingle(singleHDiffInfo);
            patcher.Patch(inputPath, outputPath, useBufferedPatch);

            _patchStopwatch.Stop();
        }
        #endregion

        public static void UpdateEvent(int read)
        {
            PatchEvent.UpdateEvent(currentSizePatched += read, totalSizePatched, read, _patchStopwatch.Elapsed.TotalSeconds);
            Event.PushEvent(PatchEvent);
        }
    }

    public class EventListener
    {
        // Log for external listener
        public static event EventHandler<PatchEvent> PatchEvent;
        // Push log to listener
        public void PushEvent(PatchEvent patchEvent) => PatchEvent?.Invoke(this, patchEvent);
    }
}
