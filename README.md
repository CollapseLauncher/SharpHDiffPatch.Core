# SharpHDiffPatch

[![NuGet Downloads](https://img.shields.io/nuget/dt/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/) [![NuGet version](https://img.shields.io/nuget/v/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/)

**SharpHDiffPatch** is a patching library for HDiffPatch format written in C#, purposedly as a port of **HPatchZ** implementation (from [**HDiffPatch** by **housisong**](https://github.com/sisong/HDiffPatch)). This project doesn't support making diff file and only works for patching.

Supporting file and directory patching with these compression formats:
- BZip2
- Deflate
- Zstd
- LZMA2 (not LZMA)
- No Compression.

Unfortunately, the **``HDIFFSF20``** (Single Compressed) format is still unsupported. But we are planning to add it in the future.

This project is used as a part submodule of our main project: [**Collapse Launcher**](https://github.com/CollapseLauncher).

# Usage Example
## Patching with a simple progress indicator
```CSharp
using SharpHDiffPatch.Core;
using SharpHDiffPatch.Core.Event;

string oldPath = "C:\\test\\Music1.pck";
string diffPath = "C:\\test\\Music1.pck.hdiff";
string newPath = "C:\\test\\Music1.pck.new";

// Initialize the patcher instance
HDiffPatch patcher = new HDiffPatch();
// Set the verbosity of the logging
// Available Options: Quiet, Info (default), Verbose, Debug
HDiffPatch.LogVerbosity = Verbosity.Verbose;

// Subscribe an event listener to logging
EventListener.LoggerEvent += EventListener_LoggerEvent;
// Subscribe an event listener to patching progress
EventListener.PatchEvent += EventListener_PatchEvent;

// Initialize the diff file
patcher.Initialize(diffPath);
// Start the patching process
// This method has some arguments you can tweak as below:
//     Patch(string inputPath, string outputPath, bool useBufferedPatch,
//           CancellationToken token = default, bool useFullBuffer = false,
//           bool useFastBuffer = false)
// 
//     Description:
//      - inputPath             -> Path of the old/source file/folder.
//      - outputPath            -> Path of the new/target file/folder.
//      - useBufferedPatch      -> Use array-based buffer for RLE Control and Code clips.
//      - token                 -> Cancellation token.
//      - useFullBuffer         -> Buffer the RLE New Data to the MemoryStream.
//      - useFastBuffer         -> Buffer the RLE Control and Code clips to ArrayPool.
patcher.Patch(inputPath, outputPath, true, default, false, true);

// Unsubscribe an event listener to logging
EventListener.LoggerEvent -= EventListener_LoggerEvent;
// Unsubscribe an event listener to patching progress
EventListener.PatchEvent -= EventListener_PatchEvent;

// Implement logging listener
private void EventListener_LoggerEvent(object? sender, LoggerEvent e)
{
    string label = e.LogLevel switch
    {
        Verbosity.Info => $"[Info] ",
        Verbosity.Verbose => $"[Verbose] ",
        Verbosity.Debug => $"[Debug] ",
        _ => ""
    };
    Console.WriteLine($"{label}{e.Message}");
}

// Implement patching progress listener
private void EventListener_PatchEvent(object? sender, PatchEvent e)
{
    Console.Write($"Patching: {e.ProgressPercentage}% | {SummarizeSizeSimple(e.CurrentSizePatched)}/{SummarizeSizeSimple(e.TotalSizeToBePatched)} @{SummarizeSizeSimple(e.Speed)}/s    \r");
}
```

## Get the New file size from diff file.
```CSharp
using SharpHDiffPatch.Core;

string diffPath = "C:\\test\\Music1.pck.hdiff";
long newFileSize = HDiffPatch.GetHDiffNewSize(diffPath);

Console.WriteLine($"The new file size is: {newFileSize} bytes");
```