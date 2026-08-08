using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Reader;

namespace SharpHPatchZ;

public delegate (Stream Stream, bool LeaveOpen) CreateStream(long position);

public delegate ValueTask<(Stream Stream, bool LeaveOpen)> CreateStreamAsync(long position, CancellationToken token);

public static class HPatch
{
    public static HDiffInfo CreateInstance(CreateStream createStream)
    {
        (Stream stream, bool isDispose) = createStream(0);
        using BittableStreamReader reader = new(stream, leaveOpen: !isDispose);

        string    signature = reader.ReadStringToNull();
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        HeaderReader.ReadHDiffHeaderMetadata(ref info, reader, createStream);

        return info;
    }

    public static async ValueTask<HDiffInfo> CreateInstanceAsync(
        CreateStreamAsync createStream,
        CancellationToken token = default)
    {
        (Stream stream, bool isDispose) = await createStream(0, token);
        using BittableStreamReader reader = new(stream, leaveOpen: !isDispose);

        string    signature = await reader.ReadStringToNullAsync(token: token);
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, createStream, token);

        return info;
    }
}
