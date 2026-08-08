using System;
using System.Runtime.CompilerServices;
using SharpHDiffPatch.New.Header.Metadata;

namespace SharpHDiffPatch.New.Extension;

internal static class MetadataExtension
{
    extension<T>(ref T metadata) where T : unmanaged
    {
        public bool TryGetMetadataType(out MetadataTypeConst metadataType)
        {
            ref MetadataTypeConst metadataTypeCopy = ref Unsafe.As<T, MetadataTypeConst>(ref metadata);
            metadataType = metadataTypeCopy;

            return IsTypeDefined(metadataType);
        }

        public bool TryDisposeIfMetadataType()
        {
            if (!metadata.TryGetMetadataType(out MetadataTypeConst metadataType))
            {
                return false;
            }

            switch (metadataType)
            {
                case MetadataTypeConst.ChecksumDataInfoType:
                    Unsafe.As<T, ChecksumDataInfo>(ref metadata).Dispose();
                    break;
                case MetadataTypeConst.HeaderDirectoryPatchMetadataType:
                    Unsafe.As<T, HeaderDirectoryPatchMetadata>(ref metadata).Dispose();
                    break;
                case MetadataTypeConst.PatchMetadataType:
                    Unsafe.As<T, PatchMetadata>(ref metadata).Dispose();
                    break;
                case MetadataTypeConst.UnmanagedArrayType:
                    Unsafe.As<T, UnmanagedArray<byte>>(ref metadata).Dispose();
                    break;
                case MetadataTypeConst.Utf16UnmanagedStringType:
                    Unsafe.As<T, Utf16UnmanagedString>(ref metadata).Dispose();
                    break;
                case MetadataTypeConst.IsMetadataType:
                default:
                    return false;
            }

            return true;
        }
    }

    public static bool IsTypeDefined(MetadataTypeConst metadataType)
    {
#if NET6_0_OR_GREATER
        return Enum.IsDefined(metadataType)
#else
        return Enum.IsDefined(typeof(MetadataTypeConst), metadataType)
#endif
               && metadataType.HasFlag(MetadataTypeConst.IsMetadataType);
    }
}
