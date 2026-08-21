using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DirectoryPatchMetadata : IMetadataInit
{
    public DirectoryPatchMetadata()
    {
        Init();
    }

    public void Init()
    {
        if (IsDisposed || IsInitialized)
        {
            return;
        }

        IsInitialized              = true;
        IsDisposed                 = false;
        MetadataType               = MetadataTypeConst.DirectoryPatchMetadataType;
        InputPathCountSizeInfoP    = MemoryAlloc.Alloc<EntryCountSizeInfo>(1, true);
        OutputPathCountSizeInfoP   = MemoryAlloc.Alloc<EntryCountSizeInfo>(1, true);
        SameFilePathCountSizeInfoP = MemoryAlloc.Alloc<EntryCountSizeInfo>(1, true);
        ExternSizeInfoP            = MemoryAlloc.Alloc<ExternSizeInfo>(1, true);
        HeadDataSizeP              = MemoryAlloc.Alloc<ChunkSizeInfo>(1, true);
        PatchMetadataP             = MemoryAlloc.Alloc<PatchMetadata>(1, true);
        ChecksumDataInfoP          = MemoryAlloc.Alloc<ChecksumDataInfo>(1, true);
    }

    public void Dispose()
    {
        if (IsDisposed || !IsInitialized)
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;
        MemoryAlloc.Free(InputPathCountSizeInfoP);
        MemoryAlloc.Free(OutputPathCountSizeInfoP);
        MemoryAlloc.Free(SameFilePathCountSizeInfoP);
        MemoryAlloc.Free(SameFilePathIndexPairP);

        MemoryAlloc.Free(InputPathListP);
        MemoryAlloc.Free(OutputPathListP);
        MemoryAlloc.Free(InputFileIndexListP);
        MemoryAlloc.Free(OutputFileIndexListP);
        MemoryAlloc.Free(OutputFileSizeListP);

        MemoryAlloc.Free(ExternSizeInfoP);

        MemoryAlloc.Free(HeadDataSizeP);
        MemoryAlloc.Free(PatchMetadataP);
        MemoryAlloc.Free(ChecksumDataInfoP);
        MemoryAlloc.Free(NewExecuteListP);
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

    public byte IsInputDir;
    public byte IsOutputDir;

    public EntryCountSizeInfo* InputPathCountSizeInfoP;
    public EntryCountSizeInfo* OutputPathCountSizeInfoP;
    public EntryCountSizeInfo* SameFilePathCountSizeInfoP;
    public FileIndexPair*      SameFilePathIndexPairP;

    public UnmanagedArray<Utf16UnmanagedString>* InputPathListP;
    public UnmanagedArray<Utf16UnmanagedString>* OutputPathListP;
    public int*                                  InputFileIndexListP;
    public int*                                  OutputFileIndexListP;
    public long*                                 OutputFileSizeListP;

    // Seems unused.
    // TODO: See original code to see what it is.
    public ExternSizeInfo* ExternSizeInfoP;

    // File Reference Chunk Info, including:
    // - Filename string chunks
    // - Checksum chunks
    public ChunkSizeInfo*    HeadDataSizeP;
    public PatchMetadata*    PatchMetadataP;
    public ChecksumDataInfo* ChecksumDataInfoP;
    public int*              NewExecuteListP;
}