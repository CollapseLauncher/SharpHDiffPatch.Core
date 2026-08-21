using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Compression;
using SharpHPatchZ.IO.Reader;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHPatchZ;

public delegate (Stream Stream, bool LeaveOpen) CreateStream(long position);
public delegate ValueTask<(Stream Stream, bool LeaveOpen)> CreateStreamAsync(long position, CancellationToken token);

public static partial class HPatch
{
    public static HDiffInfo CreateInstance(CreateStream createPatchStream)
    {
        try
        {
            (Stream stream, bool leaveOpen) = createPatchStream(0);
            using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

            string    signature = reader.ReadStringToNull();
            HDiffInfo info      = default;
            HeaderReader.ReadHeaderSignature(signature, ref info);
            HeaderReader.ReadHDiffHeaderMetadata(ref info, reader);

            return info;
        }
        catch (EndOfStreamException eofStream)
        {
            ExceptionHelper.ThrowHDiffEndOfFileOrData(eofStream);
            throw;
        }
    }

    public static async ValueTask<HDiffInfo> CreateInstanceAsync(
        CreateStreamAsync createPatchStreamAsync,
        CancellationToken token = default)
    {
        (Stream stream, bool leaveOpen) = await createPatchStreamAsync(0, token);
        using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

        string    signature = await reader.ReadStringToNullAsync(token: token);
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, token);

        return info;
    }

    public static void Patch(ref HDiffInfo     info,
                             CreateStream      createPatchStream,
                             string            inputPath,
                             string            outputPath,
                             PatchOptions      options = default,
                             CancellationToken token   = default)
    {
        GetPatchDataOffsets(ref info,
                            out long coverDataOffset,
                            out long rleControlDataOffset,
                            out long rleCodeDataOffset,
                            out long newDataOffset,
                            out HDiffCompression compType);

        (Stream coverStream, bool coverStreamLeaveOpen)           = createPatchStream(coverDataOffset);
        (Stream rleControlStream, bool rleControlStreamLeaveOpen) = createPatchStream(rleControlDataOffset);
        (Stream rleCodeStream, bool rleCodeStreamLeaveOpen)       = createPatchStream(rleCodeDataOffset);
        (Stream newDataStream, bool newDataStreamLeaveOpen)       = createPatchStream(newDataOffset);

        using Stream decCoverStream      = DecompressStreamFactory.CreateStream(compType, coverStream, coverStreamLeaveOpen);
        using Stream decRleControlStream = DecompressStreamFactory.CreateStream(compType, rleControlStream, rleControlStreamLeaveOpen);
        using Stream decRleCodeStream    = DecompressStreamFactory.CreateStream(compType, rleCodeStream, rleCodeStreamLeaveOpen);
        using Stream decNewDataStream    = DecompressStreamFactory.CreateStream(compType, newDataStream, newDataStreamLeaveOpen);

        using BittableStreamReader coverReader      = new(decCoverStream, options.ReaderBufferSize, leaveOpen: coverStreamLeaveOpen);
        using BittableStreamReader rleControlReader = new(decRleControlStream, options.ReaderBufferSize, leaveOpen: rleControlStreamLeaveOpen);
        using BittableStreamReader rleCodeReader    = new(decRleCodeStream, options.ReaderBufferSize, leaveOpen: rleCodeStreamLeaveOpen);
        using BittableStreamReader newDataReader    = new(decNewDataStream, options.ReaderBufferSize, leaveOpen: newDataStreamLeaveOpen);
    }

    public static async Task PatchAsync(HDiffInfo         info,
                                        CreateStreamAsync createPatchStreamAsync,
                                        string            inputPath,
                                        string            outputPath,
                                        PatchOptions      options = default,
                                        CancellationToken token   = default)
    {

    }

    public static unsafe void GetPatchDataOffsets(ref HDiffInfo        info,
                                                  out long             coverDataOffset,
                                                  out long             rleControlDataOffset,
                                                  out long             rleCodeDataOffset,
                                                  out long             newDataOffset,
                                                  out HDiffCompression compressionType)
    {
        ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();

        compressionType = info.CompressionType;

        ChunkSizeInfo* coverDataSizeP      = patchMetadata.CoverDataSizeP;
        ChunkSizeInfo* rleControlDataSizeP = patchMetadata.RleControlDataSizeP;
        ChunkSizeInfo* rleCodeDataSizeP    = patchMetadata.RleCodeDataSizeP;

        coverDataOffset = patchMetadata.DiffDataOffset;
        rleControlDataOffset = coverDataOffset + (coverDataSizeP->CompressedSize > 0 ? coverDataSizeP->CompressedSize : coverDataSizeP->Size);
        rleCodeDataOffset = rleControlDataOffset + (rleControlDataSizeP->CompressedSize > 0 ? rleControlDataSizeP->CompressedSize : rleControlDataSizeP->Size);
        newDataOffset = rleCodeDataOffset + (rleCodeDataSizeP->CompressedSize > 0 ? rleCodeDataSizeP->CompressedSize : rleCodeDataSizeP->Size);
    }
}
