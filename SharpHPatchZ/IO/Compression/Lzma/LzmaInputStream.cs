using System;
using System.Buffers.Binary;
using System.IO;
using SharpHPatchZ.IO.Compression.Lzma.LZ;
using SharpHPatchZ.IO.Compression.Lzma.RangeCoder;

namespace SharpHPatchZ.IO.Compression.Lzma;

/// <summary>Provides a forward-only <see cref="Stream"/> that decompresses LZMA or LZMA2 data while it is read.</summary>
public sealed class LzmaInputStream : Stream
{
    private readonly Stream _inputStream;
    private readonly long   _inputSize;
    private readonly long   _outputSize;

    private readonly int          _dictionarySize;
    private readonly OutWindow    _outWindow    = new();
    private readonly RangeDecoder _rangeDecoder = new();
    private          Decoder?     _decoder;

    private long _position;
    private bool _endReached;
    private long _availableBytes;
    private long _rangeDecoderLimit;
    private long _inputPosition;

    // LZMA2
    private readonly bool _isLzma2;
    private readonly bool _leaveOpen;

    private bool _uncompressedChunk;
    private bool _needDictReset = true;
    private bool _needProps     = true;
    private bool _isDisposed;

    /// <summary>Initializes a new <see cref="LzmaInputStream"/> whose compressed and decompressed sizes are unknown.</summary>
    /// <param name="properties">The LZMA or LZMA2 property bytes.</param>
    /// <param name="inputStream">The <see cref="Stream"/> containing compressed data.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inputStream"/> open when this stream is disposed.</param>
    public LzmaInputStream(byte[] properties, Stream inputStream, bool leaveOpen = false)
        : this(properties, inputStream, -1, -1, null, properties.Length < 5, leaveOpen) { }

    /// <summary>Initializes a new <see cref="LzmaInputStream"/> with a known compressed size.</summary>
    /// <param name="properties">The LZMA or LZMA2 property bytes.</param>
    /// <param name="inputStream">The <see cref="Stream"/> containing compressed data.</param>
    /// <param name="inputSize">The compressed data size, in bytes.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inputStream"/> open when this stream is disposed.</param>
    public LzmaInputStream(byte[] properties, Stream inputStream, long inputSize, bool leaveOpen = false)
        : this(properties, inputStream, inputSize, -1, null, properties.Length < 5, leaveOpen) { }

    /// <summary>Initializes a new <see cref="LzmaInputStream"/> with known compressed and decompressed sizes.</summary>
    /// <param name="properties">The LZMA or LZMA2 property bytes.</param>
    /// <param name="inputStream">The <see cref="Stream"/> containing compressed data.</param>
    /// <param name="inputSize">The compressed data size, in bytes.</param>
    /// <param name="outputSize">The decompressed data size, in bytes.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inputStream"/> open when this stream is disposed.</param>
    public LzmaInputStream(byte[] properties, Stream inputStream, long inputSize, long outputSize, bool leaveOpen = false)
        : this(properties, inputStream, inputSize, outputSize, null, properties.Length < 5, leaveOpen) { }

