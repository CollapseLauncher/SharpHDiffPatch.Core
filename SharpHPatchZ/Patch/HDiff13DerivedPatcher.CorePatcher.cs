using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
#if NET6_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Reader;
// ReSharper disable AccessToDisposedClosure

namespace SharpHPatchZ.Patch;

internal sealed partial class HDiff13DerivedPatcher
{
    private const int DefaultPatchBufferSize = 1 << 20;

    private unsafe void StartCorePatcher(CancellationToken token)
    {
        ReadOnlySpan<RleCoverInfo> rleCoverSpan = RleCoverInfo.Read(_coverReader,
                                                                    Info,
                                                                    out RleCoverInfo[] rleCoverBackedBuffer,
                                                                    token);
        try
        {
            if (InputStream is null || OutputStream is null)
            {
                throw new InvalidOperationException("The patch input and output streams have not been initialized.");
            }

            PatchMetadata patchMetadata = Info.GetPatchMetadata();
            ValidateReaderOffset(_coverReader,
                                 patchMetadata.CoverDataSizeP->Size,
                                 "cover");

            int bufferSize = Options.PatchWorkerBufferSize > 0
                ? Options.PatchWorkerBufferSize
                : DefaultPatchBufferSize;
            int workerCount = GetWorkerCount(Options.ParallelThreads);

            if (workerCount == 1)
            {
                RunSequential(rleCoverSpan, patchMetadata, bufferSize, token);
            }
            else
            {
                RunParallel(rleCoverBackedBuffer,
                            rleCoverSpan.Length,
                            patchMetadata,
                            bufferSize,
                            workerCount,
                            token);
            }
        }
        finally
        {
            BigArrayPool<RleCoverInfo>.Shared.Return(rleCoverBackedBuffer);
        }
    }

    private static int GetWorkerCount(uint requestedWorkerCount)
    {
        if (requestedWorkerCount == 0)
        {
            return Math.Max(1, Environment.ProcessorCount);
        }

        if (requestedWorkerCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(PatchOptions.ParallelThreads));
        }

