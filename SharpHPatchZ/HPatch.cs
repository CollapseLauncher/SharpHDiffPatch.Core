using System;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Reader;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Patch;

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
            throw ExceptionHelper.ThrowHDiffEndOfFileOrData(eofStream);
        }
    }

    public static async ValueTask<HDiffInfo> CreateInstanceAsync(
        CreateStreamAsync createPatchStreamAsync,
        CancellationToken token = default)
    {
        (Stream stream, bool leaveOpen) = await createPatchStreamAsync(0, token);
#if NET6_0_OR_GREATER
        await
#endif
        using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

        string    signature = await reader.ReadStringToNullAsync(token: token);
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, token);

        return info;
    }

    public static PatchResult Patch(HDiffInfo         info,
                                    CreateStream      createPatchStream,
                                    string            inputPath,
                                    string            outputPath,
                                    PatchOptions      options          = default,
                                    ProgressCallback  progressCallback = default,
                                    CancellationToken token            = default)
    {
        try
        {
            using PatcherBase patcher = PatcherFactory.CreateFromInfo(ref info, createPatchStream, options, progressCallback);
            patcher.StartPatch(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public static async Task<PatchResult> PatchAsync(
        HDiffInfo         info,
        CreateStreamAsync createPatchStreamAsync,
        string            inputPath,
        string            outputPath,
        PatchOptions      options          = default,
        ProgressCallback  progressCallback = default,
        CancellationToken token            = default)
    {
        try
        {
#if NET6_0_OR_GREATER
            await
#endif
            using PatcherBase patcher = await PatcherFactory.CreateFromInfoAsync(info, createPatchStreamAsync, options, progressCallback, token);
            await patcher.StartPatchAsync(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
