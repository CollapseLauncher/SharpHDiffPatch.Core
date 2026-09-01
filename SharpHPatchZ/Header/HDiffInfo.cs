using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header.Metadata;

namespace SharpHPatchZ.Header;

/// <summary>
/// Holds the parsed header, initialization options, and unmanaged metadata for an HDiff patch.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HDiffInfo : IDisposable
{
    /// <summary>Identifies the patch format.</summary>
    public HDiffMagic        MagicType;
    /// <summary>Identifies the compression algorithm used by the patch.</summary>
    public HDiffCompression  CompressionType;
    /// <summary>Identifies the checksum algorithm used by the patch.</summary>
    public HDiffChecksum     ChecksumType;
    /// <summary>Contains the <see cref="SharpHPatchZ.InitializeOptions"/> used to initialize this <see cref="HDiffInfo"/>.</summary>
    public InitializeOptions InitializeOptions;
    /// <summary>Points to format-specific unmanaged metadata owned by this instance.</summary>
    public void*             MetadataP;

    internal ref T MetadataAs<T>()
        where T : unmanaged, IMetadataInit
        => ref MetadataP == null ?
            ref Unsafe.NullRef<T>() :
            ref Unsafe.AsRef<T>(MetadataP);

    internal ref T AllocMetadata<T>()
        where T : unmanaged, IMetadataInit
    {
        if (MetadataP != null) MemoryAlloc.Free(MetadataP);
        
        MetadataP = MemoryAlloc.Alloc<T>(1, true);
        ((T*)MetadataP)->Init(); // Start metadata initialization
        return ref Unsafe.AsRef<T>(MetadataP);
    }

    internal ref PatchMetadata GetPatchMetadata()
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

    /// <summary>Releases the unmanaged metadata owned by this instance.</summary>
    public void Dispose()
    {
        if (MetadataP == null) return;

        MemoryAlloc.Free(MetadataP);
        MetadataP = null;
    }
}
