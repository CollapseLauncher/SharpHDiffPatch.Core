using System;
using System.IO;
using System.Runtime.CompilerServices;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;

namespace SharpHPatchZ;

public static partial class HPatch
{
    public static bool TryGetPatchMetadata(
        ref HDiffInfo     info,
        out PatchMetadata patchMetadata)
    {
        Unsafe.SkipInit(out patchMetadata);

        ref PatchMetadata patchMetadataRef = ref info.GetPatchMetadata();
        if (Unsafe.IsNullRef(ref patchMetadataRef))
        {
            return false;
        }

        patchMetadata = patchMetadataRef;
        return true;
    }

    public static bool TryGetDirectoryPatchMetadata(
        ref HDiffInfo              info,
        out DirectoryPatchMetadata patchMetadata)
    {
        Unsafe.SkipInit(out patchMetadata);

        ref DirectoryPatchMetadata patchMetadataRef = ref info.MetadataAs<DirectoryPatchMetadata>();
        if (Unsafe.IsNullRef(ref patchMetadataRef))
        {
            return false;
        }

        patchMetadata = patchMetadataRef;
        return true;
    }

    public static bool TryGetDiffSizeInfo(ref HDiffInfo info,
                                          out long      totalInputSize,
                                          out long      totalOutputSize)
    {
        Unsafe.SkipInit(out totalInputSize);
        Unsafe.SkipInit(out totalOutputSize);

        ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();
        if (Unsafe.IsNullRef(ref patchMetadata))
        {
            return false;
        }

        totalInputSize  = patchMetadata.DiffOldSize;
        totalOutputSize = patchMetadata.DiffNewSize;
        return true;
    }

    public static bool TryGetDiffSizeInfo(CreateStream createStream,
                                          out long     totalInputSize,
                                          out long     totalOutputSize)
    {
        HDiffInfo info = CreateInstance(createStream);
        try
        {
            return TryGetDiffSizeInfo(ref info,
                                      out totalInputSize,
                                      out totalOutputSize);
        }
        finally
        {
            info.Dispose();
        }
    }

    public static bool TryGetDiffSizeInfo(Stream   stream,
                                          out long totalInputSize,
                                          out long totalOutputSize)
    {
        return TryGetDiffSizeInfo(CreateStream,
                                  out totalInputSize,
                                  out totalOutputSize);

        (Stream, bool) CreateStream(long pos)
        {
            stream.Position = pos;
            return (stream, true);
        }
    }

    public static unsafe Span<T> TryGetUnmanagedArraySpan<T>(UnmanagedArray<T>* array)
        where T : unmanaged
        => array == null ? Span<T>.Empty : array->GetSpan();
}