using System;
using System.Runtime.CompilerServices;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;

namespace SharpHPatchZ;

public static partial class HPatch
{
    public static bool TryGetHDiff13PatchMetadata(
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

    public static bool TryGetHDiff19DirectoryPatchMetadata(
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

    public static unsafe Span<T> TryGetUnmanagedArraySpan<T>(UnmanagedArray<T>* array)
        where T : unmanaged
        => array == null ? Span<T>.Empty : array->GetSpan();
}