using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class PacketChannel
    {
        private PacketPayload? _activePayload;
        private bool _closed;

        // One logical packet owns the channel until its last frame has been consumed.
        internal PacketPayload OpenPayload()
        {
            EnsureIdle();
            var payload = _activePayload = new PacketPayload(this);
            try { payload.StartFrame(); return payload; }
            catch { AbortPayload(); throw; }
        }

        internal async Task<PacketPayload> OpenPayloadAsync(CancellationToken token = default)
        {
            EnsureIdle();
            token.ThrowIfCancellationRequested();
            var payload = _activePayload = new PacketPayload(this);
            try { await payload.StartFrameAsync(token).ConfigureAwait(false); return payload; }
            catch { AbortPayload(); throw; }
        }

        private void EnsureIdle()
        {
            if (_closed) throw new ObjectDisposedException(nameof(PacketChannel));
            if (_activePayload != null) throw new InvalidOperationException("The current packet payload must be consumed before reusing the channel.");
        }

        private void AbortPayload()
        {
            // Preserve the framing/cancellation error if closing the failed transport also throws.
            try { Close(); }
            catch { }
        }

        internal sealed class PacketPayload : Stream
        {
            private readonly PacketChannel _channel;
            private long _size;
            private int _remaining;
            private bool _continues;
            private bool _complete;
            private bool _disposed;

            internal PacketPayload(PacketChannel channel) => _channel = channel;
            internal int FirstFrameLength { get; private set; }
            internal bool IsComplete => _complete;
            internal long MaximumLength => FirstFrameLength < MaxFramePayload ? FirstFrameLength : _channel.MaxPacketSize;

            internal byte[] ReadBuffered()
            {
                byte[]? result = null;
                long length = 0;
                while (!_complete)
                {
                    var chunk = new byte[_remaining];
                    if (Read(chunk, 0, chunk.Length) != chunk.Length) throw new ProtocolException("Incomplete buffered packet frame.");
                    result = AppendChunk(result, chunk, ref length);
                    if (!_complete) StartFrame();
                }
                return result ?? Array.Empty<byte>();
            }

            internal async Task<byte[]> ReadBufferedAsync(CancellationToken token)
            {
                byte[]? result = null;
                long length = 0;
                while (!_complete)
                {
                    var chunk = new byte[_remaining];
                    if (await ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false) != chunk.Length)
                        throw new ProtocolException("Incomplete buffered packet frame.");
                    result = AppendChunk(result, chunk, ref length);
                    if (!_complete) await StartFrameAsync(token).ConfigureAwait(false);
                }
                return result ?? Array.Empty<byte>();
            }

            public override bool CanRead => !_disposed && (_complete || !_channel._closed);
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            internal void StartFrame()
            {
                _channel.ReadExactly(_channel._headerBuffer, 0, 4, "packet header");
                TakeFrame();
            }

            internal async Task StartFrameAsync(CancellationToken token)
            {
                await _channel.ReadExactlyAsync(_channel._headerBuffer, 0, 4, token).ConfigureAwait(false);
                TakeFrame();
            }

            private void TakeFrame()
            {
                _channel.TakeHeader(out _remaining, out _continues);
                if (_size == 0) FirstFrameLength = _remaining;
                _size = checked(_size + _remaining);
                _channel.EnforcePacketCap(_size);
                CompleteIfConsumed();
            }

            private void CompleteIfConsumed()
            {
                if (_remaining != 0 || _continues) return;
                _complete = true;
                _channel._activePayload = null;
            }

            private void ValidateRead(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                ValidateRange(buffer, offset, count);
                if (_disposed || !_complete && _channel._closed) throw new ObjectDisposedException(nameof(PacketPayload));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ValidateRead(buffer, offset, count);
                if (count == 0 || _complete) return 0;
                try
                {
                    if (_remaining == 0) StartFrame();
                    var size = Math.Min(count, _remaining);
                    _channel.ReadExactly(buffer, offset, size, "packet payload");
                    _remaining -= size;
                    CompleteIfConsumed();
                    return size;
                }
                catch { _channel.AbortPayload(); throw; }
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                ValidateRead(buffer, offset, count);
                if (count == 0 || _complete) { token.ThrowIfCancellationRequested(); return 0; }
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_remaining == 0) await StartFrameAsync(token).ConfigureAwait(false);
                    var size = Math.Min(count, _remaining);
                    await _channel.ReadExactlyAsync(buffer, offset, size, token).ConfigureAwait(false);
                    _remaining -= size;
                    CompleteIfConsumed();
                    return size;
                }
                catch { _channel.AbortPayload(); throw; }
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing && !_complete) _channel.AbortPayload();
                _disposed = true;
                base.Dispose(disposing);
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
