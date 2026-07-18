using System;

namespace SharpHDiffPatch.Core.Patch;

public interface IPatch
{
    void Patch(string input, string output, Action<long> writeBytesDelegate, bool useBufferedPatch, bool useFullBuffer, bool useFastBuffer);
}
