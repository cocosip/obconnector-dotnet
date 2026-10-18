using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.UnitTests.Protocol
{
    /// <summary>
    /// MemoryStream wrapper that records how many synchronous and
    /// asynchronous reads/writes were issued. Async-only tests assert the
    /// synchronous counters stayed at zero, proving the async packet path
    /// never falls back to blocking I/O (development plan A03).
    /// </summary>
    public sealed class SpyStream : Stream
    {
        private readonly MemoryStream _inner;

        public SpyStream(byte[] content)
        {
            _inner = new MemoryStream(content);
        }

        public SpyStream()
        {
            _inner = new MemoryStream();
        }

        public int SyncReads { get; private set; }

        public int SyncWrites { get; private set; }

        public int AsyncReads { get; private set; }

        public int AsyncWrites { get; private set; }

        public byte[] ToArray() => _inner.ToArray();

        public void ResetCounters()
        {
            SyncReads = 0;
            SyncWrites = 0;
            AsyncReads = 0;
            AsyncWrites = 0;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SyncReads++;
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncReads++;
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            SyncWrites++;
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncWrites++;
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
