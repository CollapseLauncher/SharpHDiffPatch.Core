using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header.Metadata;

namespace SharpHPatchZ.Header;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct HDiffInfo : IDisposable
{
    public HDiffMagic       MagicType;
    public HDiffCompression CompressionType;
    public HDiffChecksum    ChecksumType;
    public void*            MetadataP;

    public ref T MetadataAs<T>()
        where T : unmanaged, IMetadataInit
        => ref MetadataP == null ?
            ref Unsafe.NullRef<T>() :
            ref Unsafe.AsRef<T>(MetadataP);

    public ref T AllocMetadata<T>()
        where T : unmanaged, IMetadataInit
    {
        if (MetadataP != null) MemoryAlloc.Free(MetadataP);
        
        MetadataP = MemoryAlloc.Alloc<T>(1, true);
        ((T*)MetadataP)->Init(); // Start metadata initialization
        return ref Unsafe.AsRef<T>(MetadataP);
    }

    public ref PatchMetadata GetPatchMetadata()
    {
        void* patchMetadataP;
        if (MetadataExtension.TryGetMetadataType(MetadataP, out MetadataTypeConst rootMetadataType) &&
            rootMetadataType == MetadataTypeConst.DirectoryPatchMetadataType)
        {
            ref DirectoryPatchMetadata dirPatchMetadata = ref MetadataAs<DirectoryPatchMetadata>();
            if (Unsafe.IsNullRef(ref dirPatchMetadata) || dirPatchMetadata.PatchMetadataP == null)
            {
                throw ExceptionHelper.ThrowHDiffInfoPatchMetadataNotAllocated();
            }

            patchMetadataP = dirPatchMetadata.PatchMetadataP;
        }
        else
        {
            patchMetadataP = MetadataP;
        }

        if (patchMetadataP == null)
        {
            throw ExceptionHelper.ThrowHDiffInfoPatchMetadataNotAllocated();
        }

        return ref Unsafe.AsRef<PatchMetadata>(patchMetadataP);
    }

    public void Dispose()
    {
        if (MetadataP == null) return;

        MemoryAlloc.Free(MetadataP);
        MetadataP = null;
    }
}
