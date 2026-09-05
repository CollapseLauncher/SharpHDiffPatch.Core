using System.IO;
using System.Threading.Tasks;

#if NET8_0_OR_GREATER
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.Native;
#endif

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

#if NET8_0_OR_GREATER
#if USEWINDOWS
using ConventionCall = System.Runtime.CompilerServices.CallConvStdcall;
#else
using ConventionCall = System.Runtime.CompilerServices.CallConvCdecl;
#endif
#endif

namespace SharpHPatchZ;

public static partial class HPatch
{
#if NET8_0_OR_GREATER
    /// <summary>Parses a zero-terminated UTF-8 or UTF-16 patch signature for an unmanaged caller.</summary>
    /// <param name="signatureP">A pointer to the zero-terminated signature <see cref="string"/>.</param>
    /// <param name="magicTypeP">A pointer that receives the <see cref="HDiffMagic"/>.</param>
    /// <param name="compressionTypeP">A pointer that receives the <see cref="HDiffCompression"/>.</param>
    /// <param name="checksumTypeP">A pointer that receives the <see cref="HDiffChecksum"/>.</param>
    /// <returns><c>0</c> if <paramref name="signatureP"/> is parsed successfully. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_read_header_signature_string")]
    public static unsafe int SharpHPatchZ_ReadHeaderSignatureStringAuto(void* signatureP, HDiffMagic* magicTypeP, HDiffCompression* compressionTypeP, HDiffChecksum* checksumTypeP)
    {
        char[]? signatureWideBuffer = null;
        try
        {
            string? signature = StringExtension.GetManagedStringAuto(signatureP);

            ref HDiffMagic       magicTypeRef       = ref magicTypeP[0];
            ref HDiffCompression compressionTypeRef = ref compressionTypeP[0];
            ref HDiffChecksum    checksumTypeRef    = ref checksumTypeP[0];

            HeaderReader.ReadBasicHeaderSignature(signature,
                                                  out magicTypeRef,
                                                  out compressionTypeRef,
                                                  out checksumTypeRef);
            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
        finally
        {
            if (signatureWideBuffer != null) ArrayPool<char>.Shared.Return(signatureWideBuffer);
        }
    }

    /// <summary>Initializes an <see cref="HDiffInfo"/> from an unmanaged memory buffer.</summary>
    /// <param name="dataP">A pointer to the patch data.</param>
    /// <param name="dataLength">The patch-data length, in bytes.</param>
    /// <param name="signP">A pointer that receives the initialized <see cref="HDiffInfo"/>.</param>
    /// <param name="initializeOptionsP">A pointer to <see cref="InitializeOptions"/>, or <see langword="null"/> to use defaults.</param>
    /// <returns><c>0</c> if initialization succeeds. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_memory")]
    public static unsafe int SharpHPatchZ_InitializeFromMemory(byte* dataP, long dataLength, HDiffInfo* signP, InitializeOptions* initializeOptionsP)
    {
        try
        {
            InitializeOptions initializeOptions = initializeOptionsP != null ? *initializeOptionsP : default;
            HDiffInfo thisInfo = CreateInstance(CreateUnmanagedStreamWrapper, initializeOptions);
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }

        (Stream Stream, bool LeaveOpen) CreateUnmanagedStreamWrapper(long position)
        {
            UnmanagedMemoryStream stream = new(dataP, dataLength);
            stream.Position = position;
            return (stream, false);
        }
    }

    /// <summary>Initializes an <see cref="HDiffInfo"/> from a zero-terminated UTF-8 or UTF-16 file path.</summary>
    /// <param name="pathP">A pointer to the zero-terminated patch path.</param>
    /// <param name="signP">A pointer that receives the initialized <see cref="HDiffInfo"/>.</param>
    /// <param name="initializeOptionsP">A pointer to <see cref="InitializeOptions"/>, or <see langword="null"/> to use defaults.</param>
    /// <returns><c>0</c> if initialization succeeds. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_filepath")]
    public static unsafe int SharpHPatchZ_InitializeFromFilePathAuto(void* pathP, HDiffInfo* signP, InitializeOptions* initializeOptionsP)
    {
        try
        {
            string? filePath = StringExtension.GetManagedStringAuto(pathP);
            if (filePath == null)
            {
                throw ExceptionHelper.ThrowHDiffPathIsEmptyOrInvalid();
            }

            InitializeOptions initializeOptions = initializeOptionsP != null ? *initializeOptionsP : default;
            HDiffInfo thisInfo = CreateInstance(pos => CreateFileStreamWrapper(filePath, pos), initializeOptions);
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    /// <summary>Initializes an <see cref="HDiffInfo"/> from a native <c>FILE</c> descriptor.</summary>
    /// <param name="FILEP">A pointer to the native <c>FILE</c> descriptor.</param>
    /// <param name="signP">A pointer that receives the initialized <see cref="HDiffInfo"/>.</param>
    /// <param name="initializeOptionsP">A pointer to <see cref="InitializeOptions"/>, or <see langword="null"/> to use defaults.</param>
    /// <returns><c>0</c> if initialization succeeds. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_FILE")]
    public static unsafe int SharpHPatchZ_InitializeFromFILE(void* FILEP, HDiffInfo* signP, InitializeOptions* initializeOptionsP)
    {
        try
        {
            if (FILEP == null)
            {
                throw ExceptionHelper.ThrowHDiffFILEDescriptorNull();
            }

            InitializeOptions initializeOptions = initializeOptionsP != null ? *initializeOptionsP : default;
            HDiffInfo         thisInfo = CreateInstance(pos => CreateFileStreamWrapper(FILEP, pos), initializeOptions);
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    /// <summary>Applies a patch identified by zero-terminated UTF-8 or UTF-16 file-system paths.</summary>
    /// <param name="patchPathP">A pointer to the zero-terminated patch-file path.</param>
    /// <param name="inputPathP">A pointer to the zero-terminated input path.</param>
    /// <param name="outputPathP">A pointer to the zero-terminated output path.</param>
    /// <param name="infoP">A pointer to an initialized <see cref="HDiffInfo"/>.</param>
    /// <param name="optionsP">A pointer to <see cref="PatchOptions"/>, or <see langword="null"/> to use defaults.</param>
    /// <param name="progressCallbackP">A pointer to a <see cref="ProcessedBytesCallback"/>, or <see langword="null"/> for no callback.</param>
    /// <returns><c>0</c> if patching succeeds. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_patch_from_filepath")]
    public static unsafe int SharpHPatchZ_PatchFromFilePathAuto(
        void*         patchPathP,
        void*         inputPathP,
        void*         outputPathP,
        HDiffInfo*    infoP,
        PatchOptions* optionsP,
        void*         progressCallbackP)
    {
        string? patchPath  = StringExtension.GetManagedStringAuto(patchPathP);
        string? inputPath  = StringExtension.GetManagedStringAuto(inputPathP);
        string? outputPath = StringExtension.GetManagedStringAuto(outputPathP);

        try
        {
            if (patchPath == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(patchPathP));
            if (inputPath == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(inputPathP));
            if (outputPath == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(outputPathP));
            if (infoP == null) throw ExceptionHelper.ThrowHDiffInfoNotAllocated();

            PatchOptions options = optionsP == null ? new PatchOptions() : Unsafe.AsRef<PatchOptions>(optionsP); // Copy
            ref HDiffInfo info = ref infoP[0];

            return Patch(info,
                         pos => CreateFileStreamWrapper(patchPath, pos),
                         inputPath,
                         outputPath,
                         options,
                         (totalWritten, totalSize, written) => ProgressCallbackWrapper(progressCallbackP, totalWritten, totalSize, written));
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    /// <summary>Applies a patch read from a native <c>FILE</c> descriptor.</summary>
    /// <param name="FILEP">A pointer to the native patch-file descriptor.</param>
    /// <param name="inputPathP">A pointer to the zero-terminated input path.</param>
    /// <param name="outputPathP">A pointer to the zero-terminated output path.</param>
    /// <param name="infoP">A pointer to an initialized <see cref="HDiffInfo"/>.</param>
    /// <param name="optionsP">A pointer to <see cref="PatchOptions"/>, or <see langword="null"/> to use defaults.</param>
    /// <param name="progressCallbackP">A pointer to a <see cref="ProcessedBytesCallback"/>, or <see langword="null"/> for no callback.</param>
    /// <returns><c>0</c> if patching succeeds. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_patch_from_FILE")]
    public static unsafe int SharpHPatchZ_PatchFromFILE(
        void*         FILEP,
        void*         inputPathP,
        void*         outputPathP,
        HDiffInfo*    infoP,
        PatchOptions* optionsP,
        void*         progressCallbackP)
    {
        string? inputPath  = StringExtension.GetManagedStringAuto(inputPathP);
        string? outputPath = StringExtension.GetManagedStringAuto(outputPathP);

        try
        {
            if (FILEP == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(FILEP));
            if (inputPath == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(inputPathP));
            if (outputPath == null) throw ExceptionHelper.ThrowHDiffArgumentNull(nameof(outputPathP));
            if (infoP == null) throw ExceptionHelper.ThrowHDiffInfoNotAllocated();

            PatchOptions options = optionsP == null ? new PatchOptions() : Unsafe.AsRef<PatchOptions>(optionsP); // Copy
            ref HDiffInfo info = ref infoP[0];

            return Patch(info,
                         pos => CreateFileStreamWrapper(FILEP, pos),
                         inputPath,
                         outputPath,
                         options,
                         (totalWritten, totalSize, written) => ProgressCallbackWrapper(progressCallbackP, totalWritten, totalSize, written));
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    /// <summary>Releases metadata allocated for an <see cref="HDiffInfo"/>.</summary>
    /// <param name="ptr">A pointer to the <see cref="HDiffInfo"/> to release.</param>
    /// <returns><c>0</c> if the metadata is released. Otherwise, a library error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_free_diff_info")]
    public static unsafe int SharpHPatchZ_FreeDiffInfo(HDiffInfo* ptr)
    {
        try
        {
            if (ptr == null)
            {
                throw ExceptionHelper.ThrowHDiffInfoNotAllocated();
            }

            MemoryAlloc.Free(ptr->MetadataP);
            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    /// <summary>Gets the <see cref="PatchMetadata"/> associated with an <see cref="HDiffInfo"/>.</summary>
    /// <param name="infoP">A pointer to an initialized <see cref="HDiffInfo"/>.</param>
    /// <returns>A pointer to the <see cref="PatchMetadata"/>.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_util_get_patch_metadata")]
    public static unsafe PatchMetadata* SharpHPatchZ_TryGetPatchMetadata(HDiffInfo* infoP)
        => (PatchMetadata*)Unsafe.AsPointer(ref infoP[0].GetPatchMetadata());

    /// <summary>Gets the <see cref="DirectoryPatchMetadata"/> associated with an <see cref="HDiffInfo"/>.</summary>
    /// <param name="infoP">A pointer to an initialized <see cref="HDiffInfo"/>.</param>
    /// <returns>A pointer to the <see cref="DirectoryPatchMetadata"/>, or <see langword="null"/> when it is unavailable.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_util_get_directory_patch_metadata")]
    public static unsafe DirectoryPatchMetadata* SharpHPatchZ_TryGetDirectoryPatchMetadata(HDiffInfo* infoP)
        => (DirectoryPatchMetadata*)Unsafe.AsPointer(ref infoP[0].MetadataAs<DirectoryPatchMetadata>());

    /// <summary>Writes the last recorded error to a zero-terminated UTF-8 buffer.</summary>
    /// <param name="bufferA">A pointer to the destination buffer.</param>
    /// <param name="bufferLength">The buffer length, in bytes.</param>
    /// <param name="messageType">The <see cref="ExceptionHelper.LastErrorMessageType"/> details to include.</param>
    /// <returns>The number of bytes written. Otherwise, <c>-1</c> if <paramref name="bufferA"/> is too small.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_get_last_errorA")]
    public static unsafe int SharpHPatchZ_GetLastErrorUtf8(byte* bufferA, int bufferLength, ExceptionHelper.LastErrorMessageType messageType)
        => ExceptionHelper.TryGetLastErrorMessageUtf8(new Span<byte>(bufferA, bufferLength), messageType);

    /// <summary>Writes the last recorded error to a zero-terminated UTF-16 buffer.</summary>
    /// <param name="bufferW">A pointer to the destination buffer.</param>
    /// <param name="bufferLength">The buffer length, in characters.</param>
    /// <param name="messageType">The <see cref="ExceptionHelper.LastErrorMessageType"/> details to include.</param>
    /// <returns>The number of characters written. Otherwise, <c>-1</c> if <paramref name="bufferW"/> is too small.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_get_last_errorW")]
    public static unsafe int SharpHPatchZ_GetLastErrorUnicode(char* bufferW, int bufferLength, ExceptionHelper.LastErrorMessageType messageType)
        => ExceptionHelper.TryGetLastErrorMessageUnicode(new Span<char>(bufferW, bufferLength), messageType);

    private static unsafe (Stream Stream, bool LeaveOpen) CreateFileStreamWrapper(void* FILEP, long position)
    {
        try
        {
            SafeFileHandle fileHandle = PInvoke.GetSafeFileHandleFromFILE(FILEP);
            FileStream     stream     = new(fileHandle, FileAccess.Read);
            stream.Position = position;
            return (stream, true);
        }
        catch (IOException ex)
        {
            throw ExceptionHelper.ThrowHDiffIOException(ex);
        }
    }
#endif

    private static unsafe void ProgressCallbackWrapper(void* delegateP, long totalWritten, long totalSize, int written)
    {
        if (delegateP != null)
            ((delegate* unmanaged[Cdecl]<long, long, int, void>)delegateP)(totalWritten, totalSize, written);
    }

    private static (Stream Stream, bool LeaveOpen) CreateFileStreamWrapper(string filePath, long position)
    {
        FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return (stream, false);
    }

    private static ValueTask<(Stream Stream, bool LeaveOpen)> CreateFileStreamWrapperAsync(string filePath, long position)
    {
        FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return new ValueTask<(Stream Stream, bool LeaveOpen)>((stream, false));
    }
}