    /// <summary>Initializes a new <see cref="LzmaInputStream"/> with explicit format and dictionary settings.</summary>
    /// <param name="properties">The decoder property bytes.</param>
    /// <param name="inputStream">The <see cref="Stream"/> containing compressed data.</param>
    /// <param name="inputSize">The compressed data size, in bytes, or a negative value when unknown.</param>
    /// <param name="outputSize">The decompressed data size, in bytes, or a negative value when unknown.</param>
    /// <param name="presetDictionary">An optional preset dictionary <see cref="Stream"/>.</param>
    /// <param name="isLzma2">Whether the compressed data uses the LZMA2 container format.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inputStream"/> open when this stream is disposed.</param>
    public LzmaInputStream(
        byte[]  properties,
        Stream  inputStream,
        long    inputSize,
        long    outputSize,
        Stream? presetDictionary,
        bool    isLzma2,
        bool    leaveOpen = false)
    {
        _inputStream = inputStream;
        _inputSize   = inputSize;
        _outputSize  = outputSize;
        _isLzma2     = isLzma2;
        _leaveOpen   = leaveOpen;

        if (!isLzma2)
        {
            _dictionarySize = BinaryPrimitives.ReadInt32LittleEndian(properties.AsSpan(1));
            _outWindow.Create(_dictionarySize);
            if (presetDictionary != null)
            {
                _outWindow.Train(presetDictionary);
            }

            _rangeDecoder.Init(inputStream, inputSize);

            _decoder = new Decoder();
            _decoder.SetDecoderProperties(properties);
            Properties = properties;

            _availableBytes    = outputSize < 0 ? long.MaxValue : outputSize;
            _rangeDecoderLimit = inputSize;
        }
        else
        {
            _dictionarySize =   2 | (properties[0] & 1);
            _dictionarySize <<= (properties[0] >> 1) + 11;

            _outWindow.Create(_dictionarySize);
            if (presetDictionary != null)
            {
                _outWindow.Train(presetDictionary);
                _needDictReset = false;
            }

            Properties      = new byte[1];
            _availableBytes = 0;
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _decoder?.Dispose();
        _rangeDecoder.Dispose();
        _outWindow.Dispose();

        if (disposing && !_leaveOpen)
        {
            _inputStream.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override long Length => _position + _availableBytes;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_endReached)
        {
            return 0;
        }

        int total = 0;
        while (total < count)
        {
            if (_availableBytes == 0)
            {
                if (_isLzma2)
                {
                    DecodeChunkHeader();
                }
                else
                {
                    _endReached = true;
                }
                if (_endReached)
                {
                    break;
                }
            }

            int toProcess = count - total;
            if (toProcess > _availableBytes)
            {
                toProcess = (int)_availableBytes;
            }

            _outWindow.SetLimit(toProcess);
            if (_uncompressedChunk)
            {
                _inputPosition += _outWindow.CopyStream(_inputStream, toProcess);
            }
            else if (_decoder != null && _decoder.Code(_dictionarySize, _outWindow, _rangeDecoder) && _outputSize < 0)
            {
                _availableBytes = _outWindow.AvailableBytes;
            }

            int read = _outWindow.Read(buffer, offset, toProcess);
            total += read;
            offset += read;
            _position += read;
            _availableBytes -= read;

            if (_availableBytes != 0 || _uncompressedChunk) continue;

            // Check range corruption scenario
            if (!_rangeDecoder.IsFinished || (_rangeDecoderLimit >= 0 && _rangeDecoder.Total != _rangeDecoderLimit))
            {
                // Stream might have End Of Stream marker
                _outWindow.SetLimit(toProcess + 1);
                if (_decoder != null && !_decoder.Code(_dictionarySize, _outWindow, _rangeDecoder))
                {
                    _rangeDecoder.ReleaseStream();
                    throw new LzmaDataErrorException();
                }
            }

            _rangeDecoder.ReleaseStream();
            _inputPosition += _rangeDecoder.Total;
            if (_outWindow.HasPending)
            {
                throw new LzmaDataErrorException();
            }
        }

        if (!_endReached) return total;
        if ((_inputSize >= 0 && _inputPosition != _inputSize) || (_outputSize >= 0 && _position != _outputSize))
        {
            throw new LzmaDataErrorException();
        }

        return total;
    }

    private void DecodeChunkHeader()
    {
        int control = _inputStream.ReadByte();
        _inputPosition++;

        switch (control)
        {
            case 0x00:
                _endReached = true;
                return;
            case >= 0xE0:
            case 0x01:
                _needProps     = true;
                _needDictReset = false;
                _outWindow.Reset();
                break;
            default:
            {
                if (_needDictReset)
                {
                    throw new LzmaDataErrorException();
                }

                break;
            }
        }

        switch (control)
        {
            case >= 0x80:
            {
                _uncompressedChunk = false;

                _availableBytes =  (control & 0x1F) << 16;
                _availableBytes += (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition  += 2;

                _rangeDecoderLimit =  (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition     += 2;

                if (control >= 0xC0)
                {
                    _needProps    = false;
                    Properties[0] = (byte)_inputStream.ReadByte();
                    _inputPosition++;

                    _decoder ??= new Decoder();
                    _decoder.SetDecoderProperties(Properties);
                }
                else if (_needProps)
                {
                    throw new LzmaDataErrorException();
                }
                else if (control >= 0xA0)
                {
                    _decoder ??= new Decoder();
                    _decoder.SetDecoderProperties(Properties);
                }

                _rangeDecoder.Init(_inputStream, _rangeDecoderLimit);
                break;
            }
            case > 0x02:
                throw new LzmaDataErrorException();
            default:
                _uncompressedChunk =  true;
                _availableBytes    =  (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition     += 2;
                break;
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Gets the decoder property bytes used by this stream.</summary>
    public byte[] Properties { get; }
}
