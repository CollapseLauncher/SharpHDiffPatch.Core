using System;
using System.IO;

namespace SharpHDiffPatch.Core.Patch
{
    public interface IPatch
    {
        void Patch(string input, string output, bool useBufferedPatch = true, bool useFullBuffer = false, bool useFastBuffer = false);
    }
}
