using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hi3Helper.SharpHDiffPatch
{
    public struct TDirPatcher
    {

    }

    public struct TDecompress
    {
        
    }

    public partial class HDiffPatch
    {

        private void RunDirectoryPatch(string inputPath, string outputPath)
        {
            if (!Directory.Exists(inputPath)) throw new ArgumentException($"Input path must be exist");
            if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

            MemoryStream patchStream = new MemoryStream();
            try
            {
                switch (headerInfo.compMode)
                {
                    case CompressionMode.lzma:
                        // DecompressLZMA2Diff(patchStream);
                        break;
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                patchStream?.Dispose();
            }
        }
    }
}
