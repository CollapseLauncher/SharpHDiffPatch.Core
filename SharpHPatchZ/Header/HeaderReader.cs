using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Compression;
using SharpHPatchZ.IO.Reader;

namespace SharpHPatchZ.Header;

/// <summary>
/// Provides operations for parsing HDiff patch headers.
/// </summary>
public class HeaderReader
{
    private delegate ref PatchMetadata PatchMetadataAllocator(ref HDiffInfo info);

    /// <summary>Parses a patch header signature into an existing <see cref="HDiffInfo"/> value.</summary>
    /// <param name="signature">The header signature to parse.</param>
    /// <param name="info">The patch information to populate.</param>
    public static void ReadHeaderSignature(ReadOnlySpan<char> signature, ref HDiffInfo info)
    {
        ReadBasicHeaderSignature(signature,
                                 out info.MagicType,
                                 out info.CompressionType,
                                 out info.ChecksumType);
    }

    internal static void ReadBasicHeaderSignature(
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
            throw ExceptionHelper.ThrowHDiffHeaderSignatureEmptyOrUnreadable();
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
            throw ExceptionHelper.ThrowHDiffHeaderMagicNotSupported(magicSpan);
        }

        switch (magicType)
        {
            // Parse HDIFF19 (Directory Patch) enums
            case HDiffMagic.HDiff19:
            {
                if (compressionTypeSpan.Length != 0 &&
                    !Enum.TryParse(compressionTypeSpan, true, out compressionType))
                {
                    throw ExceptionHelper.ThrowHDiffHeaderCompressionNotSupported(compressionTypeSpan);
                }

                if (checksumTypeSpan.Length != 0 &&
                    !Enum.TryParse(checksumTypeSpan, true, out checksumType))
                {
                    throw ExceptionHelper.ThrowHDiffHeaderChecksumNotSupported(checksumTypeSpan);
                }

                return;
            }
            // Parse HDIFF13 (Single Patch) enums
            case HDiffMagic.HDiff13:
            {
                if (compressionTypeSpan.Length != 0 &&
                    !Enum.TryParse(compressionTypeSpan, true, out compressionType))
                {
                    throw ExceptionHelper.ThrowHDiffHeaderCompressionNotSupported(compressionTypeSpan);
                }

                return;
            }
            case HDiffMagic.Unknown:
            default:
                throw ExceptionHelper.ThrowHDiffHeaderMagicNotSupported(magicSpan);
        }
    }

    internal static void ReadHDiffHeaderMetadata(
        ref HDiffInfo        info,
        BittableStreamReader streamReader,
        InitializeOptions    initializeOptions)
    {
        info.InitializeOptions = initializeOptions;
        PatchMetadataAllocator patchMetadataAllocator = DefaultPatchMetadataAllocator;
        switch (info.MagicType)
        {
            case HDiffMagic.HDiff19:
                ReadHDiff19HeaderInfoCore(ref info, streamReader);
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
        InitializeOptions    initializeOptions,
        CancellationToken    token)
    {
        info.InitializeOptions = initializeOptions;
        PatchMetadataAllocator patchMetadataAllocator = DefaultPatchMetadataAllocator;
        switch (info.MagicType)
        {
            case HDiffMagic.HDiff19:
                info = await ReadHDiff19HeaderInfoAsyncCore(info,
                                                            streamReader,
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

        ref DirectoryPatchMetadata dirPatchMetadata = ref info.MetadataAs<DirectoryPatchMetadata>();
        dirPatchMetadata.PatchMetadataP = patchMetadata;
        return ref Unsafe.AsRef<PatchMetadata>(patchMetadata);
    }

    private static unsafe void ReadHDiff19HeaderInfoCore(
        ref HDiffInfo        info,
        BittableStreamReader streamReader)
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

        if (headDataCompressedSize > 0)
        {
            streamReader.ContinueWithDecompressor(new HDiffDecompressor(info.CompressionType),
                                                  headDataCompressedSize,
                                                  headDataSize);
        }

        long headDataStartOffset = streamReader.Offset;

        UnmanagedArray<Utf16UnmanagedString>* inputPathEntryArray = streamReader.CreateUnmanagedStringList(inputPathEntryCount, (int)inputPathEntryBufferSize);
        UnmanagedArray<Utf16UnmanagedString>* outputPathEntryArray = streamReader.CreateUnmanagedStringList(outputPathEntryCount, (int)outputPathEntryBufferSize);
        UnmanagedArray<int>* inputFilesIndexArray = streamReader.CreateUnmanagedInt64As32List(inputRefFileCount);
        UnmanagedArray<int>* outputFilesIndexArray = streamReader.CreateUnmanagedInt64As32List(outputRefFileCount);

        UnmanagedArray<long>* inputFilesSizesArray = null;
        if (info.InitializeOptions.IsKuroGamesHDiff)
            inputFilesSizesArray = streamReader.CreateUnmanagedInt64List(inputRefFileCount);

        UnmanagedArray<long>* outputFilesSizesArray = streamReader.CreateUnmanagedInt64List(outputRefFileCount);

        UnmanagedArray<long>* outputFilesHashesArray = null;
        if (info.InitializeOptions.IsKuroGamesHDiff)
            outputFilesHashesArray = streamReader.CreateUnmanagedInt64List(outputRefFileCount);

        FileIndexPair* sameFilePathIndexPairArray = streamReader.CreateUnmanagedIndexPairList(sameFilePathEntryCount);
        UnmanagedArray<int>* newExecuteListArray = streamReader.CreateUnmanagedInt64As32List(newExecuteCount);

        if (streamReader.Offset - headDataStartOffset != headDataSize)
        {
            throw new InvalidDataException("The directory head data length does not match its declared length.");
        }

        string sanityDiffPatchSignature = streamReader.ReadStringToNull();
        ReadBasicHeaderSignature(sanityDiffPatchSignature, out _, out _, out _);

        ref DirectoryPatchMetadata dirTypeMetadata = ref info.AllocMetadata<DirectoryPatchMetadata>();
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

        dirTypeMetadata.InputPathListP        = inputPathEntryArray;
        dirTypeMetadata.OutputPathListP       = outputPathEntryArray;
        dirTypeMetadata.InputFileIndexListP   = inputFilesIndexArray;
        dirTypeMetadata.InputFileSizeListP    = inputFilesSizesArray;
        dirTypeMetadata.OutputFileIndexListP  = outputFilesIndexArray;
        dirTypeMetadata.OutputFileSizeListP   = outputFilesSizesArray;
        dirTypeMetadata.OutputFileHashesListP = outputFilesHashesArray;

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

        if (headDataCompressedSize > 0)
        {
            streamReader.ContinueWithDecompressor(new HDiffDecompressor(info.CompressionType),
                                                  headDataCompressedSize,
                                                  headDataSize);
        }

        long headDataStartOffset = streamReader.Offset;

        nint inputPathEntryArray = await streamReader.CreateUnmanagedStringListAsync(inputPathEntryCount, (int)inputPathEntryBufferSize, token);
        nint outputPathEntryArray = await streamReader.CreateUnmanagedStringListAsync(outputPathEntryCount, (int)outputPathEntryBufferSize, token);
        nint inputFilesIndexArray = await streamReader.CreateUnmanagedInt64As32ListAsync(inputRefFileCount, token);
        nint outputFilesIndexArray = await streamReader.CreateUnmanagedInt64As32ListAsync(outputRefFileCount, token);

        nint inputFilesSizesArray = 0;
        if (info.InitializeOptions.IsKuroGamesHDiff)
            inputFilesSizesArray = await streamReader.CreateUnmanagedInt64ListAsync(inputRefFileCount, token);

        nint outputFilesSizesArray = await streamReader.CreateUnmanagedInt64ListAsync(outputRefFileCount, token);

        nint outputFilesHashesArray = 0;
        if (info.InitializeOptions.IsKuroGamesHDiff)
            outputFilesHashesArray = await streamReader.CreateUnmanagedInt64ListAsync(outputRefFileCount, token);

        nint sameFilePathIndexPairArray = await streamReader.CreateUnmanagedIndexPairListAsync(sameFilePathEntryCount, token);
        nint newExecuteListArray = await streamReader.CreateUnmanagedInt64As32ListAsync(newExecuteCount, token);

        if (streamReader.Offset - headDataStartOffset != headDataSize)
        {
            throw new InvalidDataException("The directory head data length does not match its declared length.");
        }

        string sanityDiffPatchSignature = await streamReader.ReadStringToNullAsync(token);
        ReadBasicHeaderSignature(sanityDiffPatchSignature, out _, out _, out _);

        try
        {
            ref DirectoryPatchMetadata dirTypeMetadata = ref info.AllocMetadata<DirectoryPatchMetadata>();
            dirTypeMetadata.IsInputDir  = isInputDir;
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
                dirTypeMetadata.NewExecuteListP        = (UnmanagedArray<int>*)newExecuteListArray;

                dirTypeMetadata.InputPathListP        = (UnmanagedArray<Utf16UnmanagedString>*)inputPathEntryArray;
                dirTypeMetadata.OutputPathListP       = (UnmanagedArray<Utf16UnmanagedString>*)outputPathEntryArray;
                dirTypeMetadata.InputFileIndexListP   = (UnmanagedArray<int>*)inputFilesIndexArray;
                dirTypeMetadata.InputFileSizeListP    = (UnmanagedArray<long>*)inputFilesSizesArray;
                dirTypeMetadata.OutputFileIndexListP  = (UnmanagedArray<int>*)outputFilesIndexArray;
                dirTypeMetadata.OutputFileSizeListP   = (UnmanagedArray<long>*)outputFilesSizesArray;
                dirTypeMetadata.OutputFileHashesListP = (UnmanagedArray<long>*)outputFilesHashesArray;

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

        int coverDataCount = (int)streamReader.ReadLong7Bit();

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
        patchMetadata.NewDiffDataSizeP->Size              = newDiffSize;
        patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSizeC;
        patchMetadata.DiffDataOffset                      = streamReader.OffsetUnderlyingStream;
    }

    private static async Task<HDiffInfo> ReadHDiff13HeaderInfoAsyncCore(
        HDiffInfo              info,
        BittableStreamReader   streamReader,
        PatchMetadataAllocator metadataAllocator,
        CancellationToken      token)
    {
        long newSize = await streamReader.ReadLong7BitAsync(token);
        long oldSize = await streamReader.ReadLong7BitAsync(token);

        int coverDataCount = (int)await streamReader.ReadLong7BitAsync(token);

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
            patchMetadata.NewDiffDataSizeP->Size              = newDiffSize;
            patchMetadata.NewDiffDataSizeP->CompressedSize    = newDiffSizeC;
            patchMetadata.DiffDataOffset                      = streamReader.OffsetUnderlyingStream;

            return info;
        }
    }
}
