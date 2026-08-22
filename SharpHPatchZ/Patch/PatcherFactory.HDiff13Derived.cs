using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Compression;
using SharpHPatchZ.IO.Reader;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHPatchZ.Patch;

internal static partial class PatcherFactory
{
    private static class HDiff13Derived
    {
        public static HDiff13DerivedPatcher Create(
            ref HDiffInfo    info,
            CreateStream     createPatchStream,
            PatchOptions     options,
            ProgressCallback progressCallback)
        {
            GetPatchContextInfos(ref info,
                                 out long coverDataOffset,
                                 out long rleCtrlDataOffset,
                                 out long rleCodeDataOffset,
                                 out long newDataOffset);

            DiffReadersContext context = CreateReaderContext(info.CompressionType,
                                                             options,
                                                             createPatchStream(coverDataOffset),
                                                             createPatchStream(rleCtrlDataOffset),
                                                             createPatchStream(rleCodeDataOffset),
                                                             createPatchStream(newDataOffset));

            return new HDiff13DerivedPatcher(context.Cover,
                                             context.RleCtrl,
                                             context.RleCode,
                                             context.NewData,
                                             info,
                                             options,
                                             progressCallback);
        }

        public static async Task<HDiff13DerivedPatcher> CreateAsync(
            HDiffInfo         info,
            CreateStreamAsync createPatchStreamAsync,
            PatchOptions      options,
            ProgressCallback  progressCallback,
            CancellationToken token)
        {
            GetPatchContextInfos(ref info,
                                 out long coverDataOffset,
                                 out long rleCtrlDataOffset,
                                 out long rleCodeDataOffset,
                                 out long newDataOffset);

            DiffReadersContext context = CreateReaderContext(info.CompressionType,
                                                             options,
                                                             await createPatchStreamAsync(coverDataOffset,   token),
                                                             await createPatchStreamAsync(rleCtrlDataOffset, token),
                                                             await createPatchStreamAsync(rleCodeDataOffset, token),
                                                             await createPatchStreamAsync(newDataOffset,     token));

            return new HDiff13DerivedPatcher(context.Cover,
                                             context.RleCtrl,
                                             context.RleCode,
                                             context.NewData,
                                             info,
                                             options,
                                             progressCallback);
        }

        private static DiffReadersContext CreateReaderContext(HDiffCompression         compType,
                                                              PatchOptions             options,
                                                              ValueTuple<Stream, bool> coverCtx,
                                                              ValueTuple<Stream, bool> rleCtrlCtx,
                                                              ValueTuple<Stream, bool> rleCodeCtx,
                                                              ValueTuple<Stream, bool> newDataCtx)
        {
            int bufferSize = options.ReaderBufferSize;

            Stream decCoverStream      = DecompressStreamFactory.Create(compType, coverCtx.Item1,   coverCtx.Item2);
            Stream decRleControlStream = DecompressStreamFactory.Create(compType, rleCtrlCtx.Item1, rleCtrlCtx.Item2);
            Stream decRleCodeStream    = DecompressStreamFactory.Create(compType, rleCodeCtx.Item1, rleCodeCtx.Item2);
            Stream decNewDataStream    = DecompressStreamFactory.Create(compType, newDataCtx.Item1, newDataCtx.Item2);

            BittableStreamReader coverReader   = new(decCoverStream, bufferSize, coverCtx.Item2);
            BittableStreamReader rleCtrlReader = new(decRleControlStream, bufferSize, rleCtrlCtx.Item2);
            BittableStreamReader rleCodeReader = new(decRleCodeStream, bufferSize, rleCodeCtx.Item2);
            BittableStreamReader newDataReader = new(decNewDataStream, bufferSize, newDataCtx.Item2);

            return new DiffReadersContext(coverReader, rleCtrlReader, rleCodeReader, newDataReader);
        }

        private static unsafe void GetPatchContextInfos(ref HDiffInfo info,
                                                        out long      coverDataOffset,
                                                        out long      rleCtrlDataOffset,
                                                        out long      rleCodeDataOffset,
                                                        out long      newDataOffset)
        {
            ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();

            ChunkSizeInfo* coverDataSizeP      = patchMetadata.CoverDataSizeP;
            ChunkSizeInfo* rleControlDataSizeP = patchMetadata.RleControlDataSizeP;
            ChunkSizeInfo* rleCodeDataSizeP    = patchMetadata.RleCodeDataSizeP;

            coverDataOffset = patchMetadata.DiffDataOffset;
            rleCtrlDataOffset = coverDataOffset + (coverDataSizeP->CompressedSize > 0 ? coverDataSizeP->CompressedSize : coverDataSizeP->Size);
            rleCodeDataOffset = rleCtrlDataOffset + (rleControlDataSizeP->CompressedSize > 0 ? rleControlDataSizeP->CompressedSize : rleControlDataSizeP->Size);
            newDataOffset = rleCodeDataOffset + (rleCodeDataSizeP->CompressedSize > 0 ? rleCodeDataSizeP->CompressedSize : rleCodeDataSizeP->Size);
        }

        private record struct DiffReadersContext(
            BittableStreamReader Cover,
            BittableStreamReader RleCtrl,
            BittableStreamReader RleCode,
            BittableStreamReader NewData);
    }
}
