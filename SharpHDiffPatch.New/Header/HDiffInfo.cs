using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpHDiffPatch.New.Extension;
using SharpHDiffPatch.New.Header.Metadata;

namespace SharpHDiffPatch.New.Header;

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

    public void Dispose()
    {
        if (MetadataP == null) return;

        MemoryAlloc.Free(MetadataP);
        MetadataP = null;
    }
}
