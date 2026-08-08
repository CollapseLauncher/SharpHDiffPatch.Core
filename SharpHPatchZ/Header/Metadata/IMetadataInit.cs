using System;

namespace SharpHDiffPatch.New.Header.Metadata;

public interface IMetadataInit : IDisposable
{
    public MetadataTypeConst MetadataType  { get; }
    public bool              IsInitialized { get; }
    public bool              IsDisposed    { get; }

    void Init();
}
