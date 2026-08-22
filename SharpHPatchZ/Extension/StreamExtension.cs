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

    extension(BittableStreamReader reader)
    {
        public async ValueTask<nint> CreateUnmanagedStringListAsync(int count, int bufferSize, CancellationToken token)
        {
            if (count == 0)
            {
                return 0;
            }

            long readerOffset = reader.Offset;
            UnmanagedArray<Utf16UnmanagedString> unmanagedBuffer = UnmanagedArray<Utf16UnmanagedString>.CreateAlloc(count);

            for (int i = 0; i < count; i++)
            {
                string readString = await reader.ReadStringToNullAsync(token);

                ref Utf16UnmanagedString current = ref unmanagedBuffer[i];
                current = Utf16UnmanagedString.CreateFromManaged(readString);
                if (reader.Offset - readerOffset > bufferSize)
                {
                    throw new IndexOutOfRangeException("Read is out of index!");
                }
            }

            return unmanagedBuffer.CopyToUnmanaged();
        }

        public unsafe UnmanagedArray<Utf16UnmanagedString>* CreateUnmanagedStringList(int count, int bufferSize)
        {
            if (count == 0)
            {
                return null;
            }

            long                                  readerOffset      = reader.Offset;
            UnmanagedArray<Utf16UnmanagedString>* readOnlyArraySpan = UnmanagedArray<Utf16UnmanagedString>.CreateAllocUnsafe(count);

            for (int i = 0; i < count; i++)
            {
                readOnlyArraySpan[0][i] = Utf16UnmanagedString.CreateFromManaged(reader.ReadStringToNull());
                if (reader.Offset - readerOffset > bufferSize)
                {
                    throw new IndexOutOfRangeException("Read is out of index!");
                }
            }

            return readOnlyArraySpan;
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
                long backNumber = -1;
                for (int i = 0; i < count; i++)
                {
                    array[i] = backNumber += 1 + await reader.ReadLong7BitAsync(token);
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

            long backNumber = -1;
            for (int i = 0; i < count; i++)
            {
                allocSpan[i] = backNumber += 1 + reader.ReadLong7Bit();
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

            FileIndexPair* alloc          = MemoryAlloc.Alloc<FileIndexPair>(count);
            long           oldIndexNumber = -1;
            long           newIndexNumber = -1;
            for (int i = 0; i < count; i++)
            {
                alloc[i].OldIndex = (int)(oldIndexNumber += 1 + reader.ReadLong7Bit());
                alloc[i].NewIndex = (int)(newIndexNumber += 1 + reader.ReadLong7Bit());
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
