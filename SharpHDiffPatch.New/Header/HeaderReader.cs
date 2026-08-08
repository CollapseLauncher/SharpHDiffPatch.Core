using SharpHDiffPatch.New.Extension;
using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHDiffPatch.New.IO.Compression;
using SharpHDiffPatch.New.IO.Reader;
using SharpHDiffPatch.New.Header.Metadata;

namespace SharpHDiffPatch.New.Header;

public class HeaderReader
{
    private delegate ref PatchMetadata PatchMetadataAllocator(ref HDiffInfo info);

#if NET8_0_OR_GREATER
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)], EntryPoint = "Extent_HeaderReader_ReadHeaderSignature")]
    public static unsafe int ReadHeaderSignature_Extent(void* wSignP, int wSignLen, HDiffInfo* signP)
    {
        try
        {
            ReadOnlySpan<char> signature = new(wSignP, wSignLen);
            ReadHeaderSignature(signature, ref signP[0]);
            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }
#endif

    public static void ReadHeaderSignature(ReadOnlySpan<char> signature, ref HDiffInfo info)
    {
        ReadBasicHeaderSignature(signature,
                                 out info.MagicType,
                                 out info.CompressionType,
                                 out info.ChecksumType);
    }

    private static void ReadBasicHeaderSignature(
        ReadOnlySpan<char>   signature,
        out HDiffMagic       magicType,
        out HDiffCompression compressionType,
        out HDiffChecksum    checksumType)
    {
        Unsafe.SkipInit(out magicType);
        Unsafe.SkipInit(out compressionType);
        Unsafe.SkipInit(out checksumType);

        Span<Range> ranges = stackalloc Range[4];
        int signatureSplits = signature.GetSplits(ranges, '&');

        if (signature.IsEmpty ||
            signatureSplits == 0)
        {
            ExceptionHelper.ThrowHDiffHeaderSignatureEmptyOrUnreadable();
        }

#if !NET6_0_OR_GREATER
        string magicSpan           = signature[ranges[0]].ToString();
        string compressionTypeSpan = signature[ranges[1]].ToString();
        string checksumTypeSpan    = signature[ranges[2]].ToString();
#else
        ReadOnlySpan<char> magicSpan           = signature[ranges[0]];
        ReadOnlySpan<char> compressionTypeSpan = signature[ranges[1]];
        ReadOnlySpan<char> checksumTypeSpan    = signature[ranges[2]];
#endif

        if (!Enum.TryParse(magicSpan, true, out magicType))
        {
            ExceptionHelper.ThrowHdiffHeaderMagicNotSupported(magicSpan);
        }

        // Parse HDIFF19 (Directory Patch) enums
        if (magicType == HDiffMagic.HDiff19)
        {
            if (compressionTypeSpan.Length != 0 &&
                !Enum.TryParse(compressionTypeSpan, true, out compressionType))
            {
                ExceptionHelper.ThrowHdiffHeaderCompressionNotSupported(compressionTypeSpan);
            }

            if (checksumTypeSpan.Length != 0 &&
                !Enum.TryParse(checksumTypeSpan, true, out checksumType))
            {
                ExceptionHelper.ThrowHdiffHeaderChecksumNotSupported(checksumTypeSpan);
            }

            return;
        }

        // Parse HDIFF13 (Single Patch) enums
        if (magicType == HDiffMagic.HDiff13)
        {
            if (compressionTypeSpan.Length != 0 &&
                !Enum.TryParse(compressionTypeSpan, true, out compressionType))
            {
                ExceptionHelper.ThrowHdiffHeaderCompressionNotSupported(compressionTypeSpan);
            }

            return;
        }

        ExceptionHelper.ThrowHdiffHeaderMagicNotSupported(magicSpan);
    }

    internal static void ReadHDiffHeaderMetadata(
        ref HDiffInfo        info,
        BittableStreamReader streamReader,
        CreateStream         additionalStreamCreate)
    {
        PatchMetadataAllocator patchMetadataAllocator = DefaultPatchMetadataAllocator;
        switch (info.MagicType)
        {
            case HDiffMagic.HDiff19:
                ReadHDiff19HeaderInfoCore(ref info, streamReader, additionalStreamCreate);
                patchMetadataAllocator = HDiff19PatchMetadataAllocator;
                // Continue reading HDiff13 data section.
                goto case HDiffMagic.HDiff13;
            case HDiffMagic.HDiff13:
                ReadHDiff13HeaderInfoCore(ref info, streamReader, patchMetadataAllocator);
                break;
        }
    }

    internal static async Task<HDiffInfo> ReadHDiffHeaderMetadataAsync(
        HDiffInfo            info,
        BittableStreamReader streamReader,
        CreateStreamAsync    additionalStreamCreate,
        CancellationToken    token)
    {
        PatchMetadataAllocator patchMetadataAllocator = DefaultPatchMetadataAllocator;
        switch (info.MagicType)
        {
            case HDiffMagic.HDiff19:
                info = await ReadHDiff19HeaderInfoAsyncCore(info,
                                                            streamReader,
                                                            additionalStreamCreate,
                                                            token);

                patchMetadataAllocator = HDiff19PatchMetadataAllocator;
                // Continue reading HDiff13 data section.
                goto case HDiffMagic.HDiff13;
            case HDiffMagic.HDiff13:
                info = await ReadHDiff13HeaderInfoAsyncCore(info,
                                                            streamReader,
                                                            patchMetadataAllocator,
                                                            token);
                break;
        }

        return info;
    }

    private static ref PatchMetadata DefaultPatchMetadataAllocator(ref HDiffInfo info)
        => ref info.AllocMetadata<PatchMetadata>();

    private static unsafe ref PatchMetadata HDiff19PatchMetadataAllocator(ref HDiffInfo info)
    {
        PatchMetadata* patchMetadata = MemoryAlloc.Alloc<PatchMetadata>();
        patchMetadata->Init();

        ref HeaderDirectoryPatchMetadata dirPatchMetadata = ref info.MetadataAs<HeaderDirectoryPatchMetadata>();
        dirPatchMetadata.PatchMetadataP = patchMetadata;
        return ref Unsafe.AsRef<PatchMetadata>(patchMetadata);
    }

    private static unsafe void ReadHDiff19HeaderInfoCore(
        ref HDiffInfo        info,
        BittableStreamReader streamReader,
        CreateStream         additionalStreamCreate)
    {
        byte isInputDir  = streamReader.ReadByte();
        byte isOutputDir = streamReader.ReadByte();

        int  inputPathEntryCount       = (int)streamReader.ReadLong7Bit();
        long inputPathEntryBufferSize  = streamReader.ReadLong7Bit();
        int  outputPathEntryCount      = (int)streamReader.ReadLong7Bit();
        long outputPathEntryBufferSize = streamReader.ReadLong7Bit();

        int  inputRefFileCount        = (int)streamReader.ReadLong7Bit();
        long inputPathEntryTotalSize  = streamReader.ReadLong7Bit();
        int  outputRefFileCount       = (int)streamReader.ReadLong7Bit();
        long outputPathEntryTotalSize = streamReader.ReadLong7Bit();

        int  sameFilePathEntryCount     = (int)streamReader.ReadLong7Bit();
        long sameFilePathEntryTotalSize = streamReader.ReadLong7Bit();

        int  newExecuteCount         = streamReader.ReadInt7Bit();
        long privateReservedDataSize = streamReader.ReadLong7Bit();
        long privateExternDataSize   = streamReader.ReadLong7Bit();
        long externDataSize          = streamReader.ReadLong7Bit();

        long headDataSize           = streamReader.ReadLong7Bit();
        long headDataCompressedSize = streamReader.ReadLong7Bit();
        long checksumByteSize       = streamReader.ReadLong7Bit();

        int        checksumDataLen = (int)checksumByteSize * 4;
        Span<byte> checksumData    = stackalloc byte[checksumDataLen];

        streamReader.ReadBytes(checksumData);
        long headDataOffset = streamReader.Offset;

        // Try seek primary reader. Read the HDiff13 signature and sanity.
        long toSkipHeadDataSize = headDataCompressedSize > 0 ? headDataCompressedSize : headDataSize;
        streamReader.AdvanceSeekTo((int)toSkipHeadDataSize);
        string sanityDiffPatchSignature = streamReader.ReadStringToNull();
        ReadBasicHeaderSignature(sanityDiffPatchSignature, out _, out _, out _);

        // Read HeadData.
        (Stream additionalStream, bool leaveOpenAdditionalStream) = additionalStreamCreate(headDataOffset);
        using Stream additionalStreamDecompressed = DecompressStreamFactory.CreateStream(info.CompressionType, additionalStream, leaveOpenAdditionalStream);
        using BittableStreamReader anotherStreamReader = new(additionalStreamDecompressed, leaveOpen: leaveOpenAdditionalStream);

        UnmanagedArray<Utf16UnmanagedString>* inputPathEntryArray        = anotherStreamReader.CreateUnmanagedStringList(inputPathEntryCount, (int)inputPathEntryBufferSize);
        UnmanagedArray<Utf16UnmanagedString>* outputPathEntryArray       = anotherStreamReader.CreateUnmanagedStringList(outputPathEntryCount, (int)outputPathEntryBufferSize);
        int*                                  inputFilesIndexArray       = anotherStreamReader.CreateUnmanagedInt64As32List(inputRefFileCount);
        int*                                  outputFilesIndexArray      = anotherStreamReader.CreateUnmanagedInt64As32List(outputRefFileCount);
        long*                                 outputFilesSizesArray      = anotherStreamReader.CreateUnmanagedInt64List(outputRefFileCount);
        FileIndexPair*                        sameFilePathIndexPairArray = anotherStreamReader.CreateUnmanagedIndexPairList(sameFilePathEntryCount);
        int*                                  newExecuteListArray        = anotherStreamReader.CreateUnmanagedInt64As32List(newExecuteCount);

        ref HeaderDirectoryPatchMetadata dirTypeMetadata = ref info.AllocMetadata<HeaderDirectoryPatchMetadata>();
        dirTypeMetadata.IsInputDir  = isInputDir;
        dirTypeMetadata.IsOutputDir = isOutputDir;

        EntryCountSizeInfo* inputPathCountSizeInfoP    = dirTypeMetadata.InputPathCountSizeInfoP;
        EntryCountSizeInfo* outputPathCountSizeInfoP   = dirTypeMetadata.OutputPathCountSizeInfoP;
        EntryCountSizeInfo* sameFilePathCountSizeInfoP = dirTypeMetadata.SameFilePathCountSizeInfoP;
        ExternSizeInfo*     externSizeInfo             = dirTypeMetadata.ExternSizeInfoP;
        ChunkSizeInfo*      headDataSizeP              = dirTypeMetadata.HeadDataSizeP;
        ChecksumDataInfo*   checksumDataInfoP          = dirTypeMetadata.ChecksumDataInfoP;

        inputPathCountSizeInfoP->Count    = inputPathEntryCount;
        inputPathCountSizeInfoP->Size     = inputPathEntryTotalSize;
        outputPathCountSizeInfoP->Count   = outputPathEntryCount;
        outputPathCountSizeInfoP->Size    = outputPathEntryTotalSize;
        sameFilePathCountSizeInfoP->Count = sameFilePathEntryCount;
        sameFilePathCountSizeInfoP->Size  = sameFilePathEntryTotalSize;

        dirTypeMetadata.SameFilePathIndexPairP = sameFilePathIndexPairArray;
        dirTypeMetadata.NewExecuteListP        = newExecuteListArray;

        dirTypeMetadata.InputPathListP       = inputPathEntryArray;
        dirTypeMetadata.OutputPathListP      = outputPathEntryArray;
        dirTypeMetadata.InputFileIndexListP  = inputFilesIndexArray;
        dirTypeMetadata.OutputFileIndexListP = outputFilesIndexArray;
        dirTypeMetadata.OutputFileSizeListP  = outputFilesSizesArray;

        externSizeInfo->NewExecuteCount         = newExecuteCount;
        externSizeInfo->PrivateReservedDataSize = privateReservedDataSize;
        externSizeInfo->PrivateExternDataSize   = privateExternDataSize;
        externSizeInfo->ExternDataSize          = externDataSize;

        headDataSizeP->Size           = headDataSize;
        headDataSizeP->CompressedSize = headDataCompressedSize;

        checksumDataInfoP->AllocBytes((int)checksumByteSize, 4);
        checksumData.CopyTo(checksumDataInfoP->GetAllSpan());
    }

    private static async Task<HDiffInfo> ReadHDiff19HeaderInfoAsyncCore(
        HDiffInfo            info,
        BittableStreamReader streamReader,
        CreateStreamAsync    additionalStreamCreate,
        CancellationToken    token)
    {
        byte isInputDir  = await streamReader.ReadByteAsync(token);
        byte isOutputDir = await streamReader.ReadByteAsync(token);

        int  inputPathEntryCount       = (int)await streamReader.ReadLong7BitAsync(token);
        long inputPathEntryBufferSize  = await streamReader.ReadLong7BitAsync(token);
        int  outputPathEntryCount      = (int)await streamReader.ReadLong7BitAsync(token);
        long outputPathEntryBufferSize = await streamReader.ReadLong7BitAsync(token);

        int  inputRefFileCount        = (int)await streamReader.ReadLong7BitAsync(token);
        long inputPathEntryTotalSize  = await streamReader.ReadLong7BitAsync(token);
        int  outputRefFileCount       = (int)await streamReader.ReadLong7BitAsync(token);
        long outputPathEntryTotalSize = await streamReader.ReadLong7BitAsync(token);

        int  sameFilePathEntryCount     = (int)await streamReader.ReadLong7BitAsync(token);
        long sameFilePathEntryTotalSize = await streamReader.ReadLong7BitAsync(token);

        int  newExecuteCount         = await streamReader.ReadInt7BitAsync(token);
        long privateReservedDataSize = await streamReader.ReadLong7BitAsync(token);
        long privateExternDataSize   = await streamReader.ReadLong7BitAsync(token);
        long externDataSize          = await streamReader.ReadLong7BitAsync(token);

        long headDataSize           = await streamReader.ReadLong7BitAsync(token);
        long headDataCompressedSize = await streamReader.ReadLong7BitAsync(token);
        long checksumByteSize       = await streamReader.ReadLong7BitAsync(token);

        int    checksumDataLen = (int)checksumByteSize * 4;
        byte[] checksumData    = ArrayPool<byte>.Shared.Rent(checksumDataLen);
        await streamReader.ReadBytesAsync(checksumData.AsMemory(0, checksumDataLen), token);

        long headDataOffset = streamReader.Offset;

        // Try seek primary reader. Read the HDiff13 signature and sanity.
        long toSkipHeadDataSize = headDataCompressedSize > 0 ? headDataCompressedSize : headDataSize;
        await streamReader.AdvanceSeekToAsync((int)toSkipHeadDataSize, token);
        string sanityDiffPatchSignature = await streamReader.ReadStringToNullAsync(token);
        ReadBasicHeaderSignature(sanityDiffPatchSignature, out _, out _, out _);

        // Read HeadData.
        (Stream additionalStream, bool leaveOpenAdditionalStream) = await additionalStreamCreate(headDataOffset, token);
        using Stream additionalStreamDecompressed =
            DecompressStreamFactory.CreateStream(info.CompressionType, additionalStream, leaveOpenAdditionalStream);
        using BittableStreamReader anotherStreamReader = new(additionalStreamDecompressed, leaveOpen: leaveOpenAdditionalStream);

        nint inputPathEntryArray        = await anotherStreamReader.CreateUnmanagedStringListAsync(inputPathEntryCount, (int)inputPathEntryBufferSize, token);
        nint outputPathEntryArray       = await anotherStreamReader.CreateUnmanagedStringListAsync(outputPathEntryCount, (int)outputPathEntryBufferSize, token);
        nint inputFilesIndexArray       = await anotherStreamReader.CreateUnmanagedInt64As32ListAsync(inputRefFileCount, token);
        nint outputFilesIndexArray      = await anotherStreamReader.CreateUnmanagedInt64As32ListAsync(outputRefFileCount, token);
        nint outputFilesSizesArray      = await anotherStreamReader.CreateUnmanagedInt64ListAsync(outputRefFileCount, token);
        nint sameFilePathIndexPairArray = await anotherStreamReader.CreateUnmanagedIndexPairListAsync(sameFilePathEntryCount, token);
        nint newExecuteListArray        = await anotherStreamReader.CreateUnmanagedInt64As32ListAsync(newExecuteCount, token);

        try
        {
            ref HeaderDirectoryPatchMetadata dirTypeMetadata = ref info.AllocMetadata<HeaderDirectoryPatchMetadata>();
            dirTypeMetadata.IsInputDir = isInputDir;
            dirTypeMetadata.IsOutputDir = isOutputDir;

            unsafe
            {
                EntryCountSizeInfo* inputPathCountSizeInfoP    = dirTypeMetadata.InputPathCountSizeInfoP;
                EntryCountSizeInfo* outputPathCountSizeInfoP   = dirTypeMetadata.OutputPathCountSizeInfoP;
                EntryCountSizeInfo* sameFilePathCountSizeInfoP = dirTypeMetadata.SameFilePathCountSizeInfoP;
                ExternSizeInfo*     externSizeInfo             = dirTypeMetadata.ExternSizeInfoP;
                ChunkSizeInfo*      headDataSizeP              = dirTypeMetadata.HeadDataSizeP;
                ChecksumDataInfo*   checksumDataInfoP          = dirTypeMetadata.ChecksumDataInfoP;

                inputPathCountSizeInfoP->Count    = inputPathEntryCount;
                inputPathCountSizeInfoP->Size     = inputPathEntryTotalSize;
                outputPathCountSizeInfoP->Count   = outputPathEntryCount;
                outputPathCountSizeInfoP->Size    = outputPathEntryTotalSize;
                sameFilePathCountSizeInfoP->Count = sameFilePathEntryCount;
                sameFilePathCountSizeInfoP->Size  = sameFilePathEntryTotalSize;

                dirTypeMetadata.SameFilePathIndexPairP = (FileIndexPair*)sameFilePathIndexPairArray;
                dirTypeMetadata.NewExecuteListP        = (int*)newExecuteListArray;

                dirTypeMetadata.InputPathListP       = (UnmanagedArray<Utf16UnmanagedString>*)inputPathEntryArray;
                dirTypeMetadata.OutputPathListP      = (UnmanagedArray<Utf16UnmanagedString>*)outputPathEntryArray;
                dirTypeMetadata.InputFileIndexListP  = (int*)inputFilesIndexArray;
                dirTypeMetadata.OutputFileIndexListP = (int*)outputFilesIndexArray;
                dirTypeMetadata.OutputFileSizeListP  = (long*)outputFilesSizesArray;

                externSizeInfo->NewExecuteCount         = newExecuteCount;
                externSizeInfo->PrivateReservedDataSize = privateReservedDataSize;
                externSizeInfo->PrivateExternDataSize   = privateExternDataSize;
                externSizeInfo->ExternDataSize          = externDataSize;

                headDataSizeP->Size           = headDataSize;
                headDataSizeP->CompressedSize = headDataCompressedSize;

                checksumDataInfoP->AllocBytes((int)checksumByteSize, 4);
                checksumData.CopyTo(checksumDataInfoP->GetAllSpan());

                return info;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(checksumData);
        }
    }

    private static unsafe void ReadHDiff13HeaderInfoCore(
        ref HDiffInfo          info,
        BittableStreamReader   streamReader,
        PatchMetadataAllocator metadataAllocator)
    {
        long newSize = streamReader.ReadLong7Bit();
        long oldSize = streamReader.ReadLong7Bit();

        long coverDataCount = streamReader.ReadLong7Bit();

        long coverDataSize       = streamReader.ReadLong7Bit();
        long coverDataSizeC      = streamReader.ReadLong7Bit();
        long rleControlDataSize  = streamReader.ReadLong7Bit();
        long rleControlDataSizeC = streamReader.ReadLong7Bit();
        long rleCodeDataSize     = streamReader.ReadLong7Bit();
        long rleCodeDataSizeC    = streamReader.ReadLong7Bit();
        long newDiffSize         = streamReader.ReadLong7Bit();
        long newDiffSizeC        = streamReader.ReadLong7Bit();

        ref PatchMetadata patchMetadata = ref metadataAllocator(ref info);

        patchMetadata.DiffNewSize    = newSize;
        patchMetadata.DiffOldSize    = oldSize;
        patchMetadata.CoverDataCount = coverDataCount;

        patchMetadata.CoverDataSizeP->Size                = coverDataSize;
        patchMetadata.CoverDataSizeP->CompressedSize      = coverDataSizeC;
        patchMetadata.RleControlDataSizeP->Size           = rleControlDataSize;
        patchMetadata.RleControlDataSizeP->CompressedSize = rleControlDataSizeC;
        patchMetadata.RleCodeDataSizeP->Size              = rleCodeDataSize;
        patchMetadata.RleCodeDataSizeP->CompressedSize    = rleCodeDataSizeC;
        patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSize;
        patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSizeC;
    }

    private static async Task<HDiffInfo> ReadHDiff13HeaderInfoAsyncCore(
        HDiffInfo              info,
        BittableStreamReader   streamReader,
        PatchMetadataAllocator metadataAllocator,
        CancellationToken      token)
    {
        long newSize = await streamReader.ReadLong7BitAsync(token);
        long oldSize = await streamReader.ReadLong7BitAsync(token);

        long coverDataCount = await streamReader.ReadLong7BitAsync(token);

        long coverDataSize       = await streamReader.ReadLong7BitAsync(token);
        long coverDataSizeC      = await streamReader.ReadLong7BitAsync(token);
        long rleControlDataSize  = await streamReader.ReadLong7BitAsync(token);
        long rleControlDataSizeC = await streamReader.ReadLong7BitAsync(token);
        long rleCodeDataSize     = await streamReader.ReadLong7BitAsync(token);
        long rleCodeDataSizeC    = await streamReader.ReadLong7BitAsync(token);
        long newDiffSize         = await streamReader.ReadLong7BitAsync(token);
        long newDiffSizeC        = await streamReader.ReadLong7BitAsync(token);

        unsafe
        {
            ref PatchMetadata patchMetadata = ref metadataAllocator(ref info);

            patchMetadata.DiffNewSize    = newSize;
            patchMetadata.DiffOldSize    = oldSize;
            patchMetadata.CoverDataCount = coverDataCount;

            patchMetadata.CoverDataSizeP->Size                = coverDataSize;
            patchMetadata.CoverDataSizeP->CompressedSize      = coverDataSizeC;
            patchMetadata.RleControlDataSizeP->Size           = rleControlDataSize;
            patchMetadata.RleControlDataSizeP->CompressedSize = rleControlDataSizeC;
            patchMetadata.RleCodeDataSizeP->Size              = rleCodeDataSize;
            patchMetadata.RleCodeDataSizeP->CompressedSize    = rleCodeDataSizeC;
            patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSize;
            patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSizeC;

            return info;
        }
    }
}
