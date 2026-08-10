using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Reader;

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
            HeaderReader.ReadHDiffHeaderMetadata(ref info, reader, createPatchStream);

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
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, createPatchStreamAsync, token);

        return info;
    }
}
