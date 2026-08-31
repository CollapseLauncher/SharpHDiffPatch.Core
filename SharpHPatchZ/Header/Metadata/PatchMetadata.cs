using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Contains the size and offset metadata required to apply a single-file patch.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PatchMetadata : IMetadataInit
{
    /// <summary>Initializes a new <see cref="PatchMetadata"/> and its <see cref="ChunkSizeInfo"/> records.</summary>
    public PatchMetadata()
    {
        Init();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public MetadataTypeConst MetadataType { get; private set; }

    /// <inheritdoc/>
    public bool IsInitialized
    {
        get => _isInitialized == 1;
        private set => _isInitialized = value ? (byte)1 : (byte)0;
    }

    /// <inheritdoc/>
    public bool IsDisposed
    {
        get => _isDisposed == 1;
        private set => _isDisposed = value ? (byte)1 : (byte)0;
    }

    private byte _isInitialized;
    private byte _isDisposed;

    /// <summary>The expected output size, in bytes.</summary>
    public long DiffNewSize;
    /// <summary>The expected input size, in bytes.</summary>
    public long DiffOldSize;
    /// <summary>The byte offset at which patch data begins.</summary>
    public long DiffDataOffset;

    /// <summary>The number of cover-data entries in the patch.</summary>
    public int CoverDataCount;

    /// <summary>Points to the cover-data size record.</summary>
    public ChunkSizeInfo* CoverDataSizeP;
    /// <summary>Points to the RLE control-data size record.</summary>
    public ChunkSizeInfo* RleControlDataSizeP;
    /// <summary>Points to the RLE code-data size record.</summary>
    public ChunkSizeInfo* RleCodeDataSizeP;
    /// <summary>Points to the new-difference-data size record.</summary>
    public ChunkSizeInfo* NewDiffDataSizeP;
}
