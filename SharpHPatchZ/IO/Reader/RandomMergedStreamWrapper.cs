using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SharpHPatchZ.IO.Reader;

internal sealed class RandomMergedStreamWrapper : IDisposable
{
    private readonly ConcurrentDictionary<int, Lazy<FileStream>> _fileStreams = [];
    private readonly ReaderWriterLockSlim                        _lifetimeLock = new();
    private readonly long[]                                      _fileStreamEnds;
    private readonly string[]                                    _fileStreamPaths;
    private readonly FileAccess                                  _fileAccess;
    private          int                                         _disposed;

    public long Length => _fileStreamEnds.Length == 0 ? 0 : _fileStreamEnds[^1];

    public RandomMergedStreamWrapper(string[] fileStreams,
                                     long[]   fileStreamEnds,
                                     bool     createFiles = false)
    {
        if (fileStreams is null)
        {
            throw new ArgumentNullException(nameof(fileStreams));
        }

        if (fileStreamEnds is null)
        {
            throw new ArgumentNullException(nameof(fileStreamEnds));
        }

        if (fileStreams.Length != fileStreamEnds.Length)
        {
            throw new ArgumentException("The path and end-offset arrays must have the same length.",
                                        nameof(fileStreamEnds));
        }

        long previousEnd = 0;
        for (int index = 0; index < fileStreams.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(fileStreams[index]))
            {
                throw new ArgumentException("A file path cannot be null or empty.", nameof(fileStreams));
            }

            if (fileStreamEnds[index] < previousEnd)
            {
                throw new ArgumentException("File end offsets must be non-negative and ordered.",
                                            nameof(fileStreamEnds));
            }

            previousEnd = fileStreamEnds[index];
        }

        _fileStreamPaths = fileStreams;
        _fileStreamEnds  = fileStreamEnds;
        _fileAccess      = createFiles ? FileAccess.ReadWrite : FileAccess.Read;

