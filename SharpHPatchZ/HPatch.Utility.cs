using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ;

public static partial class HPatch
{
    /// <param name="info">A context and information struct for the <see cref="PatchMetadata"/> to be retrieved from.</param>
    extension(ref HDiffInfo info)
    {
        /// <summary>
        /// Try retrieve a <see cref="PatchMetadata"/> struct from a patch context.
        /// </summary>
        /// <param name="patchMetadata">A retrieved struct of <see cref="PatchMetadata"/> containing the main information about the patch file.</param>
        /// <returns>
        /// Returns <see langword="true"/> if <paramref name="patchMetadata"/> is successfully retrieved. Otherwise, <see langword="false"/> if the context struct is invalid or corrupted.
        /// </returns>
        public bool TryGetPatchMetadata(out PatchMetadata patchMetadata)
        {
            Unsafe.SkipInit(out patchMetadata);

            ref PatchMetadata patchMetadataRef = ref info.GetPatchMetadata();
            if (Unsafe.IsNullRef(ref patchMetadataRef))
            {
                return false;
            }

            patchMetadata = patchMetadataRef;
            return true;
        }

        /// <summary>
        /// Try retrieve a <see cref="DirectoryPatchMetadata"/> struct from a patch context.
        /// </summary>
        /// <param name="patchMetadata">A retrieved struct of <see cref="DirectoryPatchMetadata"/> containing the main information about the patch file.</param>
        /// <returns>
        /// Returns <see langword="true"/> if <paramref name="patchMetadata"/> is successfully retrieved.
        /// Otherwise, <see langword="false"/> if the patch context does not contain <see cref="DirectoryPatchMetadata"/> struct.
        /// </returns>
        public bool TryGetDirectoryPatchMetadata(out DirectoryPatchMetadata patchMetadata)
        {
            Unsafe.SkipInit(out patchMetadata);

            ref DirectoryPatchMetadata patchMetadataRef = ref info.MetadataAs<DirectoryPatchMetadata>();
            if (Unsafe.IsNullRef(ref patchMetadataRef))
            {
                return false;
            }

            patchMetadata = patchMetadataRef;
            return true;
        }

        /// <summary>
        /// Try retrieves both total Input and Output size from a patch context.
        /// </summary>
        /// <param name="totalInputSize">The total size of an Input File/Directory.</param>
        /// <param name="totalOutputSize">The total size of an Output File/Directory.</param>
        /// <returns>
        /// Returns <see langword="true"/> if both <paramref name="totalInputSize"/> and <paramref name="totalOutputSize"/> are successfully retrieved.
        /// Otherwise, <see langword="false"/> if the patch context is invalid or corrupted.
        /// </returns>
        public bool TryGetDiffSizeInfo(out long totalInputSize,
                                       out long totalOutputSize)
        {
            return info.TryGetDiffSizeInfo(out totalInputSize,
                                           out totalOutputSize,
                                           out _,
                                           out _);
        }

        /// <summary>
        /// Try retrieves both total Input and Output size from a patch context.
        /// </summary>
        /// <param name="totalInputSize">The total size of an Input File/Directory.</param>
        /// <param name="totalOutputSize">The total size of an Output File/Directory.</param>
        /// <param name="diffOnlyInputSize">The diff size of an Output File/Directory. The value only be returned if the Patch Metadata is a <see cref="DirectoryPatchMetadata"/> kind.</param>
        /// <param name="diffOnlyOutputSize">The diff size of an Output File/Directory. The value only be returned if the Patch Metadata is a <see cref="DirectoryPatchMetadata"/> kind.</param>
        /// <returns>
        /// Returns <see langword="true"/> if all <paramref name="totalInputSize"/>, <paramref name="totalOutputSize"/>, <paramref name="diffOnlyInputSize"/> and <paramref name="diffOnlyOutputSize"/> are successfully retrieved.
        /// Otherwise, <see langword="false"/> if the patch context is invalid or corrupted.
        /// </returns>
        public unsafe bool TryGetDiffSizeInfo(out long totalInputSize,
                                              out long totalOutputSize,
                                              out long diffOnlyInputSize,
                                              out long diffOnlyOutputSize)
        {
            Unsafe.SkipInit(out totalInputSize);
            Unsafe.SkipInit(out totalOutputSize);
            Unsafe.SkipInit(out diffOnlyInputSize);
            Unsafe.SkipInit(out diffOnlyOutputSize);

            ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();
            if (Unsafe.IsNullRef(ref patchMetadata))
            {
                return false;
            }

            if (info.TryGetDirectoryPatchMetadata(out DirectoryPatchMetadata dirPatchMetadata))
            {
                // In directory patch, the actual total size must be added by
                // Input/OutputPathCountSizeInfoP + SameFilePathCountSizeInfoP as
                // the DiffOld/NewSize only contains the diff size.
                totalInputSize = dirPatchMetadata.InputPathCountSizeInfoP->Size +
                                 dirPatchMetadata.SameFilePathCountSizeInfoP->Size;
                totalOutputSize = dirPatchMetadata.OutputPathCountSizeInfoP->Size +
                                  dirPatchMetadata.SameFilePathCountSizeInfoP->Size;

                diffOnlyInputSize  = patchMetadata.DiffOldSize;
                diffOnlyOutputSize = patchMetadata.DiffNewSize;

                return true;
            }

            totalInputSize  = patchMetadata.DiffOldSize;
            totalOutputSize = patchMetadata.DiffNewSize;
            return true;
        }
    }

