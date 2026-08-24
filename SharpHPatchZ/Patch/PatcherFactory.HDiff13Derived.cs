using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Compression;
using SharpHPatchZ.IO.Reader;
using System;
using System.Collections.Generic;
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
            ExecutionPlan executionPlan = CreateExecutionPlan(info.MagicType, options);
            GetPatchContextInfos(ref info,
                                 out long coverDataOffset,
                                 out long rleCtrlDataOffset,
                                 out long rleCodeDataOffset,
                                 out long newDataOffset);
            PatchMetadata patchMetadata = info.GetPatchMetadata();
            DiffReadersContext context = CreateReaderContext(info.CompressionType,
                                                             options,
                                                             patchMetadata,
                                                             createPatchStream(coverDataOffset),
                                                             createPatchStream(rleCtrlDataOffset),
                                                             createPatchStream(rleCodeDataOffset),
                                                             createPatchStream(newDataOffset),
                                                             executionPlan.MaxDecompressionWorkers);

            return new HDiff13DerivedPatcher(context.Cover,
                                             context.RleCtrl,
                                             context.RleCode,
                                             context.NewData,
                                             info,
                                             options,
                                             progressCallback,
                                             executionPlan.PatchWorkerBudget - context.DecompressionWorkers,
                                             executionPlan.CopyWorkers,
                                             executionPlan.RunCopyConcurrently);
        }

        public static async Task<HDiff13DerivedPatcher> CreateAsync(
            HDiffInfo         info,
            CreateStreamAsync createPatchStreamAsync,
            PatchOptions      options,
            ProgressCallback  progressCallback,
            CancellationToken token)
        {
            ExecutionPlan executionPlan = CreateExecutionPlan(info.MagicType, options);
            GetPatchContextInfos(ref info,
                                 out long coverDataOffset,
                                 out long rleCtrlDataOffset,
                                 out long rleCodeDataOffset,
                                 out long newDataOffset);
            PatchMetadata patchMetadata = info.GetPatchMetadata();
            DiffReadersContext context = CreateReaderContext(info.CompressionType,
                                                             options,
                                                             patchMetadata,
                                                             await createPatchStreamAsync(coverDataOffset,   token),
                                                             await createPatchStreamAsync(rleCtrlDataOffset, token),
                                                             await createPatchStreamAsync(rleCodeDataOffset, token),
                                                             await createPatchStreamAsync(newDataOffset,     token),
                                                             executionPlan.MaxDecompressionWorkers);

            return new HDiff13DerivedPatcher(context.Cover,
                                             context.RleCtrl,
                                             context.RleCode,
                                             context.NewData,
                                             info,
                                             options,
                                             progressCallback,
                                             executionPlan.PatchWorkerBudget - context.DecompressionWorkers,
                                             executionPlan.CopyWorkers,
                                             executionPlan.RunCopyConcurrently);
        }

        private static unsafe DiffReadersContext CreateReaderContext(HDiffCompression         compType,
                                                                     PatchOptions             options,
                                                                     PatchMetadata            patchMetadata,
                                                                     ValueTuple<Stream, bool> coverCtx,
                                                                     ValueTuple<Stream, bool> rleCtrlCtx,
                                                                     ValueTuple<Stream, bool> rleCodeCtx,
                                                                     ValueTuple<Stream, bool> newDataCtx,
                                                                     int                      maxDecompressionWorkers)
        {
            int bufferSize = options.ReaderBufferSize;

            ChunkSizeInfo coverSize  = *patchMetadata.CoverDataSizeP;
            ChunkSizeInfo controlSize = *patchMetadata.RleControlDataSizeP;
            ChunkSizeInfo codeSize    = *patchMetadata.RleCodeDataSizeP;
            ChunkSizeInfo newDataSize = *patchMetadata.NewDiffDataSizeP;

            Stream[] decompressedStreams =
            [
                CreateDecompressionStream(compType, coverSize,   coverCtx),
                CreateDecompressionStream(compType, controlSize, rleCtrlCtx),
                CreateDecompressionStream(compType, codeSize,    rleCodeCtx),
                CreateDecompressionStream(compType, newDataSize, newDataCtx)
            ];

            int decompressionWorkers = EnableParallelDecompression(
                compType,
                options,
                decompressedStreams,
                [coverSize, controlSize, codeSize, newDataSize],
                maxDecompressionWorkers);

            BittableStreamReader coverReader   = new(decompressedStreams[0], bufferSize, coverCtx.Item2);
            BittableStreamReader rleCtrlReader = new(decompressedStreams[1], bufferSize, rleCtrlCtx.Item2);
            BittableStreamReader rleCodeReader = new(decompressedStreams[2], bufferSize, rleCodeCtx.Item2);
            BittableStreamReader newDataReader = new(decompressedStreams[3], bufferSize, newDataCtx.Item2);

            return new DiffReadersContext(coverReader,
                                          rleCtrlReader,
                                          rleCodeReader,
                                          newDataReader,
                                          decompressionWorkers);
        }

        private static int EnableParallelDecompression(HDiffCompression compType,
                                                       PatchOptions     options,
                                                       Stream[]         streams,
                                                       ChunkSizeInfo[]  sizes,
                                                       int              maxWorkers)
        {
            if (maxWorkers == 0 ||
                compType is HDiffCompression.Uncompressed or HDiffCompression.Zstd)
            {
                return 0;
            }

            int prefetchCount = Math.Min(streams.Length, maxWorkers);

            List<(int Index, long CompressedSize)> candidates = new(streams.Length);
            for (int index = 0; index < sizes.Length; index++)
            {
                if (sizes[index].CompressedSize > 0 && sizes[index].Size > 0)
                {
                    candidates.Add((index, sizes[index].CompressedSize));
                }
            }

            candidates.Sort(static (left, right) => right.CompressedSize.CompareTo(left.CompressedSize));
            prefetchCount = Math.Min(prefetchCount, candidates.Count);

            int prefetchBufferSize = options.ReaderBufferSize > 0
                ? Math.Max(64 << 10, options.ReaderBufferSize)
                : 1 << 20;
            for (int candidateIndex = 0; candidateIndex < prefetchCount; candidateIndex++)
            {
                int streamIndex = candidates[candidateIndex].Index;
                streams[streamIndex] = new PrefetchedReadStream(streams[streamIndex],
                                                                prefetchBufferSize);
            }

            return prefetchCount;
        }

        private static ExecutionPlan CreateExecutionPlan(HDiffMagic   magicType,
                                                         PatchOptions options)
        {
            int totalWorkers;
            if (options.ParallelThreads == 0)
            {
                totalWorkers = Math.Max(1, Environment.ProcessorCount);
            }
            else if (options.ParallelThreads > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(PatchOptions.ParallelThreads));
            }
            else
            {
                totalWorkers = (int)options.ParallelThreads;
            }

            bool hasCopyStage = magicType != HDiffMagic.HDiff13;
            int copyWorkers = hasCopyStage
                ? totalWorkers == 1
                    ? 1
                    : Math.Max(1, totalWorkers / 4)
                : 0;
            int patchWorkerBudget = hasCopyStage && totalWorkers > 1
                ? totalWorkers - copyWorkers
                : totalWorkers;
            int maxDecompressionWorkers = Math.Min(4, patchWorkerBudget / 2);

            return new ExecutionPlan(patchWorkerBudget,
                                     copyWorkers,
                                     maxDecompressionWorkers,
                                     hasCopyStage && totalWorkers > 1);
        }

        private static Stream CreateDecompressionStream(
            HDiffCompression         compType,
            ChunkSizeInfo            size,
            ValueTuple<Stream, bool> streamContext)
            => DecompressStreamFactory.Create(
                compType,
                streamContext.Item1,
                streamContext.Item2,
                size.CompressedSize,
                size.Size);

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
            BittableStreamReader NewData,
            int DecompressionWorkers);

        private readonly record struct ExecutionPlan(int  PatchWorkerBudget,
                                                     int  CopyWorkers,
                                                     int  MaxDecompressionWorkers,
                                                     bool RunCopyConcurrently);
    }
}