        return (int)requestedWorkerCount;
    }

    private void RunSequential(ReadOnlySpan<RleCoverInfo> covers,
                               PatchMetadata              patchMetadata,
                               int                        bufferSize,
                               CancellationToken          token)
    {
        byte[] oldBuffer = BigArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            ProduceWork(covers,
                        patchMetadata.DiffNewSize,
                        bufferSize,
                        item => ProcessWorkItem(item, oldBuffer, token),
                        token);
            ValidateDataReaders(patchMetadata);
        }
        finally
        {
            BigArrayPool<byte>.Shared.Return(oldBuffer);
        }
    }

    private void RunParallel(RleCoverInfo[]    covers,
                             int               coverCount,
                             PatchMetadata     patchMetadata,
                             int               bufferSize,
                             int               workerCount,
                             CancellationToken token)
    {
        int queueCapacity = workerCount > int.MaxValue / 2
            ? int.MaxValue
            : workerCount * 2;

        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        using BlockingCollection<PatchWorkItem> workQueue = new(queueCapacity);

        ExceptionDispatchInfo? producerFailure = null;
        ExceptionDispatchInfo? workerFailure   = null;
        ExceptionDispatchInfo? parallelFailure = null;

        Task producer = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    ProduceWork(covers.AsSpan(0, coverCount),
                                patchMetadata.DiffNewSize,
                                bufferSize,
                                item => AddWorkItem(workQueue, item, linkedCancellation.Token),
                                linkedCancellation.Token);
                    ValidateDataReaders(patchMetadata);
                }
                catch (Exception exception)
                {
                    producerFailure = ExceptionDispatchInfo.Capture(exception);
                    linkedCancellation.Cancel();
                }
                finally
                {
                    workQueue.CompleteAdding();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Parallel.ForEach(
                workQueue.GetConsumingEnumerable(linkedCancellation.Token),
                new ParallelOptions
                {
                    CancellationToken      = linkedCancellation.Token,
                    MaxDegreeOfParallelism = workerCount
                },
                () => BigArrayPool<byte>.Shared.Rent(bufferSize),
                (item, _, oldBuffer) =>
                {
                    try
                    {
                        ProcessWorkItem(item, oldBuffer, linkedCancellation.Token);
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(
                            ref workerFailure,
                            ExceptionDispatchInfo.Capture(exception),
                            null);
                        linkedCancellation.Cancel();
                        throw;
                    }

                    return oldBuffer;
                },
                oldBuffer => BigArrayPool<byte>.Shared.Return(oldBuffer));
        }
        catch (Exception exception)
        {
            parallelFailure = ExceptionDispatchInfo.Capture(exception);
            linkedCancellation.Cancel();
        }
        finally
        {
            producer.GetAwaiter().GetResult();

            while (workQueue.TryTake(out PatchWorkItem abandonedItem))
            {
                BigArrayPool<byte>.Shared.Return(abandonedItem.Buffer);
            }
        }

        // A real producer error is more useful than the cancellation observed by
        // workers after the producer stopped. Conversely, a worker error takes
        // precedence when cancellation is all the producer observed.
        if (producerFailure?.SourceException is not OperationCanceledException)
        {
            producerFailure?.Throw();
        }

        workerFailure?.Throw();
        producerFailure?.Throw();
        parallelFailure?.Throw();
    }

    private static void AddWorkItem(BlockingCollection<PatchWorkItem> queue,
                                    PatchWorkItem                     item,
                                    CancellationToken                 token)
    {
        try
        {
            queue.Add(item, token);
        }
        catch
        {
            BigArrayPool<byte>.Shared.Return(item.Buffer);
            throw;
        }
    }

    private void ProduceWork(ReadOnlySpan<RleCoverInfo> covers,
                             long                       newDataSize,
                             int                        bufferSize,
                             Action<PatchWorkItem>      emit,
                             CancellationToken          token)
    {
        RleDecoder rleDecoder = new(_rleCtrlReader, _rleCodeReader);
        byte[] skipBuffer = BigArrayPool<byte>.Shared.Rent(Math.Min(bufferSize, 64 << 10));
        using PatchWorkBuilder workBuilder = new(bufferSize, emit);
        try
        {
            long newPosition = 0;
            for (int coverIndex = 0; coverIndex < covers.Length; coverIndex++)
            {
                token.ThrowIfCancellationRequested();
                ref readonly RleCoverInfo cover = ref covers[coverIndex];
                ValidateCover(cover, newPosition, newDataSize, InputStream!.Length);

                ProduceNewData(cover.CopyLength,
                               ref newPosition,
                               rleDecoder,
                               skipBuffer,
                               workBuilder,
                               token);
                ProduceCover(cover,
                             ref newPosition,
                             rleDecoder,
                             workBuilder,
                             token);
            }

            ProduceNewData(newDataSize - newPosition,
                           ref newPosition,
                           rleDecoder,
                           skipBuffer,
                           workBuilder,
                           token);

            if (newPosition != newDataSize || rleDecoder.HasPendingData)
            {
                throw new InvalidDataException("The decoded patch data does not match the declared output size.");
            }

            workBuilder.Flush();
        }
        finally
        {
            BigArrayPool<byte>.Shared.Return(skipBuffer);
        }
    }

    private void ProduceNewData(long              length,
                                ref long          newPosition,
                                RleDecoder        rleDecoder,
                                byte[]            skipBuffer,
                                PatchWorkBuilder  workBuilder,
                                CancellationToken token)
    {
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> destination = workBuilder.GetWritableSpan(newPosition,
                                                                 length,
                                                                 out int step);
            _newDataReader.ReadBytes(destination);
            rleDecoder.Skip(step, skipBuffer, token);
            workBuilder.CommitNewData(step);

            newPosition += step;
            length      -= step;
        }
    }

    private static void ProduceCover(RleCoverInfo      cover,
                                     ref long          newPosition,
                                     RleDecoder        rleDecoder,
                                     PatchWorkBuilder  workBuilder,
                                     CancellationToken token)
    {
        long coverOffset = 0;
        while (coverOffset < cover.RleLength)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> destination = workBuilder.GetWritableSpan(
                newPosition,
                cover.RleLength - coverOffset,
                out int step);
            rleDecoder.Decode(destination, token);
            workBuilder.CommitCover(step, cover.OldStreamPosition + coverOffset);

            newPosition += step;
            coverOffset += step;
        }
    }

    private void ProcessWorkItem(PatchWorkItem     item,
                                 byte[]            oldBuffer,
                                 CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (item.CoverSegments is { } coverSegments)
            {
                for (int segmentIndex = 0; segmentIndex < coverSegments.Count; segmentIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    CoverSegment segment = coverSegments[segmentIndex];
                    Span<byte> oldData = oldBuffer.AsSpan(0, segment.Length);
                    int read = InputStream!.Read(oldData, segment.OldPosition);
                    if (read != segment.Length)
                    {
                        throw new EndOfStreamException("The old-data stream ended while processing a cover.");
                    }

#if NET6_0_OR_GREATER
                    AddRle(oldBuffer,
                           item.Buffer,
                           segment.BufferOffset,
                           segment.Length,
                           Options.UseSIMD);
#else
                    AddRle(oldBuffer,
                           item.Buffer,
                           segment.BufferOffset,
                           segment.Length);
#endif
                    oldData.CopyTo(item.Buffer.AsSpan(segment.BufferOffset, segment.Length));
                }
            }

            OutputStream!.Write(item.Buffer.AsSpan(0, item.Length), item.OutputPosition);
            AdvanceProgress(item.Length);
        }
        finally
        {
            BigArrayPool<byte>.Shared.Return(item.Buffer);
        }
    }

