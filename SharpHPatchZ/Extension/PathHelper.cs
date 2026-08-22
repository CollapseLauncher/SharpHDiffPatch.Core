using System;
using System.IO;

namespace SharpHPatchZ.Extension;

internal static class PathHelper
{
    extension(string path)
    {
        public DirectoryInfo GetDirectoryInfo()
            => new FileInfo(path).Exists
                ? throw ExceptionHelper.ThrowHDiffPatchPathNotADirectory(path)
                : new DirectoryInfo(path);

        public FileInfo GetFileInfo()
            => new DirectoryInfo(path).Exists
                ? throw ExceptionHelper.ThrowHDiffPatchPathNotAFile(path)
                : new FileInfo(path);

        public bool IsDirectory()
        {
            ReadOnlySpan<char> pathSpan = path;
            if (pathSpan.IsEmpty) return false;

            char lastChar = pathSpan[^1];
            return lastChar is '\\' or '/';
        }
    }
}
