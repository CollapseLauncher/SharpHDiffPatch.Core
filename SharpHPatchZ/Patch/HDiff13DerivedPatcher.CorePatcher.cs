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
        using NativeMemoryBuffer<RleCoverInfo> rleCoverBuffer = RleCoverInfo.Read(_coverReader,
                                                                                  Info,
                                                                                  token);
        ReadOnlySpan<RleCoverInfo> rleCoverSpan = rleCoverBuffer.Span;
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
            RunParallel(rleCoverBuffer,
                        patchMetadata,
                        bufferSize,
                        workerCount,
                        token);
        }
    }

    private static int GetWorkerCount(uint requestedWorkerCount)
    {
        return requestedWorkerCount switch
        {
            0              => Math.Max(1, Environment.ProcessorCount),
            > int.MaxValue => throw new ArgumentOutOfRangeException(nameof(PatchOptions.ParallelThreads)),
            _              => (int)requestedWorkerCount
        };
    }

    private void RunSequential(ReadOnlySpan<RleCoverInfo> covers,
                               PatchMetadata              patchMetadata,
                               int                        bufferSize,
                               CancellationToken          token)
    {
        using NativeMemoryBuffer<byte> oldBuffer = new(bufferSize);
        ProduceWork(covers,
                    patchMetadata.DiffNewSize,
                    bufferSize,
                    item => ProcessWorkItem(item, oldBuffer, token),
                    token);
        ValidateDataReaders(patchMetadata);
    }

    private void RunParallel(NativeMemoryBuffer<RleCoverInfo> covers,
                             PatchMetadata                    patchMetadata,
                             int                              bufferSize,
                             int                              workerCount,
                             CancellationToken                token)
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
                    ProduceWork(covers.Span,
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
                () => new NativeMemoryBuffer<byte>(bufferSize),
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
                oldBuffer => oldBuffer.Dispose());
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
                abandonedItem.Buffer.Dispose();
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
            item.Buffer.Dispose();
            throw;
        }
    }

    private void ProduceWork(ReadOnlySpan<RleCoverInfo> covers,
                             long                       newDataSize,
                             int                        bufferSize,
                             Action<PatchWorkItem>      emit,
                             CancellationToken          token)
    {
        RleDecoder                     rleDecoder  = new(_rleCtrlReader, _rleCodeReader);
        using NativeMemoryBuffer<byte> skipBuffer  = new(Math.Min(bufferSize, 64 << 10));
        using PatchWorkBuilder         workBuilder = new(bufferSize, emit);

        long newPosition = 0;
        for (int coverIndex = 0; coverIndex < covers.Length; coverIndex++)
        {
            token.ThrowIfCancellationRequested();
            ref readonly RleCoverInfo cover = ref covers[coverIndex];
            ValidateCover(cover, newPosition, newDataSize, InputStream!.Length);

            ProduceNewData(cover.CopyLength,
                           ref newPosition,
                           rleDecoder,
                           skipBuffer.Span,
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
                       skipBuffer.Span,
                       workBuilder,
                       token);

        if (newPosition != newDataSize || rleDecoder.HasPendingData)
        {
            throw new InvalidDataException("The decoded patch data does not match the declared output size.");
        }

        workBuilder.Flush();
    }

    private void ProduceNewData(long              length,
                                ref long          newPosition,
                                RleDecoder        rleDecoder,
                                Span<byte>        skipBuffer,
                                PatchWorkBuilder  workBuilder,
                                CancellationToken token)
    {
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> destination = workBuilder
                .GetWritableSpan(newPosition,
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

    private void ProcessWorkItem(PatchWorkItem            item,
                                 NativeMemoryBuffer<byte> oldBuffer,
                                 CancellationToken        token)
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
                    Span<byte> oldData = oldBuffer.Span[..segment.Length];
                    int read = InputStream!.Read(oldData, segment.OldPosition);
                    if (read != segment.Length)
                    {
                        throw new EndOfStreamException("The old-data stream ended while processing a cover.");
                    }

#if NET6_0_OR_GREATER
                    Span<byte> rleData = item.Buffer.Span.Slice(segment.BufferOffset, segment.Length);
                    AddRle(rleData, oldData, Options.UseSIMD);
#else
                    Span<byte> rleData = item.Buffer.Span.Slice(segment.BufferOffset, segment.Length);
                    AddRle(rleData, oldData);
#endif
                }
            }

            OutputStream!.Write(item.Buffer.Span[..item.Length], item.OutputPosition);
            AdvanceProgress(item.Length);
        }
        finally
        {
            item.Buffer.Dispose();
        }
    }