#if NET6_0_OR_GREATER
    private static void AddRle(byte[] destination,
                               byte[] rle,
                               int    rleOffset,
                               int    length,
                               bool   useSimd)
#else
    private static void AddRle(byte[] destination,
                               byte[] rle,
                               int    rleOffset,
                               int    length)
#endif
    {
        int index = 0;
#if NET6_0_OR_GREATER
        if (useSimd && Avx2.IsSupported)
        {
            const int vectorLength = 32;
            int vectorEnd = length - length % vectorLength;
            if (vectorEnd != 0)
            {
                ref byte destinationRef = ref destination[0];
                ref byte rleRef         = ref rle[rleOffset];
                for (; index < vectorEnd; index += vectorLength)
                {
                    var oldVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref destinationRef, index));
                    var rleVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref rleRef, index));
                    Vector256<byte> result = Avx2.Add(oldVector, rleVector);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationRef, index), result);
                }
            }
        }
        else if (useSimd && Sse2.IsSupported)
        {
            const int vectorLength = 16;
            int vectorEnd = length - length % vectorLength;
            if (vectorEnd != 0)
            {
                ref byte destinationRef = ref destination[0];
                ref byte rleRef         = ref rle[rleOffset];
                for (; index < vectorEnd; index += vectorLength)
                {
                    var oldVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref destinationRef, index));
                    var rleVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rleRef, index));
                    Vector128<byte> result = Sse2.Add(oldVector, rleVector);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationRef, index), result);
                }
            }
        }
        else if (useSimd && Vector.IsHardwareAccelerated)
        {
            int vectorLength = Vector<byte>.Count;
            int vectorEnd    = length - length % vectorLength;
            for (; index < vectorEnd; index += vectorLength)
            {
                Vector<byte> oldVector = new(destination, index);
                Vector<byte> rleVector = new(rle, rleOffset + index);
                (oldVector + rleVector).CopyTo(destination, index);
            }
        }
