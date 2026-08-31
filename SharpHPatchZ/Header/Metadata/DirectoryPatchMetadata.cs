using System.Runtime.InteropServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Contains file lists, index mappings, and chunk information for a directory patch.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DirectoryPatchMetadata : IMetadataInit
{
    /// <summary>Initializes a new <see cref="DirectoryPatchMetadata"/> and its owned records.</summary>
    public DirectoryPatchMetadata()
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

    /// <inheritdoc/>
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
        MemoryAlloc.Free(InputFileSizeListP);
        MemoryAlloc.Free(OutputFileIndexListP);
        MemoryAlloc.Free(OutputFileSizeListP);
        MemoryAlloc.Free(OutputFileHashesListP);

        MemoryAlloc.Free(ExternSizeInfoP);

        MemoryAlloc.Free(HeadDataSizeP);
        MemoryAlloc.Free(PatchMetadataP);
        MemoryAlloc.Free(ChecksumDataInfoP);
        MemoryAlloc.Free(NewExecuteListP);
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

    /// <summary>Indicates whether the patch input is a directory.</summary>
    public byte IsInputDir;
    /// <summary>Indicates whether the patch output is a directory.</summary>
    public byte IsOutputDir;

    /// <summary>Points to input-path count and size information.</summary>
    public EntryCountSizeInfo* InputPathCountSizeInfoP;
    /// <summary>Points to output-path count and size information.</summary>
    public EntryCountSizeInfo* OutputPathCountSizeInfoP;
    /// <summary>Points to unchanged-path count and size information.</summary>
    public EntryCountSizeInfo* SameFilePathCountSizeInfoP;
    /// <summary>Points to mappings between unchanged input and output paths.</summary>
    public FileIndexPair*      SameFilePathIndexPairP;

    /// <summary>Points to the input path list.</summary>
    public UnmanagedArray<Utf16UnmanagedString>* InputPathListP;
    /// <summary>Points to the output path list.</summary>
    public UnmanagedArray<Utf16UnmanagedString>* OutputPathListP;
    /// <summary>Points to indexes of referenced input files.</summary>
    public UnmanagedArray<int>*                  InputFileIndexListP;
    /// <summary>Points to sizes of referenced input files, when present.</summary>
    public UnmanagedArray<long>*                 InputFileSizeListP;
    /// <summary>Points to indexes of referenced output files.</summary>
    public UnmanagedArray<int>*                  OutputFileIndexListP;
    /// <summary>Points to sizes of referenced output files.</summary>
    public UnmanagedArray<long>*                 OutputFileSizeListP;
    /// <summary>Points to hashes of referenced output files, when present.</summary>
    public UnmanagedArray<long>*                 OutputFileHashesListP;

    // Seems unused.
    // TODO: See original code to see what it is.
    /// <summary>Points to external-data size information.</summary>
    public ExternSizeInfo* ExternSizeInfoP;

    // File Reference Chunk Info, including:
    // - Filename string chunks
    // - Checksum chunks
    /// <summary>Points to directory header-data size information.</summary>
    public ChunkSizeInfo*       HeadDataSizeP;
    /// <summary>Points to the embedded single-file patch metadata.</summary>
    public PatchMetadata*       PatchMetadataP;
    /// <summary>Points to the directory-patch checksum data.</summary>
    public ChecksumDataInfo*    ChecksumDataInfoP;
    /// <summary>Points to indexes of output entries that should be executable.</summary>
    public UnmanagedArray<int>* NewExecuteListP;
}
