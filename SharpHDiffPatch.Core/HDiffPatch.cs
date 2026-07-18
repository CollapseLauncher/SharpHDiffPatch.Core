using SharpHDiffPatch.Core.Event;
using SharpHDiffPatch.Core.Patch;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpHDiffPatch.Core.Binary.Compression;

namespace SharpHDiffPatch.Core;

public enum BufferMode { None, Partial, Full }
public enum Verbosity { Quiet, Info, Verbose, Debug }

public enum ChecksumMode
{
    NoChecksum,
    Crc32,
    FAdler64
}

public struct HeaderInfoExt
{
    public HeaderInfo HeaderInfo;
    public DataReferenceInfo DataReferenceInfo;
}

public struct HeaderInfo
{
    public HDiffCompressionMode CompMode;
    public ChecksumMode ChecksumMode;

    public bool IsInputDir;
    public bool IsOutputDir;
    public bool IsSingleCompressedDiff;
    public string PatchPath;
    public Func<Stream> PatchCreateStream;
    public string HeaderMagic;  

    public long StepMemSize;

    public bool DirDataIsCompressed;

    public long OldDataSize;
    public long NewDataSize;
    public long CompressedCount;

    public DiffSingleChunkInfo SingleChunkInfo;
    public DiffChunkInfo ChunkInfo;
}

public struct DataReferenceInfo
{
    public long InputDirCount;
    public long InputRefFileCount;
    public long InputRefFileSize;
    public long InputSumSize;

    public long OutputDirCount;
    public long OutputRefFileCount;
    public long OutputRefFileSize;
    public long OutputSumSize;

    public long SameFilePairCount;
    public long SameFileSize;

    public int NewExecuteCount;

    public long PrivateReservedDataSize;
    public long PrivateExternDataSize;
    public long PrivateExternDataOffset;

    public long ExternDataOffset;
    public long ExternDataSize;

    public long CompressSizeBeginPos;

    public byte ChecksumByteSize;
    public long ChecksumOffset;

    public long HeadDataSize;
    public long HeadDataOffset;
    public long HeadDataCompressedSize;

    public long HDiffDataOffset;
    public long HDiffDataSize;
}

public struct DiffSingleChunkInfo
{
    public long UncompressedSize;
    public long CompressedSize;

    public long DiffDataPos;
}

public struct DiffChunkInfo
{
    public long TypesEndPos;
    public long CoverCount;
    public long CompressSizeBeginPos;
    public long CoverBufSize;
    public long CompressCoverBufSize;
    public long RleCtrlBufSize;
    public long CompressRleCtrlBufSize;
    public long RleCodeBufSize;
    public long CompressRleCodeBufSize;
    public long NewDataDiffSize;
    public long CompressNewDataDiffSize;
    public long HeadEndPos;
    public long CoverEndPos;
}

public sealed class HDiffPatch
{
    private HeaderInfo        _headerInfo;
    private DataReferenceInfo ReferenceInfo { get; set; }
    private Stream            DiffStream    { get; set; }
    private bool              IsPatchDir    { get; set; } = true;

    internal static PatchEvent    PatchEvent = new();
    public static   EventListener Event      = new();

    public static Verbosity LogVerbosity { get; set; } = Verbosity.Quiet;

#region Header Initialization
    public void Initialize(string diff)
    {
        using (DiffStream = new FileStream(diff, FileMode.Open, FileAccess.Read))
        {
            IsPatchDir = Header.TryParseHeaderInfo(DiffStream, diff, out HeaderInfo info, out DataReferenceInfo reference);
            _headerInfo = info;
            ReferenceInfo = reference;
        }
    }

    public void Initialize(Func<Stream> diffCreateStream)
    {
        using (DiffStream = diffCreateStream())
        {
            IsPatchDir = Header.TryParseHeaderInfo(DiffStream, null, out HeaderInfo info, out DataReferenceInfo reference);
            _headerInfo = info;
            ReferenceInfo = reference;
            _headerInfo.PatchCreateStream = diffCreateStream;
        }
    }

