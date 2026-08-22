#if NET8_0_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Native;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

#if USEWINDOWS
using ConventionCall = System.Runtime.CompilerServices.CallConvStdcall;
#else
using ConventionCall = System.Runtime.CompilerServices.CallConvCdecl;
#endif

namespace SharpHPatchZ;

public static partial class HPatch
{
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

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_memory")]
    public static unsafe int SharpHPatchZ_InitializeFromMemory(byte* dataP, long dataLength, HDiffInfo* signP)
    {
        try
        {
            HDiffInfo thisInfo = CreateInstance(CreateUnmanagedStreamWrapper);
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

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_filepath")]
    public static unsafe int SharpHPatchZ_InitializeFromFilePathAuto(void* pathP, HDiffInfo* signP)
    {
        try
        {
            string? filePath = StringExtension.GetManagedStringAuto(pathP);
            if (filePath == null)
            {
                throw ExceptionHelper.ThrowHDiffPathIsEmptyOrInvalid();
            }

            HDiffInfo thisInfo = CreateInstance(pos => CreateFileStreamWrapper(filePath, pos));
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_init_from_FILE")]
    public static unsafe int SharpHPatchZ_InitializeFromFILE(void* FILEP, HDiffInfo* signP)
    {
        try
        {
            if (FILEP == null)
            {
                throw ExceptionHelper.ThrowHDiffFILEDescriptorNull();
            }

            HDiffInfo thisInfo = CreateInstance(pos => CreateFileStreamWrapper(FILEP, pos));
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_patch_from_filepath")]
    public static unsafe int SharpHPatchZ_PatchFromFilePathAuto(
        void*             patchPathP,
        void*             inputPathP,
        void*             outputPathP,
        HDiffInfo*        infoP,
        PatchOptions*     optionsP,
        ProgressCallback* progressCallbackP)
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
            ProgressCallback progressCallback = progressCallbackP == null ? new ProgressCallback() : Unsafe.AsRef<ProgressCallback>(progressCallbackP);
            ref HDiffInfo info = ref infoP[0];

            return Patch(info,
                         pos => CreateFileStreamWrapper(patchPath, pos),
                         inputPath,
                         outputPath,
                         options,
                         progressCallback);
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_patch_from_FILE")]
    public static unsafe int SharpHPatchZ_PatchFromFILE(
        void*             FILEP,
        void*             inputPathP,
        void*             outputPathP,
        HDiffInfo*        infoP,
        PatchOptions*     optionsP,
        ProgressCallback* progressCallbackP)
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
            ProgressCallback progressCallback = progressCallbackP == null ? new ProgressCallback() : Unsafe.AsRef<ProgressCallback>(progressCallbackP);
            ref HDiffInfo info = ref infoP[0];

            return Patch(info,
                         pos => CreateFileStreamWrapper(FILEP, pos),
                         inputPath,
                         outputPath,
                         options,
                         progressCallback);
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }

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

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_get_last_errorA")]
    public static unsafe int SharpHPatchZ_GetLastErrorUtf8(byte* bufferA, int bufferLength, ExceptionHelper.LastErrorMessageType messageType)
        => ExceptionHelper.TryGetLastErrorMessageUtf8(new Span<byte>(bufferA, bufferLength), messageType);

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_get_last_errorW")]
    public static unsafe int SharpHPatchZ_GetLastErrorUnicode(char* bufferW, int bufferLength, ExceptionHelper.LastErrorMessageType messageType)
        => ExceptionHelper.TryGetLastErrorMessageUnicode(new Span<char>(bufferW, bufferLength), messageType);

    private static (Stream Stream, bool LeaveOpen) CreateFileStreamWrapper(string filePath, long position)
    {
        FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = position;
        return (stream, false);
    }

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
}
#endif