#if NET6_0_OR_GREATER
    private static void AddRle(Span<byte>         destination,
                               ReadOnlySpan<byte> addend,
                               bool               useSimd)
#else
    private static void AddRle(Span<byte>         destination,
                               ReadOnlySpan<byte> addend)
#endif
    {
        if (destination.Length != addend.Length)
        {
            throw new ArgumentException("The old-data and RLE buffers must have the same length.");
        }

        int length = destination.Length;
        int index = 0;
#if NET6_0_OR_GREATER
        if (useSimd && Avx2.IsSupported)
        {
            const int vectorLength = 32;
            int vectorEnd = length - length % vectorLength;
            if (vectorEnd != 0)
            {
                ref byte destinationRef = ref destination[0];
                ref byte addendRef      = ref Unsafe.AsRef(in addend[0]);
                for (; index < vectorEnd; index += vectorLength)
                {
                    var destinationVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref destinationRef, index));
                    var addendVector      = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref addendRef, index));
                    Vector256<byte> result = Avx2.Add(destinationVector, addendVector);
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
                ref byte addendRef      = ref Unsafe.AsRef(in addend[0]);
                for (; index < vectorEnd; index += vectorLength)
                {
                    var destinationVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref destinationRef, index));
                    var addendVector      = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref addendRef, index));
                    Vector128<byte> result = Sse2.Add(destinationVector, addendVector);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationRef, index), result);
                }
            }
        }
        else if (useSimd && Vector.IsHardwareAccelerated)
        {
            int vectorLength = Vector<byte>.Count;
            int vectorEnd    = length - length % vectorLength;
            if (vectorEnd != 0)
            {
                ref byte destinationRef = ref destination[0];
                ref byte addendRef      = ref Unsafe.AsRef(in addend[0]);
                for (; index < vectorEnd; index += vectorLength)
                {
                    var destinationVector = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref destinationRef, index));
                    var addendVector      = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref addendRef, index));
                    Vector<byte> result = destinationVector + addendVector;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationRef, index), result);
                }
            }
        }
#endif

        while (index < length)
        {
            destination[index] = unchecked((byte)(destination[index] + addend[index++]));
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

    private readonly struct PatchWorkItem
    {
        internal PatchWorkItem(NativeMemoryBuffer<byte> buffer,
                               int                      length,
                               long                     outputPosition,
                               List<CoverSegment>?      coverSegments)
        {
            Buffer         = buffer;
            Length         = length;
            OutputPosition = outputPosition;
            CoverSegments  = coverSegments;
        }

        public readonly NativeMemoryBuffer<byte> Buffer;
        public readonly int                      Length;
        public readonly long                     OutputPosition;
        public readonly List<CoverSegment>?      CoverSegments;
    }

    private readonly struct CoverSegment
    {
        internal CoverSegment(int  bufferOffset,
                              int  length,
                              long oldPosition)
        {
            BufferOffset = bufferOffset;
            Length       = length;
            OldPosition  = oldPosition;
        }

        public readonly int  BufferOffset;
        public readonly int  Length;
        public readonly long OldPosition;
    }

    private sealed class PatchWorkBuilder : IDisposable
    {
        private          NativeMemoryBuffer<byte>? _buffer;
        private          int                       _length;
        private          long                      _outputPosition;
        private          List<CoverSegment>?       _coverSegments;
        private readonly int                       _bufferSize;
        private readonly Action<PatchWorkItem>     _emit;

        internal PatchWorkBuilder(int                   bufferSize,
                                  Action<PatchWorkItem> emit)
        {
            _bufferSize = bufferSize;
            _emit       = emit;
        }

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
                _buffer         = new NativeMemoryBuffer<byte>(_bufferSize);
                _outputPosition = outputPosition;
            }
            else if (outputPosition != _outputPosition + _length)
            {
                throw new InvalidOperationException("Patch batches must contain contiguous output ranges.");
            }

            writableLength = (int)Math.Min(requestedLength, _bufferSize - _length);
            return _buffer.Span.Slice(_length, writableLength);
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
            if (_buffer is null) return;

            _buffer.Dispose();
            _buffer = null;
        }
    }

    private sealed class RleDecoder
    {
        private long _remaining;
        private byte _type;
        private byte _value;

        private readonly BittableStreamReader _controlReader;
        private readonly BittableStreamReader _codeReader;

        internal RleDecoder(BittableStreamReader controlReader,
                            BittableStreamReader codeReader)
        {
            _controlReader = controlReader;
            _codeReader    = codeReader;
        }

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

        public void Skip(long length, Span<byte> scratchBuffer, CancellationToken token)
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
                        _codeReader.ReadBytes(scratchBuffer[..readLength]);
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
