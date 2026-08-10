#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Native;
// ReSharper disable InconsistentNaming

#if USEWINDOWS
using ConventionCall = System.Runtime.CompilerServices.CallConvStdcall;
#else
using ConventionCall = System.Runtime.CompilerServices.CallConvCdecl;
#endif

namespace SharpHPatchZ;

public static partial class HPatch
{
    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_read_header_signature_string")]
    public static unsafe int SharpHPatchZ_ReadHeaderSignatureString(void* signWP, int signWLen, HDiffMagic* magicTypeP, HDiffCompression* compressionTypeP, HDiffChecksum* checksumTypeP)
    {
        try
        {
            ReadOnlySpan<char>   signature          = new(signWP, signWLen);
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
    public static unsafe int SharpHPatchZ_InitializeFromFilePath(char* pathW, int pathWLength, HDiffInfo* signP)
    {
        try
        {
            if (pathW == null ||
                pathWLength == 0)
            {
                ExceptionHelper.ThrowHDiffPathIsEmptyOrInvalid();
            }

            string    filePath = new ReadOnlySpan<char>(pathW, pathWLength).Trim('\0').ToString();
            HDiffInfo thisInfo = CreateInstance(CreateFileStreamWrapper);
            thisInfo.CopyTo(signP);

            return 0;

            (Stream Stream, bool LeaveOpen) CreateFileStreamWrapper(long position)
            {
                FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Position = position;
                return (stream, false);
            }
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
                ExceptionHelper.ThrowHDiffFILEDescriptorNull();
            }

            HDiffInfo thisInfo = CreateInstance(CreateFileStreamWrapper);
            thisInfo.CopyTo(signP);

            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }

        (Stream Stream, bool LeaveOpen) CreateFileStreamWrapper(long position)
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
                ExceptionHelper.ThrowHDiffIOException(ex);
                throw;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(ConventionCall)], EntryPoint = "shpz_free_diff_info")]
    public static unsafe int SharpHPatchZ_FreeDiffInfo(HDiffInfo* ptr)
    {
        try
        {
            if (ptr == null)
            {
                ExceptionHelper.ThrowHDiffInfoIsNull();
            }

            MemoryAlloc.Free(ptr->MetadataP);
            return 0;
        }
        catch (Exception ex)
        {
            return ExceptionHelper.TryGetReturnCodeFromError(ex);
        }
    }
}
#endif
