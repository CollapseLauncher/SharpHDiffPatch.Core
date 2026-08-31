# SharpHDiffPatch

[![NuGet Downloads](https://img.shields.io/nuget/dt/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/) [![NuGet version](https://img.shields.io/nuget/v/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/)

**SharpHPatchZ** (formerly SharpHDiffPatch) is a patching library for HDiffPatch format written in C#, purposedly as a port of **HPatchZ** implementation (from [**HDiffPatch** by **housisong**](https://github.com/sisong/HDiffPatch)). This project doesn't support making a diff file and only works for patching.

Supporting file and directory patching with these compression formats:
- BZip2
- Deflate
- ZStandard
- LZMA
- LZMA2
- No Compression.

HDIFFSF20 format is planned to be supported later v3.0 releases.

This project is used as a part submodule of our main project: [**Collapse Launcher**](https://github.com/CollapseLauncher).

# Usage Examples
## A. Basic Patching Usage with Progress Output
```CSharp
using System;
using SharpHPatchZ;
using SharpHPatchZ.Header;

namespace Example;

public class Program
{
    private const string InputPath  = @"G:\HDiffTest\Hi3SEA";
    private const string PatchPath  = @"G:\HDiffTest\Hi3SEAtoCNExecOnly.lzma.diff";
    private const string OutputPath = @"G:\HDiffTest\Hi3CN-dotnet";

    public static void Main()
    {
        // Create a callback struct and pass the `UpdateProgress` method to the create method.
        ProgressCallback progressCallback = ProgressCallback.CreateFromManaged(UpdateProgress);

        // The `HDiffInfo` info context must be used within `using` scope or by disposing the struct manually.
        // Undisposed info context will cause a memory leak as it uses unmanaged memory under the hood.

        // Pass the patch file path into the `HPatch.CreateInstance()` method to create the info context.
        using HDiffInfo info = HPatch.CreateInstance(PatchPath);
        
        // Pass the `HDiffInfo` info context to the `HPatch.Patch()` method to start the patching process. Also pass the Input and Output Path, and the `progressCallback`
        PatchResult patchResult = HPatch.Patch(info, PatchPath, InputPath, OutputPath, progressCallback: progressCallback);

        // If the patch process is not successful, throw the exception.
        if (!patchResult)
            throw patchResult.Exception ?? new Exception();
    }

    private static void UpdateProgress(long totalProcessed, long totalSize, int written)
    {
        double percent = Math.Round(totalProcessed / (double)totalSize * 100, 2);
        Console.Write(
            $"Patching: {percent}% | " +
            $"{SummarizeSizeSimple(totalProcessed)}/{SummarizeSizeSimple(totalSize)}    \r");
    }

    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];

    private static string SummarizeSizeSimple(double value, int decimalPlaces = 2)
    {
        byte mag = (byte)Math.Log(value, 1000);

        return $"{Math.Round(value / (1L << (mag * 10)), decimalPlaces)} {SizeSuffixes[mag]}";
    }
}
```

## B. Basic Patching Usage with Adjusted Options
```CSharp
// There are few `PatchOptions` templates you can use.
//   - PatchOptions.BigBuffer
//     Generally the fastest, but uses numerous amount of buffer to process the patch.
//         ParallelThreads       = (Auto) determines the max. amount of patch worker used per thread.
//         ReaderBufferSize      = 1 MiB/reader
//         CopyBufferSize        = 128 KiB/copy file job (Only being used if the patch is a directory patch (HDiff19 type))
//         PatchWorkerBufferSize = 16 MiB/patch worker
//         UseSIMD               = true (only available for .NET 6 or above)
//   
//   - PatchOptions.Default
//     Moderately faster, sometimes better for small patch. Uses modest amount of buffer to process the patch.
//         ParallelThreads       = (Auto) determines the max. amount of patch worker used per thread.
//         ReaderBufferSize      = 64 KiB/reader
//         CopyBufferSize        = 16 KiB/copy file job (Only being used if the patch is a directory patch (HDiff19 type))
//         PatchWorkerBufferSize = 1 MiB/patch worker
//         UseSIMD               = true (only available for .NET 6 or above)
//   
//   - PatchOptions.SmallBuffer
//     Slower but uses the smallest buffer size possible.
//         ParallelThreads       = (Auto) determines the max. amount of patch worker used per thread.
//         ReaderBufferSize      = 4 KiB/reader
//         CopyBufferSize        = 4 KiB/copy file job (Only being used if the patch is a directory patch (HDiff19 type))
//         PatchWorkerBufferSize = 128 KiB/patch worker
//         UseSIMD               = true (only available for .NET 6 or above)
//   
//   - PatchOptions.OptimizeForHDD
//     Equally the same as Default but spawn only one patch worker for better sequential write performance on HDD and to avoid
//     heavy random seek.
//         ParallelThreads       = 1 (Spawn only one patch worker at a time for sequential write to the output.)
//         ReaderBufferSize      = 64 KiB/reader
//         CopyBufferSize        = 16 KiB/copy file job (Only being used if the patch is a directory patch (HDiff19 type))
//         PatchWorkerBufferSize = 1 MiB/patch worker
//         UseSIMD               = true (only available for .NET 6 or above)
PatchOptions patchOptions = PatchOptions.BigBuffer;

// Create the info context from the patch file path.
using HDiffInfo info = HPatch.CreateInstance(PatchPath);

// Pass the info context, patch patch, input path and output path, as well as the `patchOptions`.
PatchResult patchResult = HPatch.Patch(info, PatchPath, InputPath, OutputPath, options: patchOptions);
```

## C. Uses asychronous version of the method.
```CSharp
private static async Task ProcessPatch(
    string patchPath,
    string inputPath,
    string outputPath,
    PatchOptions options,
    CancellationToken token)
{
    // Creates the info context asynchronously from the patch path.
    using HDiffInfo info = await HPatch.CreateInstanceAsync(patchPath, token);

    // Starts the patch process asynchronously, pass the arguments as usual.
    PatchResult patchResult = await HPatch.PatchAsync(info, patchPath, inputPath, outputPath, options: options, token: token);
}
```

## D. Uses a `Stream` Factory method to for the patch file.
### Synchronous version:
```CSharp
private static void ProcessPatch(string patchPath, string inputPath, string outputPath, PatchOptions options)
{
    // Create the info context from a `Stream` factory method by `CreateStream()`.
    using HDiffInfo info = HPatch.CreateInstance(CreateStream);

    // Start the patch process by passing the `CreateStream()` factory method to the `HPatch.Patch()`.
    PatchResult patchResult = HPatch.Patch(info, CreateStream, inputPath, outputPath, options: options);

    if (!patchResult)
        throw patchResult.Exception ?? new Exception();

    return;

    // This method is used to produce the `Stream` of the patch file starting from the specified position/offset of the file.
    // The `position` argument here is necessary as it's used to specify where the data will start to be read by the patching methods.
    (Stream stream, bool leaveOpen) CreateStream(long position)
    {
        // Open the patch `Stream` with shared Read operation. The `Stream` must be created with `FileShare.Read` as this method
        // will be called to produce multiple `Stream` instances with shared Read operations.
        // 
        // This is important to note that we don't use `using` keyword here as we don't want to dispose the `Stream` instance
        // after leaving this method. We want the callee to dispose the `Stream` instance instead later by setting `leaveOpen`
        // to `false` under the returned value below.
        FileStream stream = File.Open(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // The `Stream` instance must be seek-ed to the certain offset provided by the `position` argument.
        stream.Position = position;

        // Return the `Stream` instance and set the `leaveOpen` to `false` to tell the callee to
        // dispose this created `Stream` instance after use.
        return (stream, false);
    }
}
```

### Asynchronous version:
The `async` version is basically the same as the sychronous version but with a few adjustments onto the `Stream` factory method where it receives `CancellationToken` from the callee method within the patch processing method.
```CSharp
private static async Task ProcessPatch(string patchPath, string inputPath, string outputPath, PatchOptions options, CancellationToken token)
{
    // Create the info context from a `Stream` factory method by `CreateStreamAsync()`.
    using HDiffInfo info = await HPatch.CreateInstanceAsync(CreateStreamAsync, token);

    // Start the patch process by passing the `CreateStreamAsync()` factory method to the `HPatch.PatchAsync()`.
    PatchResult patchResult = await HPatch.PatchAsync(info, CreateStreamAsync, inputPath, outputPath, options: options, token: token);

    if (!patchResult)
        throw patchResult.Exception ?? new Exception();

    return;

    // This method is used to produce the `Stream` of the patch file starting from the specified position/offset of the file.
    // The `position` argument here is necessary as it's used to specify where the data will start to be read by the patching methods.
    ValueTask<(Stream stream, bool leaveOpen)> CreateStreamAsync(long position, CancellationToken calleeToken)
    {
        // Open the patch `Stream` with shared Read operation. The `Stream` must be created with `FileShare.Read` as this method
        // will be called to produce multiple `Stream` instances with shared Read operations.
        // 
        // This is important to note that we don't use `using` keyword here as we don't want to dispose the `Stream` instance
        // after leaving this method. We want the callee to dispose the `Stream` instance instead later by setting `leaveOpen`
        // to `false` under the returned value below.
        FileStream stream = File.Open(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // The `Stream` instance must be seek-ed to the certain offset provided by the `position` argument.
        stream.Position = position;

        // Return the `Stream` instance and set the `leaveOpen` to `false` to tell the callee to
        // dispose this created `Stream` instance after use.
        // 
        // Another thing to note on this asynchronous version that since we don't await any method here, we can
        // just pass the result of the `ValueTask` instead. For the fully asynchronous example, you can go to
        // the next example below.
        return new ValueTask<(Stream stream, bool leaveOpen)>((stream, false));
    }
}
```

## E. Uses a `Stream` Factory to open Patch file from a remote URL (HTTP).
**Yes, you heard it right.** Thanks to the power of "re-writing the code instead of fixing the existing ones", we re-think to make the read of the patch from a remote `Stream` possible. In this example, we utilizes a fully asynchronous method version of the methods. This demo is basically identical as previous one but we adjust the inner `CreateStreamAsync` method to produce the `Stream` instance from an `HttpClient` as the methods under the `HttpClient` are mostly asynchronous.

```CSharp
private static async Task ProcessPatchFromRemote(HttpClient        client,
                                                 string            patchUrl,
                                                 string            inputPath,
                                                 string            outputPath,
                                                 PatchOptions      options,
                                                 CancellationToken token)
{
    // Create the info context from a `Stream` factory method by `CreateStreamAsync()`.
    using HDiffInfo info = await HPatch.CreateInstanceAsync(CreateStreamAsync, token);

    // Start the patch process by passing the `CreateStreamAsync()` factory method to the `HPatch.PatchAsync()`.
    PatchResult patchResult = await HPatch.PatchAsync(info, CreateStreamAsync, inputPath, outputPath, options: options, token: token);

    if (!patchResult)
        throw patchResult.Exception ?? new Exception();

    return;

    // This factory method produces a remote `Stream` instance from a remote URL with specified position/offset.
    // This method is now fully asynchronous than the previous demos.
    async ValueTask<(Stream stream, bool leaveOpen)> CreateStreamAsync(long position, CancellationToken calleeToken)
    {
        // Creates a `GET` request message to the HTTP server with specified URL.
        // Also, the request message must provide a `content-range` header by specifying `RangeHeaderValue`
        // to the `HttpRequestMessage.Headers.Range` property. The `null` after the `position` here means
        // to: "Get a slice of the remote `Stream` starting from this `position` to the rest of the file."
        HttpRequestMessage request = new(HttpMethod.Get, patchUrl);
        request.Headers.Range = new RangeHeaderValue(position, null);

        // Send the `GET` request message to the HTTP server, then get the response of the request.
        // The `HttpCompletionOption.ResponseHeadersRead` here is important as we don't want to pre-cache
        // the entire response data to the memory temporarily (which took much longer). Instead, we just
        // want it on-demand and read the rest of the data we only wanted.
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, calleeToken);

        // This method is actually optional to call, but it's better for us to call it upfront to
        // ensure that the response produces `2xx` response (OK/Continue) and return the actual patch data
        // instead of an error message.
        // 
        // If non-`2xx` response is returned, then throw for us.
        response.EnsureSuccessStatusCode();

        // Open the remote `Stream` instance and pass the `leaveOpen` to `false` to tell the callee to
        // dispose the `Stream` instance after use (and so, disposing the `HttpRequestMessage` and
        // `HttpResponseMessage` for us automatically).
        return (await response.Content.ReadAsStreamAsync(calleeToken), false);
    }
}
```

## F. Initialize Kuro Games HDiff19 Patch Format
Kuro Games recently produces a slightly modified version of HDiff19 to be used on the patching process of their game, Wuthering Waves. This format slighty changes how the Directory Patch Information is structurized.

Instead of using this regular format:
```
... previous struct
- Input Path Entry Array (string)
- Output Path Entry Array (string)
- Input Files Index Array (int)
- Output Files Index Array (int)
- Output Files Sizes Array (long)
- Same File Path Index Pair Array (int, int)
... next struct
```
They added two additional structs:
```
... previous struct
- Input Path Entry Array (string)
- Output Path Entry Array (string)
- Input Files Index Array (int)
- Output Files Index Array (int)
- Input Files Sizes Array (long) < Exists on Kuro Games format, used for input file size sanity >
- Output Files Sizes Array (long)
- Output Files Hashes Array (long) < Exists on Kuro Games format, used for final output file integrity >
- Same File Path Index Pair Array (int, int)
... next struct
```

Another to mention that the Kuro Games format still uses the same signature magic, which is `HDiff19`, we can't make it distinctly different from normal ones. So in order to initialize the patch file, you have to manually create an additional `InitializeOptions` and set the `IsKuroGamesHDiff` field to `true`, then pass the struct into the `HPatch.CreateInstance()` or `HPatch.CreateInstanceAsync` method (depends on your use case).

Here's the example of the usage:
```CSharp
private static void ProcessPatch(string       patchPath,
                                 string       inputPath,
                                 string       outputPath,
                                 PatchOptions options)
{
    // Create the initialization option and set `IsKuroGamesHDiff` to `true`.
    InitializeOptions initializeOptions = new()
    {
        IsKuroGamesHDiff = true
    };

    // Pass the initialization option to the `CreateInstance()` method.
    using HDiffInfo info = HPatch.CreateInstance(patchPath, initializeOptions);

    // Start the patching process as usual.
    PatchResult patchResult = HPatch.Patch(info, patchPath, inputPath, outputPath, options: options);

    if (!patchResult)
        throw patchResult.Exception ?? new Exception();
}
```
_Thanks to @Cryotechnic for the initial implementaion on the V2 codebase._

### For Kuro Games developer:

_I think it's better for you to make a different signature magic (for example: `HDiff19Kuro` or something) instead of just adding an arbitrary structs inside of the file so it's easier for us to automatically parse your format. Also, don't forget to ask the permission to the original developer of the format (housisong) that you slightly changed their format for your own purposes (**And... don't forget to give them a credit on your launcher license file or something**)_😉

~ @neon-nyan

### G. Use Miscellaneous Utilities
We have few utility methods which enables you to play with the `HDiffInfo` info context struct and gets some information about the patch file.

#### Example 1: Get the PatchMetadata using `HPatch.TryGetPatchMetadata()`
```CSharp
// Creates an `HDiffInfo` info context from a patch file path.
HDiffInfo info = HPatch.CreateInstance(PatchPath);

try
{
    // Try to get the `PatchMetadata` out of `HDiffInfo` info context.
    if (!HPatch.TryGetPatchMetadata(ref info, out PatchMetadata patchMetadata))
    {
        throw new InvalidOperationException("Cannot get PatchMetadata");
    }

    // Prints some info from the `PatchMetadata` struct.
    Console.WriteLine($"""
                       Old Data Size: {patchMetadata.DiffOldSize}
                       New Data Size: {patchMetadata.DiffNewSize}
                       RLE Cover Info Count: {patchMetadata.CoverDataCount}
                       """);
}
finally
{
    // Dispose the info context struct to release the unmanaged resources
    info.Dispose();
}
```

#### Example 2: Get the DirectoryPatchMetadata and prints the Input and Output Path List
```CSharp
// Creates an `HDiffInfo` info context from a patch file path.
HDiffInfo info = HPatch.CreateInstance(PatchPath);

try
{
    // Try to get the `PatchMetadata` out of `HDiffInfo` info context.
    if (!HPatch.TryGetDirectoryPatchMetadata(ref info, out DirectoryPatchMetadata directoryPatchMetadata))
    {
        Console.WriteLine("File is not an HDiff19 Directory patch format.");
        return;
    }

    // Print data from DirectoryPatchMetadata.
    // NOTE: As some of these methods are unsafe, you must enable <AllowUnsafeBlocks> to true in your project file.
    unsafe
    {
        // Creates a `Span` from an unmanaged `InputPathListP` field
        Span<Utf16UnmanagedString> inputPaths = HPatch.TryGetUnmanagedArraySpan(directoryPatchMetadata.InputPathListP);
        Console.WriteLine("Input paths:");
        for (int i = 0; i < inputPaths.Length; i++)
        {
            // Prints the `Utf16UnmanagedString` directly as it implicitly converts itself to a managed `string` type.
            Console.WriteLine($"  - {inputPaths[i]}");
        }

        // Creates a `Span` from an unmanaged `OutputPathListP` field
        Span<Utf16UnmanagedString> outputPaths = HPatch.TryGetUnmanagedArraySpan(directoryPatchMetadata.OutputPathListP);
        Console.WriteLine("Output paths:");
        for (int i = 0; i < outputPaths.Length; i++)
        {
            // Prints the `Utf16UnmanagedString` directly as it implicitly converts itself to a managed `string` type.
            Console.WriteLine($"  - {outputPaths[i]}");
        }
    }
}
finally
{
    // Dispose the info context struct to release the unmanaged resources
    info.Dispose();
}
```

#### Example 3: Get the total Input/Output size from the patch path.
```CSharp
// Try to get the total size of the input or output files from a patch file path.
if (!HPatch.TryGetDiffSizeInfo(PatchPath, out long totalInputSize, out long totalOutputSize))
{
    // If failed, throw.
    throw new InvalidOperationException("Patch file might be invalid or unsupported");
}

// Print the total size info.
Console.WriteLine($"""
                   Total Old/Input File Size: {totalInputSize}
                   Total New/Output File Size: {totalOutputSize}
                   """);
```