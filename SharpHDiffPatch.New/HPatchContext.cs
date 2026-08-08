using System;
using SharpHDiffPatch.New.Header;

namespace SharpHDiffPatch.New;

public class HPatchContext : IDisposable
{
    public required HDiffInfo Info { get; init; }

    public void Dispose()
    {
    }
}
