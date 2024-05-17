#if NET6_0_OR_GREATER
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpHDiffPatch.Core
{
    internal class Extern
    {
        private static readonly string _currentProcPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName).TrimEnd(Path.DirectorySeparatorChar);
        private static readonly string _libArchitecturePrefix = GetLibArchitecturePrefix();
        private static readonly string _libPlatformNamePrefix = GetLibPlatformNamePrefix();
        private static readonly string _libExtensionPrefix = GetLibExtensionPrefix();
        private static readonly string _libFolderPath = Path.Combine("Lib", _libPlatformNamePrefix);
        private static readonly string _libFullPath = Path.Combine(_currentProcPath, _libFolderPath, "{0}" + _libExtensionPrefix);

        static Extern()
        {
            // Use custom Dll import resolver
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);
        }

        internal static bool IsLibraryExist(string libraryName) => File.Exists(string.Format(_libFullPath, libraryName));

        private static string GetLibPlatformNamePrefix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return $"win-{_libArchitecturePrefix}";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return $"linux-{_libArchitecturePrefix}";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "osx";
            else return "unknown";
        }

        private static string GetLibExtensionPrefix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ".dll";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ".so";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ".dylib";
            else return string.Empty;
        }

        private static string GetLibArchitecturePrefix() => RuntimeInformation.OSArchitecture.ToString().ToLower();

        private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            libraryName = string.Format(_libFullPath, libraryName);
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
}
#endif