        if (createFiles)
        {
            CreateOutputFiles();
        }
    }

    public void Write(Span<byte> buffer, long offset)
    {
        StreamCursor cursor = default;
        Write(buffer, offset, ref cursor);
    }

    internal void Write(Span<byte> buffer, long offset, ref StreamCursor cursor)
    {
        using AccessScope access = AcquireAccess();
        access.Write(buffer, offset, ref cursor);
    }

    private void WriteCore(Span<byte> buffer, long offset, ref StreamCursor cursor)
    {
        ValidateOffset(offset);

        if (buffer.Length > Length - offset)
        {
            throw new EndOfStreamException("The write exceeds the merged stream length.");
        }

        while (!buffer.IsEmpty)
        {
            int  streamIndex = FindStreamIndex(offset, cursor.StreamIndex);
            long streamStart = GetStreamStart(streamIndex);
            int  writeLength = (int)Math.Min(buffer.Length, _fileStreamEnds[streamIndex] - offset);

            FileStream stream = GetFileStream(streamIndex, ref cursor);
            RandomAccessCompat.Write(stream.SafeFileHandle!,
                                     buffer[..writeLength],
                                     offset - streamStart);

            buffer = buffer[writeLength..];
            offset += writeLength;
        }
    }

    public int Read(Span<byte> buffer, long offset)
    {
        StreamCursor cursor = default;
        return Read(buffer, offset, ref cursor);
    }

    internal int Read(Span<byte> buffer, long offset, ref StreamCursor cursor)
    {
        using AccessScope access = AcquireAccess();
        return access.Read(buffer, offset, ref cursor);
    }

    private int ReadCore(Span<byte> buffer, long offset, ref StreamCursor cursor)
    {
        ValidateOffset(offset);

        int totalRead = 0;
        while (!buffer.IsEmpty && offset < Length)
        {
            int  streamIndex = FindStreamIndex(offset, cursor.StreamIndex);
            long streamStart = GetStreamStart(streamIndex);
            int  readLength  = (int)Math.Min(buffer.Length, _fileStreamEnds[streamIndex] - offset);

            FileStream stream = GetFileStream(streamIndex, ref cursor);
            int read = RandomAccessCompat.Read(stream.SafeFileHandle!,
                                               buffer[..readLength],
                                               offset - streamStart);

            if (read == 0)
            {
                throw new EndOfStreamException("An underlying file ended before its declared length.");
            }

            totalRead += read;
            offset    += read;
            buffer    =  buffer[read..];
        }

        return totalRead;
    }

    internal AccessScope AcquireAccess()
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new AccessScope(this);
        }
        catch
        {
            _lifetimeLock.ExitReadLock();
            throw;
        }
    }

    public void Dispose()
    {
        _lifetimeLock.EnterWriteLock();
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            foreach (KeyValuePair<int, Lazy<FileStream>> kvp in _fileStreams)
            {
                if (kvp.Value.IsValueCreated)
                {
                    kvp.Value.Value.Dispose();
                }
            }
            _fileStreams.Clear();
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }
    }

    private FileStream GetFileStream(int streamIndex)
    {
        Lazy<FileStream> lazyStream = _fileStreams.GetOrAdd(
            streamIndex,
            index => new Lazy<FileStream>(
                () => new FileStream(_fileStreamPaths[index],
                                     FileMode.Open,
                                     _fileAccess,
                                     FileShare.ReadWrite,
                                     bufferSize: 1,
                                     FileOptions.RandomAccess),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazyStream.Value;
    }

    private FileStream GetFileStream(int streamIndex, ref StreamCursor cursor)
    {
        if (cursor.Stream is not null && cursor.StreamIndex == streamIndex)
        {
            return cursor.Stream;
        }

        FileStream stream = GetFileStream(streamIndex);
        cursor.StreamIndex = streamIndex;
        cursor.Stream      = stream;
        return stream;
    }

    private void CreateOutputFiles()
    {
        long streamStart = 0;
        for (int index = 0; index < _fileStreamPaths.Length; index++)
        {
            string path = _fileStreamPaths[index];
            string? directoryPath = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            using FileStream stream = new(path,
                                          FileMode.Create,
                                          FileAccess.ReadWrite,
                                          FileShare.ReadWrite,
                                          bufferSize: 1,
                                          FileOptions.RandomAccess);
            stream.SetLength(_fileStreamEnds[index] - streamStart);
            streamStart = _fileStreamEnds[index];
        }
    }

    private int FindStreamIndex(long offset)
    {
        int low  = 0;
        int high = _fileStreamEnds.Length - 1;

        // Find the first segment whose cumulative end is greater than the offset.
        // This also skips any zero-length segments.
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_fileStreamEnds[middle] > offset)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private int FindStreamIndex(long offset, int cachedStreamIndex)
    {
        if ((uint)cachedStreamIndex < (uint)_fileStreamEnds.Length)
        {
            long streamStart = GetStreamStart(cachedStreamIndex);
            if (offset >= streamStart && offset < _fileStreamEnds[cachedStreamIndex])
            {
                return cachedStreamIndex;
            }
        }

        return FindStreamIndex(offset);
    }

    private long GetStreamStart(int streamIndex)
        => streamIndex == 0 ? 0 : _fileStreamEnds[streamIndex - 1];

    private void ValidateOffset(long offset)
    {
        if ((ulong)offset > (ulong)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RandomMergedStreamWrapper));
        }
    }

    internal struct StreamCursor
    {
        public int         StreamIndex;
        public FileStream? Stream;
    }

    internal readonly ref struct AccessScope(RandomMergedStreamWrapper owner)
    {
        public int Read(Span<byte> buffer, long offset, ref StreamCursor cursor)
            => owner.ReadCore(buffer, offset, ref cursor);

        public void Write(Span<byte> buffer, long offset, ref StreamCursor cursor)
            => owner.WriteCore(buffer, offset, ref cursor);

        public void Dispose()
            => owner._lifetimeLock.ExitReadLock();
    }
}
