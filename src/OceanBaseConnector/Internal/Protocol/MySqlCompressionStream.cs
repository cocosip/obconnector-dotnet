using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>Bounded compressed blocks wrapping a stream of ordinary MySQL frames.</summary>
    internal sealed class MySqlCompressionStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _header = new byte[7];
        private readonly byte[] _writeBuffer = new byte[64 * 1024];
        private byte[] _readBuffer = Array.Empty<byte>();
        private int _readPosition, _writeCount;
        private byte _sequence;
        private bool _disposed;
        private readonly ProtocolDiagnostics _diagnostics;
        private int _failureReported;

        internal MySqlCompressionStream(Stream inner, ProtocolDiagnostics? diagnostics = null)
        { _inner = inner; _diagnostics = diagnostics ?? new ProtocolDiagnostics(); }

        internal void BeginCommand() { CompleteResponse(); _sequence = 0; }
        internal void CompleteResponse()
        {
            EnsureAvailable();
            if (_readPosition != _readBuffer.Length || _writeCount != 0)
            {
                Poison();
                throw new ProtocolException("Unread compressed bytes or pending writes at an exchange boundary.");
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count); EnsureAvailable();
            if (count == 0) return 0;
            try
            {
                if (_readPosition == _readBuffer.Length)
                {
                    ReadExactly(_header, _header.Length);
                    var body = TakeHeader(out var originalLength);
                    ReadExactly(body, body.Length); Install(body, originalLength);
                }
                return CopyRead(buffer, offset, count);
            }
            catch (Exception error) { Poison(error, "read"); throw; }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Validate(buffer, offset, count); EnsureAvailable();
            if (count == 0) return 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_readPosition == _readBuffer.Length)
                {
                    await ReadExactlyAsync(_header, _header.Length, cancellationToken).ConfigureAwait(false);
                    var body = TakeHeader(out var originalLength);
                    await ReadExactlyAsync(body, body.Length, cancellationToken).ConfigureAwait(false); Install(body, originalLength);
                }
                return CopyRead(buffer, offset, count);
            }
            catch (Exception error) { Poison(error, "read"); throw; }
        }

        private byte[] TakeHeader(out int originalLength)
        {
            _diagnostics.Frame("compressed", "read", LengthAt(0), _header[3], _sequence, LengthAt(4));
            if (_header[3] != _sequence++) throw new ProtocolException("Compressed packet sequence mismatch.");
            var length = LengthAt(0); originalLength = LengthAt(4);
            if (length == 0) throw new ProtocolException("Empty compressed block.");
            return new byte[length];
        }
        private int LengthAt(int offset) => _header[offset] | _header[offset + 1] << 8 | _header[offset + 2] << 16;
        private void Install(byte[] body, int originalLength)
        { _readBuffer = originalLength == 0 ? body : ZlibBlock.Decompress(body, originalLength); _readPosition = 0; }
        private int CopyRead(byte[] buffer, int offset, int count)
        {
            var size = Math.Min(count, _readBuffer.Length - _readPosition);
            Buffer.BlockCopy(_readBuffer, _readPosition, buffer, offset, size); _readPosition += size;
            return size;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count); EnsureAvailable();
            try
            {
                while (count > 0)
                {
                    var size = CopyWrite(buffer, offset, count); offset += size; count -= size;
                    if (_writeCount == _writeBuffer.Length) WriteBlock();
                }
            }
            catch (Exception error) { Poison(error, "write"); throw; }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Validate(buffer, offset, count); EnsureAvailable();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (count > 0)
                {
                    var size = CopyWrite(buffer, offset, count); offset += size; count -= size;
                    if (_writeCount == _writeBuffer.Length) await WriteBlockAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) { Poison(error, "write"); throw; }
        }

        private int CopyWrite(byte[] buffer, int offset, int count)
        {
            var size = Math.Min(count, _writeBuffer.Length - _writeCount);
            Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeCount, size); _writeCount += size;
            return size;
        }
        private ArraySegment<byte> PrepareBlock()
        {
            var body = ZlibBlock.Compress(_writeBuffer, _writeCount);
            SetLength(0, body.Count); _header[3] = _sequence++;
            SetLength(4, ReferenceEquals(body.Array, _writeBuffer) ? 0 : _writeCount);
            return body;
        }
        private void SetLength(int offset, int length)
        { _header[offset] = (byte)length; _header[offset + 1] = (byte)(length >> 8); _header[offset + 2] = (byte)(length >> 16); }
        private void WriteBlock()
        {
            var body = PrepareBlock(); _inner.Write(_header, 0, 7); _inner.Write(body.Array!, body.Offset, body.Count); _writeCount = 0;
            _diagnostics.Frame("compressed", "write", body.Count, _header[3], _header[3], LengthAt(4));
        }
        private async Task WriteBlockAsync(CancellationToken token)
        {
            var body = PrepareBlock();
            await _inner.WriteAsync(_header, 0, 7, token).ConfigureAwait(false);
            await _inner.WriteAsync(body.Array!, body.Offset, body.Count, token).ConfigureAwait(false); _writeCount = 0;
            _diagnostics.Frame("compressed", "write", body.Count, _header[3], _header[3], LengthAt(4));
        }

        public override void Flush()
        {
            EnsureAvailable();
            try { if (_writeCount != 0) WriteBlock(); _inner.Flush(); }
            catch (Exception error) { Poison(error, "write"); throw; }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            EnsureAvailable();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_writeCount != 0) await WriteBlockAsync(cancellationToken).ConfigureAwait(false);
                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) { Poison(error, "write"); throw; }
        }
        private void ReadExactly(byte[] bytes, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = _inner.Read(bytes, offset, count - offset);
                if (read == 0) throw new ProtocolException("Connection closed inside a compressed block.");
                offset += read;
            }
        }
        private async Task ReadExactlyAsync(byte[] bytes, int count, CancellationToken token)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await _inner.ReadAsync(bytes, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) throw new ProtocolException("Connection closed inside a compressed block.");
                offset += read;
            }
        }
        private void EnsureAvailable() { if (_disposed) throw new ObjectDisposedException(nameof(MySqlCompressionStream)); }
        private void Poison(Exception? error = null, string direction = "boundary")
        {
            if (Interlocked.Exchange(ref _failureReported, 1) == 0) _diagnostics.Error("compressed", direction, error);
            try { Dispose(); } catch { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            { _disposed = true; _readBuffer = Array.Empty<byte>(); _inner.Dispose(); }
            base.Dispose(disposing);
        }
        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        }
        public override bool CanRead => !_disposed;
        public override bool CanWrite => !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
