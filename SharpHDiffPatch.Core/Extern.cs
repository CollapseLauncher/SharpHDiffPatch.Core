#if NET6_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpHDiffPatch.Core;

internal class Extern
{
    private static readonly string CurrentProcPath = Environment.ProcessPath?.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string LibArchitecturePrefix = GetLibArchitecturePrefix();
    private static readonly string LibPlatformNamePrefix = GetLibPlatformNamePrefix();
    private static readonly string LibExtensionPrefix = GetLibExtensionPrefix();
    private static readonly string LibFolderPath = Path.Combine("Lib", LibPlatformNamePrefix);
    private static readonly string LibFullPath = Path.Combine(CurrentProcPath, LibFolderPath, "{0}" + LibExtensionPrefix);

    static Extern()
    {
        // Use custom Dll import resolver
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);
    }

    private static string GetLibPlatformNamePrefix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"win-{LibArchitecturePrefix}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return $"linux-{LibArchitecturePrefix}";
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "unknown";
    }

    private static string GetLibExtensionPrefix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ".dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ".so";
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : string.Empty;
    }

    private static string GetLibArchitecturePrefix() => RuntimeInformation.OSArchitecture.ToString().ToLower();

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        libraryName = string.Format(LibFullPath, libraryName);
        string searchPathName = searchPath == null ? "Default" : searchPath.ToString();
        HDiffPatch.Event.PushLog($"[Extern::DllImportResolver] Loading library from path: {libraryName} | Search path: {searchPathName}", Verbosity.Debug);
        // Try load the library and if fails, then throw.
        bool isLoadSuccessful = NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr pResult);
        if (!isLoadSuccessful || pResult == IntPtr.Zero)
            throw new FileLoadException($"Failed while loading library from this path: {libraryName}\r\nMake sure that the library/.dll is exist or valid and not corrupted!");

        // If success, then return the pointer to the library
        return pResult;
    }
}
#endif