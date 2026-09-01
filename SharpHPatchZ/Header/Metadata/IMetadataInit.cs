using System;

namespace SharpHPatchZ.Header.Metadata;

/// <summary>Defines lifecycle information and initialization for unmanaged patch metadata.</summary>
public interface IMetadataInit : IDisposable
{
    /// <summary>Gets the <see cref="MetadataTypeConst"/> discriminator for this metadata type.</summary>
    public MetadataTypeConst MetadataType  { get; }
    /// <summary>Gets whether the metadata has been initialized.</summary>
    public bool              IsInitialized { get; }
    /// <summary>Gets whether the metadata has been disposed.</summary>
    public bool              IsDisposed    { get; }

    /// <summary>Initializes the metadata and any resources it owns.</summary>
    void Init();
}
