# SharpHDiffPatch

[![NuGet Downloads](https://img.shields.io/nuget/dt/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/) [![NuGet version](https://img.shields.io/nuget/v/SharpHDiffPatch.Core.svg?style=flat-square)](https://www.nuget.org/packages/SharpHDiffPatch.Core/)

**SharpHPatchZ** (formerly SharpHDiffPatch) is a patching library for HDiffPatch format written in C#, purposedly as a port of **HPatchZ** implementation (from [**HDiffPatch** by **housisong**](https://github.com/sisong/HDiffPatch)). This project doesn't support making diff file and only works for patching.

Supporting file and directory patching with these compression formats:
- BZip2
- Deflate
- Zstd
- LZMA and LZMA2
- No Compression.

HDIFFSF20 format is planned to be supported in v3.0 branch on the General Public release.

This project is used as a part submodule of our main project: [**Collapse Launcher**](https://github.com/CollapseLauncher).

# TODO: Usage Example

## TODO: Patching Kuro directory diffs

## TODO: Get the New file size from diff file.