    /// <summary>
    /// Try retrieves both total Input and Output size from a path of the patch file.
    /// </summary>
    /// <param name="patchFilePath">The path of the patch file.</param>
    /// <param name="totalInputSize">The total size of an Input File/Directory.</param>
    /// <param name="totalOutputSize">The total size of an Output File/Directory.</param>
    /// <returns>
    /// Returns <see langword="true"/> if both <paramref name="totalInputSize"/> and <paramref name="totalOutputSize"/> are successfully retrieved.
    /// Otherwise, <see langword="false"/> if the patch context or the file is invalid or corrupted.
    /// </returns>
    public static bool TryGetDiffSizeInfo(
        string   patchFilePath,
        out long totalInputSize,
        out long totalOutputSize)
    {
        HDiffInfo info = CreateInstance(CreateStream);
        try
        {
            return info.TryGetDiffSizeInfo(out totalInputSize,
                                           out totalOutputSize);
        }
        finally
        {
            info.Dispose();
        }

        (Stream, bool) CreateStream(long pos)
        {
            FileStream fileStream = File.Open(patchFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileStream.Position = pos;
            return (fileStream, false);
        }
    }

    /// <summary>
    /// Try retrieves both total Input and Output size from a <see cref="Stream"/> of the patch data.
    /// </summary>
    /// <param name="createStream">A factory delegate which creates the <see cref="Stream"/> instance of the patch file.</param>
    /// <param name="totalInputSize">The total size of an Input File/Directory.</param>
    /// <param name="totalOutputSize">The total size of an Output File/Directory.</param>
    /// <returns>
    /// Returns <see langword="true"/> if both <param name="totalInputSize"/> and <param name="totalOutputSize"/> are successfully retrieved.
    /// Otherwise, <see langword="false"/> if the patch context or the file stream is invalid or corrupted.
    /// </returns>
    public static bool TryGetDiffSizeInfo(CreateStream createStream,
                                          out long     totalInputSize,
                                          out long     totalOutputSize)
    {
        HDiffInfo info = CreateInstance(createStream);
        try
        {
            return info.TryGetDiffSizeInfo(out totalInputSize,
                                           out totalOutputSize);
        }
        finally
        {
            info.Dispose();
        }
    }

    /// <summary>
    /// Try retrieves both total Input and Output size from a <see cref="Stream"/> of the patch data.
    /// </summary>
    /// <param name="stream">A source <see cref="Stream"/> instance of the patch data.</param>
    /// <param name="totalInputSize">The total size of an Input File/Directory.</param>
    /// <param name="totalOutputSize">The total size of an Output File/Directory.</param>
    /// <returns>
    /// Returns <see langword="true"/> if both <param name="totalInputSize"/> and <param name="totalOutputSize"/> are successfully retrieved.
    /// Otherwise, <see langword="false"/> if the patch context or the file stream is invalid or corrupted.
    /// </returns>
    public static bool TryGetDiffSizeInfo(Stream   stream,
                                          out long totalInputSize,
                                          out long totalOutputSize)
    {
        return TryGetDiffSizeInfo(CreateStream,
                                  out totalInputSize,
                                  out totalOutputSize);

        (Stream, bool) CreateStream(long pos)
        {
            stream.Position = pos;
            return (stream, true);
        }
    }

    /// <summary>
    /// Try gets the <see cref="Span{T}"/> from an <see cref="UnmanagedArray{T}"/> instance.
    /// </summary>
    /// <typeparam name="T">The type of the content struct</typeparam>
    /// <param name="array">The unmanaged array to get the <see cref="Span{T}"/> from.</param>
    /// <returns>
    /// Returns a non-empty <see cref="Span{T}"/> if the array is valid.
    /// Otherwise, returns an empty <see cref="Span{T}"/> if the array is invalid.
    /// </returns>
    public static unsafe Span<T> TryGetUnmanagedArraySpan<T>(UnmanagedArray<T>* array)
        where T : unmanaged
        => array == null ||
           (MetadataExtension.TryGetMetadataType(array, out MetadataTypeConst metadataType) && metadataType != MetadataTypeConst.UnmanagedArrayType)
            ? Span<T>.Empty : array->GetSpan();

    /// <summary>
    /// Try gets the <see cref="Span{T}"/> from an <see cref="UnmanagedArray{T}"/> instance.
    /// </summary>
    /// <typeparam name="T">The type of the content struct</typeparam>
    /// <param name="array">The unmanaged array to get the <see cref="Span{T}"/> from.</param>
    /// <returns>
    /// Returns a non-empty <see cref="Span{T}"/> if the array is valid.
    /// Otherwise, returns an empty <see cref="Span{T}"/> if the array is invalid.
    /// </returns>
    public static unsafe Span<T> TryGetUnmanagedArraySpan<T>(this ref UnmanagedArray<T> array)
        where T : unmanaged
        => TryGetUnmanagedArraySpan((UnmanagedArray<T>*)Unsafe.AsPointer(ref array));
}