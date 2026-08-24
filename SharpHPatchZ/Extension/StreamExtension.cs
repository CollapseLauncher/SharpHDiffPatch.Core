using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Reader;

namespace SharpHPatchZ.Extension;

internal static class StreamExtension
{
    extension(Stream stream)
    {
#if NET6_0
        public async ValueTask ReadExactlyAsync(
            Memory<byte>      buffer,
            CancellationToken cancellationToken = default)
        {
            await stream.ReadAtLeastAsync(buffer, buffer.Length, true, cancellationToken);
        }
#endif

        public void ReadExactly(
            byte[]            buffer,
            int               offset,
            int               count)
        {
#if !NET6_0_OR_GREATER
            _ = stream.ReadAtLeast(buffer, offset, count, count, true);
#else
            _ = stream.ReadAtLeast(buffer.AsSpan(offset, count), count, true);
#endif
        }

        public async ValueTask ReadExactlyAsync(
            byte[]            buffer,
            int               offset,
            int               count,
            CancellationToken cancellationToken = default)
        {
#if !NET6_0_OR_GREATER
            ValueTask<int> vt = stream.ReadAtLeastAsync(buffer, offset, count, count, true, cancellationToken);
            await vt;
#else
            await stream.ReadAtLeastAsync(buffer.AsMemory(offset, count), count, true, cancellationToken);
#endif
        }

#if !NET6_0_OR_GREATER
        public int ReadAtLeast(
            byte[]            buffer,
            int               offset,
            int               count,
            int               minimumBytes,
            bool              throwOnEndOfStream = false)
        {
            Debug.Assert(minimumBytes <= buffer.Length);

            int totalRead = offset;
            while (totalRead < minimumBytes)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                {
                    return throwOnEndOfStream ? throw new EndOfStreamException() : totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }

        public async ValueTask<int> ReadAtLeastAsync(
            byte[]            buffer,
            int               offset,
            int               count,
            int               minimumBytes,
            bool              throwOnEndOfStream = false,
            CancellationToken cancellationToken  = default)
        {
            Debug.Assert(minimumBytes <= buffer.Length);

            int totalRead = offset;
            while (totalRead < minimumBytes)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return throwOnEndOfStream ? throw new EndOfStreamException() : totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }
#else
        public int ReadAtLeast(
            Span<byte> buffer,
            int        minimumBytes,
            bool       throwOnEndOfStream = false)
        {
            Debug.Assert(minimumBytes <= buffer.Length);

            int totalRead = 0;
            while (totalRead < minimumBytes)
            {
                int read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    return throwOnEndOfStream ? throw new EndOfStreamException() : totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }

        public async ValueTask<int> ReadAtLeastAsync(
            Memory<byte>      buffer,
            int               minimumBytes,
            bool              throwOnEndOfStream = false,
            CancellationToken cancellationToken  = default)
        {
            Debug.Assert(minimumBytes <= buffer.Length);

            int totalRead = 0;
            while (totalRead < minimumBytes)
            {
                int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return throwOnEndOfStream ? throw new EndOfStreamException() : totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }
#endif
    }

    public static unsafe UnmanagedArray<Utf16UnmanagedString>* CreateUnmanagedStringList(
        scoped ReadOnlySpan<byte> buffer, int count)
    {
        UnmanagedArray<Utf16UnmanagedString>* unmanagedStringArray = UnmanagedArray<Utf16UnmanagedString>.CreateAllocUnsafe(count);
        Span<Utf16UnmanagedString> unmanagedStringSpan = unmanagedStringArray->GetSpan();
        int index = 0;
        do
        {
            int indexOfNull = buffer.IndexOf((byte)0);
            if (indexOfNull < 0)
            {
                throw ExceptionHelper.ThrowHDiffStreamReadOutOfBound();
            }

            ReadOnlySpan<byte> currentSlice = buffer[..indexOfNull];
            unmanagedStringSpan[index++] = Utf16UnmanagedString.CreateFromManaged(currentSlice);

            buffer = buffer[(indexOfNull + 1)..];
        } while (!buffer.IsEmpty);

        return index != count
            ? throw ExceptionHelper.ThrowHDiffStreamReadOutOfBound()
            : unmanagedStringArray;
    }

    extension(BittableStreamReader reader)
    {
        public async ValueTask<nint> CreateUnmanagedStringListAsync(int count, int bufferSize, CancellationToken token)
        {
            if (count == 0)
            {
                return 0;
            }

            long readerOffset = reader.Offset;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                // Preload string buffer
                await reader.ReadBytesAsync(buffer.AsMemory(0, bufferSize), token);
                unsafe
                {
                    return reader.Offset != readerOffset + bufferSize
                        ? throw ExceptionHelper.ThrowHDiffStreamReadOutOfBound()
                        : (nint)CreateUnmanagedStringList(buffer.AsSpan(0, bufferSize), count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public unsafe UnmanagedArray<Utf16UnmanagedString>* CreateUnmanagedStringList(int count, int bufferSize)
        {
            if (count == 0)
            {
                return null;
            }

            long readerOffset = reader.Offset;

            byte[]?    buffer     = bufferSize > 4 << 10 ? ArrayPool<byte>.Shared.Rent(bufferSize) : null;
            Span<byte> bufferSpan = (buffer ?? stackalloc byte[bufferSize])[..bufferSize];

            try
            {
                // Preload string buffer
                reader.ReadBytes(bufferSpan);
                return reader.Offset != readerOffset + bufferSize
                    ? throw ExceptionHelper.ThrowHDiffStreamReadOutOfBound()
                    : CreateUnmanagedStringList(bufferSpan, count);
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async ValueTask<nint> CreateUnmanagedInt64ListAsync(int count, CancellationToken token)
        {
            if (count == 0)
            {
                return 0;
            }

            long[] array = ArrayPool<long>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    array[i] = await reader.ReadLong7BitAsync(token);
                }

                unsafe
                {
                    UnmanagedArray<long>* allocArray = UnmanagedArray<long>.CreateAllocUnsafe(count);
                    array.AsSpan(0, count).CopyTo(allocArray->GetSpan());
                    return (nint)allocArray;
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(array);
            }
        }

        public unsafe UnmanagedArray<long>* CreateUnmanagedInt64List(int count)
        {
            if (count == 0)
            {
                return null;
            }

            UnmanagedArray<long>* allocArray = UnmanagedArray<long>.CreateAllocUnsafe(count);
            Span<long>            allocSpan  = allocArray->GetSpan();

            for (int i = 0; i < count; i++)
            {
                allocSpan[i] = reader.ReadLong7Bit();
            }

            return allocArray;
        }

        public async ValueTask<nint> CreateUnmanagedInt64As32ListAsync(int count, CancellationToken token)
        {
            if (count == 0)
            {
                return 0;
            }

            int[] array = ArrayPool<int>.Shared.Rent(count);
            try
            {
                long backNumber = -1;
                for (int i = 0; i < count; i++)
                {
                    array[i] = (int)(backNumber += 1 + await reader.ReadLong7BitAsync(token));
                }

                unsafe
                {
                    UnmanagedArray<int>* allocArray = UnmanagedArray<int>.CreateAllocUnsafe(count);
                    array.AsSpan(0, count).CopyTo(allocArray->GetSpan());
                    return (nint)allocArray;
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(array);
            }
        }

        public unsafe UnmanagedArray<int>* CreateUnmanagedInt64As32List(int count)
        {
            if (count == 0)
            {
                return null;
            }

            UnmanagedArray<int>* allocArray = UnmanagedArray<int>.CreateAllocUnsafe(count);
            Span<int>            allocSpan  = allocArray->GetSpan();

            long backNumber = -1;
            for (int i = 0; i < count; i++)
            {
                allocSpan[i] = (int)(backNumber += 1 + reader.ReadLong7Bit());
            }

            return allocArray;
        }

        public async ValueTask<nint> CreateUnmanagedIndexPairListAsync(int count, CancellationToken token)
        {
            if (count == 0)
            {
                return 0;
            }

            FileIndexPair[] array = ArrayPool<FileIndexPair>.Shared.Rent(count);
            try
            {
                long oldIndexNumber = -1;
                long newIndexNumber = -1;
                for (int i = 0; i < count; i++)
                {
                    oldIndexNumber += 1 + await reader.ReadLong7BitAsync(token);
                    newIndexNumber += 1 + await reader.ReadLong7BitAsync(token);
                    array[i] = new FileIndexPair
                    {
                        OldIndex = (int)oldIndexNumber,
                        NewIndex = (int)newIndexNumber
                    };
                }

                unsafe
                {
                    FileIndexPair* alloc = MemoryAlloc.Alloc<FileIndexPair>(count);
                    array.AsSpan(0, count).CopyTo(new Span<FileIndexPair>(alloc, count));
                    return (nint)alloc;
                }
            }
            finally
            {
                ArrayPool<FileIndexPair>.Shared.Return(array);
            }
        }

        public unsafe FileIndexPair* CreateUnmanagedIndexPairList(int count)
        {
            if (count == 0)
            {
                return null;
            }

            FileIndexPair*      alloc          = MemoryAlloc.Alloc<FileIndexPair>(count);
            Span<FileIndexPair> allocSpan      = new(alloc, count);
            long                oldIndexNumber = -1;
            long                newIndexNumber = -1;
            for (int i = 0; i < count; i++)
            {
                long incNewValue = reader.ReadLong7Bit();
                newIndexNumber    += 1 + incNewValue;

                long incOldValue = reader.ReadLong7Bit(1);
                int  pSign       = reader.PreviousByte;

                if (pSign >> (8 - 1) == 0)
                    oldIndexNumber += 1 + incOldValue;
                else
                    oldIndexNumber = oldIndexNumber + 1 - incOldValue;

                allocSpan[i].OldIndex = (int)oldIndexNumber;
                allocSpan[i].NewIndex = (int)newIndexNumber;
            }

            return alloc;
        }

        public async ValueTask AdvanceSeekToAsync(int advancedBytes, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(advancedBytes);
            try
            {
                await reader.ReadBytesAsync(buffer.AsMemory(0, advancedBytes), token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void AdvanceSeekTo(int advancedBytes)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(advancedBytes);
            try
            {
                reader.ReadBytes(buffer.AsSpan(0, advancedBytes));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