    public void Patch(string inputPath, string outputPath, bool useBufferedPatch, CancellationToken token = default, bool useFullBuffer = false, bool useFastBuffer = false)
        => Patch(inputPath, outputPath, useBufferedPatch, null, token, useFullBuffer, useFastBuffer);

    public void Patch(string inputPath, string outputPath, bool useBufferedPatch, Action<long> writeBytesDelegate, CancellationToken token = default, bool useFullBuffer = false, bool useFastBuffer = false)
    {
        IPatch patcher;
        if (IsPatchDir && _headerInfo is { IsInputDir: true, IsOutputDir: true })
        {
            patcher = new PatchDir(_headerInfo, ReferenceInfo, _headerInfo.PatchPath, token);
        }
        else
        {
            patcher = new PatchSingle(_headerInfo, token);
        }
        patcher.Patch(inputPath, outputPath, writeBytesDelegate, useBufferedPatch, useFullBuffer, useFastBuffer);
    }
#endregion

    internal static void DisplayDirPatchInformation(long oldFileSize, long newFileSize, HeaderInfo headerInfo)
    {
        Event.PushLog("Patch Information:");
        Event.PushLog($"    Size -> Old: {oldFileSize} bytes | New: {newFileSize} bytes");
        Event.PushLog("Technical Information:");
        if (!headerInfo.IsSingleCompressedDiff)
        {
            Event.PushLog($"    Cover Data -> Count: {headerInfo.ChunkInfo.CoverCount} | Offset: {headerInfo.ChunkInfo.HeadEndPos} | Size: {headerInfo.ChunkInfo.CoverBufSize}");
            Event.PushLog($"    RLE Data -> Offset: {headerInfo.ChunkInfo.CoverEndPos} | Control: {headerInfo.ChunkInfo.RleCtrlBufSize} | Code: {headerInfo.ChunkInfo.RleCodeBufSize}");
            Event.PushLog($"    Diff Data -> Size: {headerInfo.ChunkInfo.NewDataDiffSize}");
        }
        else
        {
            Event.PushLog($"    Cover Data -> Count: {headerInfo.ChunkInfo.CoverCount} | DiffDataPos: {headerInfo.SingleChunkInfo.DiffDataPos}");
            Event.PushLog($"    RLE Data -> Compressed Size: {headerInfo.SingleChunkInfo.CompressedSize} | Size: {headerInfo.SingleChunkInfo.UncompressedSize}");
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

    public static long GetHDiffNewSize(string diffFilePath)
    {
        HeaderInfoExt headerInfo = GetHDiffHeaderInfo(diffFilePath);
        return headerInfo.HeaderInfo.NewDataSize;
    }

    public static long GetHDiffOldSize(string diffFilePath)
    {
        HeaderInfoExt headerInfo = GetHDiffHeaderInfo(diffFilePath);
        return headerInfo.HeaderInfo.OldDataSize;
    }

    public static long GetHDiffNewSize(Stream diffStream)
    {
        HeaderInfoExt headerInfo = GetHDiffHeaderInfo(diffStream);
        return headerInfo.HeaderInfo.NewDataSize;
    }

    public static long GetHDiffOldSize(Stream diffStream)
    {
        HeaderInfoExt headerInfo = GetHDiffHeaderInfo(diffStream);
        return headerInfo.HeaderInfo.OldDataSize;
    }

    public static HeaderInfoExt GetHDiffHeaderInfo(string diffFilePath)
    {
        using FileStream fs = new(diffFilePath, FileMode.Open, FileAccess.Read);
        _ = Header.TryParseHeaderInfo(fs, diffFilePath, out HeaderInfo headerInfo, out DataReferenceInfo headerInfoReference);
        return new HeaderInfoExt { HeaderInfo = headerInfo, DataReferenceInfo = headerInfoReference };
    }

    public static HeaderInfoExt GetHDiffHeaderInfo(Stream diffStream)
    {
        _ = Header.TryParseHeaderInfo(diffStream, null, out HeaderInfo headerInfo, out DataReferenceInfo headerInfoReference);
        return new HeaderInfoExt { HeaderInfo = headerInfo, DataReferenceInfo = headerInfoReference };
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
