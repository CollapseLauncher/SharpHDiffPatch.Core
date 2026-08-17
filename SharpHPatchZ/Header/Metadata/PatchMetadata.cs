using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PatchMetadata : IMetadataInit
{
    public PatchMetadata()
    {
        Init();
    }

    public void Init()
    {
        if (IsDisposed || IsInitialized)
        {
            return;
        }

        IsInitialized       = true;
        IsDisposed          = false;
        MetadataType        = MetadataTypeConst.PatchMetadataType;
        CoverDataSizeP      = MemoryAlloc.Alloc<ChunkSizeInfo>(1, true);
        RleControlDataSizeP = MemoryAlloc.Alloc<ChunkSizeInfo>(1, true);
        RleCodeDataSizeP    = MemoryAlloc.Alloc<ChunkSizeInfo>(1, true);
        NewDiffDataSizeP    = MemoryAlloc.Alloc<ChunkSizeInfo>(1, true);
    }

    public void Dispose()
    {
        if (IsDisposed || !IsInitialized)
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;
        MemoryAlloc.Free(CoverDataSizeP);
        MemoryAlloc.Free(RleControlDataSizeP);
        MemoryAlloc.Free(RleCodeDataSizeP);
        MemoryAlloc.Free(NewDiffDataSizeP);
        CoverDataSizeP      = null;
        RleControlDataSizeP = null;
        RleCodeDataSizeP    = null;
        NewDiffDataSizeP    = null;
    }

    public MetadataTypeConst MetadataType { get; private set; }

    public bool IsInitialized
    {
        get => _isInitialized == 1;
        private set => _isInitialized = value ? (byte)1 : (byte)0;
    }

    public bool IsDisposed
    {
        get => _isDisposed == 1;
        private set => _isDisposed = value ? (byte)1 : (byte)0;
    }

    private byte _isInitialized;
    private byte _isDisposed;

    public long DiffNewSize;
    public long DiffOldSize;

    public long CoverDataCount;
    public long DiffDataOffset;

    public ChunkSizeInfo* CoverDataSizeP;
    public ChunkSizeInfo* RleControlDataSizeP;
    public ChunkSizeInfo* RleCodeDataSizeP;
    public ChunkSizeInfo* NewDiffDataSizeP;
}
