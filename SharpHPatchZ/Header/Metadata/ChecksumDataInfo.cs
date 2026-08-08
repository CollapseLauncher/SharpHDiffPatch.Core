using System;
using System.Runtime.CompilerServices;
using SharpHDiffPatch.New.Extension;

namespace SharpHDiffPatch.New.Header.Metadata;

public unsafe struct ChecksumDataInfo : IMetadataInit
{
    public ChecksumDataInfo()
    {
        Init();
    }

    public void Init()
    {
        if (IsDisposed || IsInitialized)
        {
            return;
        }

        IsInitialized = true;
        IsDisposed    = false;
        MetadataType  = MetadataTypeConst.ChecksumDataInfoType;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed    = true;
        IsInitialized = false;

        if (_byte != null) MemoryAlloc.Free(_byte);
        _byte = null;
    }

    public MetadataTypeConst MetadataType { get; private set; }

    public bool IsInitialized
    {
        get => _isInitialized == 1;
        private set => _isInitialized = value ? (byte)1 : (byte)0;
    }

    public bool IsDisposed
    {
        get => _isDisposed == 1;
        private set => _isDisposed = value ? (byte)1 : (byte)0;
    }

    private byte  _isInitialized;
    private byte  _isDisposed;

    private int   _dataSize;
    private int   _dataCount;
    private void* _byte;

    public void AllocBytes(int dataSize, int elementCount)
        => _byte = MemoryAlloc.Alloc((_dataSize = dataSize) * (_dataCount = elementCount), true);

    public Span<byte> GetAllSpan()
        => new(_byte, _dataCount * _dataSize);

    public Span<byte> GetSpan(int index)
        => new(Unsafe.Add<byte>(_byte, index * _dataSize), _dataSize);
}
