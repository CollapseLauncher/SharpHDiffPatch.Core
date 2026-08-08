using System;

namespace SharpHPatchZ.Header.Metadata;

public interface IMetadataInit : IDisposable
{
    public MetadataTypeConst MetadataType  { get; }
    public bool              IsInitialized { get; }
    public bool              IsDisposed    { get; }

    void Init();
}
