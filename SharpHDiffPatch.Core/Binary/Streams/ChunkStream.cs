using System;
using System.IO;

namespace SharpHDiffPatch.Core.Binary.Streams
{
    public sealed class ChunkStream : Stream
    {
        private long Start { get; }
        private long End { get; }
        private long Size => End - Start;
        private long CurPos { get; set; }
        private long Remain => Size - CurPos;
        private readonly Stream _stream;
        private bool IsDisposing { get; }

        public ChunkStream(Stream stream, long start, long end, bool isDisposing = false)
            : base()
        {
            _stream = stream;

            if (_stream.Length == 0)
            {
                throw new Exception("The stream must not have 0 bytes!");
            }

            if (_stream.Length < start || end > _stream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(stream), "Offset is out of stream size range!");
            }

            _stream.Position = start;
            Start = start;
            End = end;
            CurPos = 0;
            IsDisposing = isDisposing;
        }

        ~ChunkStream() => Dispose(IsDisposing);

#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        public override int Read(Span<byte> buffer)
        {
            if (Remain == 0) return 0;

            int toSlice = (int)(buffer.Length > Remain ? Remain : buffer.Length);
            _stream.Position = Start + CurPos;
            int read = _stream.Read(buffer[..toSlice]);
            CurPos += read;

            return read;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Remain == 0) return;

            int toSlice = (int)(buffer.Length > Remain ? Remain : buffer.Length);
            CurPos += toSlice;

            _stream.Write(buffer[..toSlice]);
        }
#endif

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Remain == 0) return 0;

            int toRead = (int)(Remain < count ? Remain : count);
            _stream.Position = Start + CurPos;
            int read = _stream.Read(buffer, offset, toRead);
            CurPos += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int toRead = (int)(Remain < count ? Remain : count);
            int toOffset = offset > Remain ? 0 : offset;
            _stream.Position += toOffset;
            CurPos += toOffset + toRead;

            _stream.Write(buffer, offset, toRead);
        }

#if !(NETSTANDARD2_0 || NET461_OR_GREATER)
        public override void CopyTo(Stream destination, int bufferSize)
        {
            throw new NotSupportedException();
        }
#endif

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override void Flush()
        {
            _stream.Flush();
        }

        public override long Length => Size;

        public override long Position
        {
            get => CurPos;
            set
            {
                if (value > Size)
                {
                    throw new IndexOutOfRangeException();
                }

                CurPos = value;
                _stream.Position = CurPos + Start;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    {
                        if (offset > Size)
                        {
                            throw new ArgumentOutOfRangeException(nameof(offset));
                        }
                        return _stream.Seek(offset + Start, SeekOrigin.Begin) - Start;
                    }
                case SeekOrigin.Current:
                    {
                        long pos = _stream.Position - Start;
                        if (pos + offset > Size)
                        {
                            throw new ArgumentOutOfRangeException(nameof(offset));
                        }
                        return _stream.Seek(offset, SeekOrigin.Current) - Start;
                    }
                case SeekOrigin.End:
                default:
                    {
                        _stream.Position = End;
                        _stream.Position -= offset;

                        return Position;
                    }
            }
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) base.Dispose(true);
            if (IsDisposing) _stream.Dispose();
        }
    }
}