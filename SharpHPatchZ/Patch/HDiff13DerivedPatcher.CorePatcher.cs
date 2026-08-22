using System;
using System.Threading;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ.Patch;

internal sealed partial class HDiff13DerivedPatcher
{
    private void StartCorePatcher(CancellationToken token)
    {
        ReadOnlySpan<RleCoverInfo> rleCoverSpan = RleCoverInfo.Read(_coverReader,
                                                                    Info,
                                                                    out RleCoverInfo[] rleCoverBackedBuffer,
                                                                    token);
        try
        {

        }
        finally
        {
            BigArrayPool<RleCoverInfo>.Shared.Return(rleCoverBackedBuffer);
        }
    }
}
