using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Reader;
// ReSharper disable CommentTypo

namespace SharpHPatchZ.Patch;

internal sealed partial class HDiff13DerivedPatcher : PatcherBase
{
    private readonly BittableStreamReader _coverReader;
    private readonly BittableStreamReader _rleCtrlReader;
    private readonly BittableStreamReader _rleCodeReader;
    private readonly BittableStreamReader _newDataReader;

    internal HDiff13DerivedPatcher(
        BittableStreamReader coverReader,
        BittableStreamReader rleCtrlReader,
        BittableStreamReader rleCodeReader,
        BittableStreamReader newDataReader,
        HDiffInfo            info,
        PatchOptions         options,
        ProgressCallback     progressCallback)
        : base(info, options, progressCallback)
    {
        _coverReader   = coverReader;
        _rleCtrlReader = rleCtrlReader;
        _rleCodeReader = rleCodeReader;
        _newDataReader = newDataReader;
    }

    private CopySimilarFilesContext? _copySimilarFilesContext;

    public override void StartPatch(string inputPath, string outputPath, CancellationToken token)
    {
        InitializeInputOutputStream(inputPath, outputPath);

        // Start both CopyOver and CorePatch routine if context is not null.
        if (_copySimilarFilesContext != null)
        {
            TaskCompletionSource<bool> copyOverTcs = new(null!, TaskCreationOptions.RunContinuationsAsynchronously); 
            TaskCompletionSource<bool> corePatcherTcs = new(null!, TaskCreationOptions.RunContinuationsAsynchronously);

            Thread copyOverThread = new(_ =>
            {
                try
                {
                    _copySimilarFilesContext.RunCopy(this, Options, token);
                    copyOverTcs.SetResult(true);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
#if NET6_0_OR_GREATER
                    copyOverTcs.SetCanceled(token);
#else
                    copyOverTcs.SetCanceled();
#endif
                }
                catch (Exception ex)
                {
                    copyOverTcs.SetException(ex);
                }
            });

            Thread corePatcherThread = new(_ =>
            {
                try
                {
                    StartCorePatcher(token);
                    corePatcherTcs.SetResult(true);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
#if NET6_0_OR_GREATER
                    copyOverTcs.SetCanceled(token);
#else
                    copyOverTcs.SetCanceled();
#endif
                }
                catch (Exception ex)
                {
                    corePatcherTcs.SetException(ex);
                }
            });

            copyOverThread.Start();
            corePatcherThread.Start();
            Task.WhenAll(copyOverTcs.Task, corePatcherTcs.Task).Wait(token);
            return;
        }

        // Otherwise, run the CorePatch routine only
        StartCorePatcher(token);
    }

    public override Task StartPatchAsync(string inputPath, string outputPath, CancellationToken token)
        => Task.Factory.StartNew(state => StartPatch(inputPath, outputPath, (CancellationToken)state!),
                                 token,
                                 TaskCreationOptions.LongRunning);