#endif
        for (; index < length; index++)
        {
            destination[index] = unchecked((byte)(destination[index] + rle[rleOffset + index]));
        }
    }

    private static void ValidateCover(RleCoverInfo cover,
                                      long         expectedNewPosition,
                                      long         newDataSize,
                                      long         oldDataSize)
    {
        if (cover.CopyLength < 0 || cover.RleLength < 0 || cover.OldStreamPosition < 0 ||
            cover.NewStreamPosition < expectedNewPosition ||
            cover.CopyLength != cover.NewStreamPosition - expectedNewPosition ||
            cover.NewStreamPosition > newDataSize ||
            cover.RleLength > newDataSize - cover.NewStreamPosition ||
            cover.OldStreamPosition > oldDataSize ||
            cover.RleLength > oldDataSize - cover.OldStreamPosition)
        {
            throw new InvalidDataException("A patch cover is outside the declared old or new data bounds.");
        }
    }

    private unsafe void ValidateDataReaders(PatchMetadata patchMetadata)
    {
        ValidateReaderOffset(_rleCtrlReader, patchMetadata.RleControlDataSizeP->Size, "RLE control");
        ValidateReaderOffset(_rleCodeReader, patchMetadata.RleCodeDataSizeP->Size,    "RLE code");
        ValidateReaderOffset(_newDataReader, patchMetadata.NewDiffDataSizeP->Size,    "new-data");
    }

    private static void ValidateReaderOffset(BittableStreamReader reader,
                                             long                 expectedOffset,
                                             string               dataName)
    {
        if (reader.Offset != expectedOffset)
        {
            throw new InvalidDataException(
                $"The consumed {dataName} size ({reader.Offset}) does not match its declared size ({expectedOffset}).");
        }
    }

    private readonly struct PatchWorkItem(byte[]              buffer,
                                          int                 length,
                                          long                outputPosition,
                                          List<CoverSegment>? coverSegments)
    {
        public byte[]              Buffer         { get; } = buffer;
        public int                 Length         { get; } = length;
        public long                OutputPosition { get; } = outputPosition;
        public List<CoverSegment>? CoverSegments  { get; } = coverSegments;
    }

    private readonly struct CoverSegment(int bufferOffset,
                                         int length,
                                         long oldPosition)
    {
        public int  BufferOffset { get; } = bufferOffset;
        public int  Length       { get; } = length;
        public long OldPosition  { get; } = oldPosition;
    }

    private sealed class PatchWorkBuilder(int                   bufferSize,
                                          Action<PatchWorkItem> emit) : IDisposable
    {
        private readonly int                   _bufferSize = bufferSize;
        private readonly Action<PatchWorkItem> _emit       = emit;

        private byte[]?             _buffer;
        private int                 _length;
        private long                _outputPosition;
        private List<CoverSegment>? _coverSegments;

        public Span<byte> GetWritableSpan(long outputPosition,
                                          long requestedLength,
                                          out int writableLength)
        {
            if (_buffer is not null && _length == _bufferSize)
            {
                Flush();
            }

            if (_buffer is null)
            {
                _buffer         = BigArrayPool<byte>.Shared.Rent(_bufferSize);
                _outputPosition = outputPosition;
            }
            else if (outputPosition != _outputPosition + _length)
            {
                throw new InvalidOperationException("Patch batches must contain contiguous output ranges.");
            }

            writableLength = (int)Math.Min(requestedLength, _bufferSize - _length);
            return _buffer.AsSpan(_length, writableLength);
        }

        public void CommitNewData(int length)
            => _length += length;

        public void CommitCover(int length, long oldPosition)
        {
            _coverSegments ??= [];

            int segmentCount = _coverSegments.Count;
            if (segmentCount > 0)
            {
                CoverSegment previous = _coverSegments[segmentCount - 1];
                if (previous.BufferOffset + previous.Length == _length &&
                    previous.OldPosition + previous.Length == oldPosition)
                {
                    _coverSegments[segmentCount - 1] = new CoverSegment(previous.BufferOffset,
                                                                        previous.Length + length,
                                                                        previous.OldPosition);
                    _length += length;
                    return;
                }
            }

            _coverSegments.Add(new CoverSegment(_length, length, oldPosition));
            _length += length;
        }

        public void Flush()
        {
            if (_buffer is null || _length == 0)
            {
                return;
            }

            PatchWorkItem item = new(_buffer,
                                     _length,
                                     _outputPosition,
                                     _coverSegments);
            _buffer        = null;
            _length        = 0;
            _coverSegments = null;
            _emit(item);
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                BigArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }

    private sealed class RleDecoder(BittableStreamReader controlReader,
                                    BittableStreamReader codeReader)
    {
        private readonly BittableStreamReader _controlReader = controlReader;
        private readonly BittableStreamReader _codeReader    = codeReader;

        private long _remaining;
        private byte _type;
        private byte _value;

        public bool HasPendingData => _remaining != 0;

        public void Decode(Span<byte> destination, CancellationToken token)
        {
            while (!destination.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                EnsureRun();

                int step = (int)Math.Min(_remaining, destination.Length);
                Span<byte> target = destination[..step];
                switch (_type)
                {
                    case 0:
                        target.Clear();
                        break;
                    case 1:
                        target.Fill(byte.MaxValue);
                        break;
                    case 2:
                        target.Fill(_value);
                        break;
                    case 3:
                        _codeReader.ReadBytes(target);
                        break;
                    default:
                        throw new InvalidDataException("The RLE control stream contains an unknown run type.");
                }

                _remaining -= step;
                destination =  destination[step..];
            }
        }

        public void Skip(long length, byte[] scratchBuffer, CancellationToken token)
        {
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                EnsureRun();

                long step = Math.Min(_remaining, length);
                if (_type == 3)
                {
                    long rawRemaining = step;
                    while (rawRemaining > 0)
                    {
                        int readLength = (int)Math.Min(rawRemaining, scratchBuffer.Length);
                        _codeReader.ReadBytes(scratchBuffer.AsSpan(0, readLength));
                        rawRemaining -= readLength;
                    }
                }

                _remaining -= step;
                length     -= step;
            }
        }

        private void EnsureRun()
        {
            if (_remaining != 0)
            {
                return;
            }

            byte firstByte = _controlReader.ReadByte();
            _type = (byte)(firstByte >> (8 - BittableStreamReader.KByteRleType));

            long encodedLength = _controlReader.ReadLong7Bit(BittableStreamReader.KByteRleType,
                                                             firstByte);
            if (encodedLength == long.MaxValue)
            {
                throw new InvalidDataException("An RLE run length exceeds the supported range.");
            }

            _remaining = encodedLength + 1;
            if (_type == 2)
            {
                _value = _codeReader.ReadByte();
            }
        }
    }
}