    private unsafe void InitializeInputOutputStream(string inputPath, string outputPath)
    {
        ref PatchMetadata patchMetadata = ref Info.GetPatchMetadata();
        if (Unsafe.IsNullRef(ref patchMetadata)) throw ExceptionHelper.ThrowHDiffInfoPatchMetadataNotAllocated();

        // Return only single stream for HDiff13
        if (Info.MagicType == HDiffMagic.HDiff13)
        {
            FileInfo inputFile  = inputPath.GetFileInfo();
            FileInfo outputFile = outputPath.GetFileInfo();

            // Sanity input existence and its size
            if (!inputFile.Exists) throw ExceptionHelper.ThrowHDiffPatchInputPathNotExist(inputFile.FullName);
            if (inputFile.Length != patchMetadata.DiffOldSize)
                throw ExceptionHelper.ThrowHDiffPatchInputSizeMismatched(
                    inputFile.FullName, inputFile.Length, patchMetadata.DiffOldSize);

            InputStream  = new RandomMergedStreamWrapper([inputFile.FullName],  [inputFile.Length]);
            OutputStream = new RandomMergedStreamWrapper([outputFile.FullName],
                                                         [patchMetadata.DiffNewSize],
                                                         createFiles: true);

            return;
        }

        // Return multiple stream for HDiff19
        DirectoryInfo inputDir  = inputPath.GetDirectoryInfo();
        DirectoryInfo outputDir = outputPath.GetDirectoryInfo();
        outputDir.Create();

        ref DirectoryPatchMetadata dirMetadata = ref Info.MetadataAs<DirectoryPatchMetadata>();
        if (Unsafe.IsNullRef(ref dirMetadata)) throw ExceptionHelper.ThrowHDiffInfoDirectoryPatchMetadataNotAllocated();

        // Directory progress includes both the core/reference output and files
        // copied unchanged from the input tree.
        TotalSize = checked(dirMetadata.OutputPathCountSizeInfoP->Size +
                            dirMetadata.SameFilePathCountSizeInfoP->Size);

        // All input and output paths
        Utf16UnmanagedString[] allInputPaths = CopyToManagedStringList(dirMetadata.InputPathListP);
        Utf16UnmanagedString[] allOutputPaths = CopyToManagedStringList(dirMetadata.OutputPathListP);

        // Similar input and output paths
        (string[] similarInputFiles, string[] similarOutputFiles) =
            CopySimilarFilePairsFromIndexes(allInputPaths, allOutputPaths, ref dirMetadata);

        // Reference / Merged Input and Output paths
        long     refInputTotalSize = dirMetadata.InputPathCountSizeInfoP->Size;
        int      refInputCount     = dirMetadata.InputFileIndexListP->Length;
        string[] refInputFiles     = new string[refInputCount];
        long[]   refInputFilesSize = new long[refInputCount];

        int      refOutputCount     = dirMetadata.OutputFileIndexListP->Length;
        string[] refOutputFiles     = new string[refOutputCount];
        long[]   refOutputFilesSize = new long[refOutputCount];

        // -- Reference Input
        Span<int> refInputFileIndexSpan = dirMetadata.InputFileIndexListP->GetSpan();
        long      lastInputFileEnds    = 0;
        for (int i = 0; i < refInputCount; i++)
        {
            int fileIndex = refInputFileIndexSpan[i];
            refInputFiles[i] = Path.GetFullPath(Path.Combine(inputDir.FullName, allInputPaths[fileIndex]));

            string   filePath = refInputFiles[i];
            FileInfo fileInfo = new(filePath);

            if (!fileInfo.Exists)
                throw ExceptionHelper.ThrowHDiffPatchInputPathNotExist(filePath);

            //   -- Additional size check reference for Kuro Games HDiff format.
            if (Info.InitializeOptions.IsKuroGamesHDiff &&
                dirMetadata.InputFileSizeListP != null)
            {
                ref long expectedOldRefSize = ref dirMetadata.InputFileSizeListP->GetSpan()[i];
                if (fileInfo.Length != expectedOldRefSize)
                    throw ExceptionHelper.ThrowHDiffPatchKuroInputFileSizeMismatched(filePath, fileInfo.Length, expectedOldRefSize);
            }

            lastInputFileEnds    += fileInfo.Length;
            refInputFilesSize[i] =  lastInputFileEnds;
        }

        // -- Reference Input Size sanity
        if (lastInputFileEnds != refInputTotalSize)
        {
            throw ExceptionHelper.ThrowHDiffPatchInputFilesMismatched(lastInputFileEnds, refInputTotalSize);
        }

        // -- Reference Output
        Span<int>  refOutputFileIndexSpan = dirMetadata.OutputFileIndexListP->GetSpan();
        Span<long> refOutputFileSizeSpan  = dirMetadata.OutputFileSizeListP->GetSpan();

        long outputSize = 0;
        for (int i = 0; i < refOutputCount; i++)
        {
            int  fileIndex = refOutputFileIndexSpan[i];
            long fileSize  = refOutputFileSizeSpan[i];

            refOutputFiles[i]     = Path.GetFullPath(Path.Combine(outputDir.FullName, allOutputPaths[fileIndex]));
            outputSize            += fileSize;
            refOutputFilesSize[i] = outputSize;
        }

        if (outputSize != patchMetadata.DiffNewSize)
        {
            throw new InvalidDataException(
                $"The reference output size ({outputSize}) does not match the patch output size ({patchMetadata.DiffNewSize}).");
        }

        InputStream  = new RandomMergedStreamWrapper(refInputFiles,  refInputFilesSize);
        OutputStream = new RandomMergedStreamWrapper(refOutputFiles,
                                                     refOutputFilesSize,
                                                     createFiles: true);
        _copySimilarFilesContext = new CopySimilarFilesContext(inputDir.FullName, similarInputFiles,
                                                               outputDir.FullName, similarOutputFiles);
    }

    private static unsafe Utf16UnmanagedString[] CopyToManagedStringList(UnmanagedArray<Utf16UnmanagedString>* unmanagedArray)
    {
        var                        strings = new Utf16UnmanagedString[unmanagedArray->Length];
        Span<Utf16UnmanagedString> span    = unmanagedArray->GetSpan();

        span.CopyTo(strings);
        return strings;
    }

    private static unsafe (string[], string[]) CopySimilarFilePairsFromIndexes(
        Span<Utf16UnmanagedString> inputPaths, Span<Utf16UnmanagedString> outputPaths, ref DirectoryPatchMetadata dirMetadata)
    {
        int count = dirMetadata.SameFilePathCountSizeInfoP->Count;

        string[] resultInputs  = new string[count];
        string[] resultOutputs = new string[count];

        for (int i = 0; i < count; i++)
        {
            ref FileIndexPair pair = ref dirMetadata.SameFilePathIndexPairP[i];
            resultInputs[i]  = inputPaths[pair.OldIndex];
            resultOutputs[i] = outputPaths[pair.NewIndex];
        }

        return (resultInputs, resultOutputs);
    }

    protected override void DisposeCore()
    {
        base.DisposeCore();
        _coverReader.Dispose();
        _rleCtrlReader.Dispose();
        _rleCodeReader.Dispose();
        _newDataReader.Dispose();
    }

#if NET6_0_OR_GREATER
    protected override ValueTask DisposeCoreAsync()
        => new(Task.WhenAll(base.DisposeCoreAsync().AsTask(),
                            _coverReader.DisposeAsync().AsTask(),
                            _rleCtrlReader.DisposeAsync().AsTask(),
                            _rleCodeReader.DisposeAsync().AsTask(),
                            _newDataReader.DisposeAsync().AsTask()));
#endif
}
