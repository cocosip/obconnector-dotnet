using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>Single-owner OB20 transport underneath the standard MySQL packet channel.</summary>
    internal sealed class Ob20Stream : Stream
    {
        private readonly Stream _inner;
        private readonly uint _connectionId;
        private readonly byte[] _writeBuffer = new byte[64 * 1024];
        private readonly byte[] _header = new byte[Ob20Packet.HeaderLength];
        private readonly byte[] _scratch = new byte[8192];
        private uint _nextRequestId;
        private uint _requestId;
        private byte _sequence;
        private int _writeCount;
        private int _remaining;
        private uint _crc;
        private int _frames;
        private bool _last;
        private bool _readStarted;
        private bool _writing;
        private bool _pending;
        private bool _closed;

        public Ob20Stream(Stream inner, uint connectionId)
        { _inner = inner ?? throw new ArgumentNullException(nameof(inner)); _connectionId = connectionId; }

        public void BeginRequest()
        {
            EnsureOpen();
            if (_pending) throw new InvalidOperationException("The previous OB20 response has not ended.");
            _requestId = _nextRequestId; _nextRequestId = (_nextRequestId + 1) & 0xffffff;
            _sequence = 0; _frames = 0; _remaining = 0; _writeCount = 0;
            _readStarted = _last = false; _pending = _writing = true;
        }

        public void FinishRequest()
        {
            try { EnsureWriting(); WriteFrame(true); _writing = false; }
            catch { Abort(); throw; }
        }

        public async Task FinishRequestAsync(CancellationToken token)
        {
            try { EnsureWriting(); token.ThrowIfCancellationRequested(); await WriteFrameAsync(true, token).ConfigureAwait(false); _writing = false; }
            catch { Abort(); throw; }
        }

        public void CompleteResponse(bool expectsResponse = true)
        {
            EnsureOpen();
            if (!_pending) return;
            try
            {
                if (_writing || (expectsResponse ? !_readStarted || !_last || _remaining != 0 : _readStarted))
                    throw new ProtocolException("The OB20 response did not end at the command boundary.");
                _pending = false;
            }
            catch { Abort(); throw; }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count); EnsureWriting();
            try
            {
                while (count > 0)
                {
                    if (_writeCount == _writeBuffer.Length) WriteFrame(false);
                    var size = Math.Min(count, _writeBuffer.Length - _writeCount);
                    Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeCount, size);
                    _writeCount += size; offset += size; count -= size;
                }
            }
            catch { Abort(); throw; }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            Validate(buffer, offset, count); EnsureWriting();
            try
            {
                token.ThrowIfCancellationRequested();
                while (count > 0)
                {
                    if (_writeCount == _writeBuffer.Length) await WriteFrameAsync(false, token).ConfigureAwait(false);
                    var size = Math.Min(count, _writeBuffer.Length - _writeCount);
                    Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeCount, size);
                    _writeCount += size; offset += size; count -= size;
                }
            }
            catch { Abort(); throw; }
        }

        private void PrepareWriteFrame(bool last)
        {
            Ob20Packet.WriteHeader(_header, _connectionId, _requestId, _sequence, _writeCount, last ? 2u : 0u, true);
            _sequence = (byte)((_sequence + 1) & 255);
            var crc = Ob20Checksum.Payload(0, _writeBuffer, 0, _writeCount);
            for (var i = 0; i < 4; i++) _scratch[i] = (byte)(crc >> (8 * i));
        }

        private void WriteFrame(bool last)
        {
            PrepareWriteFrame(last);
            _inner.Write(_header, 0, _header.Length); _inner.Write(_writeBuffer, 0, _writeCount); _inner.Write(_scratch, 0, 4);
            _writeCount = 0;
        }

        private async Task WriteFrameAsync(bool last, CancellationToken token)
        {
            PrepareWriteFrame(last);
            await _inner.WriteAsync(_header, 0, _header.Length, token).ConfigureAwait(false);
            await _inner.WriteAsync(_writeBuffer, 0, _writeCount, token).ConfigureAwait(false);
            await _inner.WriteAsync(_scratch, 0, 4, token).ConfigureAwait(false); _writeCount = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count); EnsureOpen();
            if (count == 0) return 0;
            try
            {
                while (_remaining == 0) ReadFrame();
                var size = Math.Min(count, _remaining);
                ReadWireExactly(buffer, offset, size); Consume(buffer, offset, size);
                if (_remaining == 0) ReadTail();
                return size;
            }
            catch { Abort(); throw; }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            Validate(buffer, offset, count); EnsureOpen();
            try
            {
                token.ThrowIfCancellationRequested(); if (count == 0) return 0;
                while (_remaining == 0) await ReadFrameAsync(token).ConfigureAwait(false);
                var size = Math.Min(count, _remaining);
                await ReadWireExactlyAsync(buffer, offset, size, token).ConfigureAwait(false); Consume(buffer, offset, size);
                if (_remaining == 0) await ReadTailAsync(token).ConfigureAwait(false);
                return size;
            }
            catch { Abort(); throw; }
        }

        private void BeforeReadFrame()
        {
            if (!_pending || _writing || _last) throw new ProtocolException("Unexpected data past the OB20 response boundary.");
            if (++_frames > 1_000_000) throw new ProtocolException("OB20 response frame limit exceeded.");
        }

        private bool TakeHeader()
        {
            var header = Ob20Packet.ParseHeader(_header, _connectionId, _requestId, _sequence);
            if ((header.Flags & ~11u) != 0) throw new ProtocolException("Unsupported OB20 response flags, including proxy reroute.");
            _sequence = (byte)((_sequence + 1) & 255);
            _remaining = header.PayloadLength; _crc = 0; _readStarted = true;
            _last = (header.Flags & Ob20Packet.LastPacketFlag) != 0;
            return (header.Flags & Ob20Packet.ExtraInfoFlag) != 0;
        }

        private int ExtraLength()
        {
            Consume(_scratch, 0, 4);
            var length = new ByteCursor(_scratch, 0, 4).ReadUInt32();
            if (length > (uint)_remaining) throw new ProtocolException("OB20 extra info exceeds the frame payload.");
            return (int)length;
        }

        private void ReadFrame()
        {
            BeforeReadFrame(); ReadWireExactly(_header, 0, _header.Length);
            if (TakeHeader())
            {
                if (_remaining < 4) throw new ProtocolException("Truncated OB20 extra info length.");
                ReadWireExactly(_scratch, 0, 4); var extra = ExtraLength();
                while (extra > 0)
                {
                    var size = Math.Min(extra, _scratch.Length); ReadWireExactly(_scratch, 0, size);
                    Consume(_scratch, 0, size); extra -= size;
                }
            }
            if (_remaining == 0) ReadTail();
        }

        private async Task ReadFrameAsync(CancellationToken token)
        {
            BeforeReadFrame(); await ReadWireExactlyAsync(_header, 0, _header.Length, token).ConfigureAwait(false);
            if (TakeHeader())
            {
                if (_remaining < 4) throw new ProtocolException("Truncated OB20 extra info length.");
                await ReadWireExactlyAsync(_scratch, 0, 4, token).ConfigureAwait(false); var extra = ExtraLength();
                while (extra > 0)
                {
                    var size = Math.Min(extra, _scratch.Length);
                    await ReadWireExactlyAsync(_scratch, 0, size, token).ConfigureAwait(false);
                    Consume(_scratch, 0, size); extra -= size;
                }
            }
            if (_remaining == 0) await ReadTailAsync(token).ConfigureAwait(false);
        }

        private void Consume(byte[] buffer, int offset, int size)
        { _crc = Ob20Checksum.Payload(_crc, buffer, offset, size); _remaining -= size; }
        private void CheckTail()
        {
            var crc = new ByteCursor(_scratch, 0, 4).ReadUInt32();
            if (crc != 0 && crc != _crc) throw new ProtocolException("OB20 payload checksum mismatch.");
        }
        private void ReadTail() { ReadWireExactly(_scratch, 0, 4); CheckTail(); }
        private async Task ReadTailAsync(CancellationToken token)
        { await ReadWireExactlyAsync(_scratch, 0, 4, token).ConfigureAwait(false); CheckTail(); }

        private void ReadWireExactly(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var size = _inner.Read(buffer, offset, count);
                if (size <= 0) throw new ProtocolException("Connection closed inside an OB20 frame.");
                offset += size; count -= size;
            }
        }
        private async Task ReadWireExactlyAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            while (count > 0)
            {
                var size = await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
                if (size <= 0) throw new ProtocolException("Connection closed inside an OB20 frame.");
                offset += size; count -= size;
            }
        }

        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        }
        private void EnsureOpen() { if (_closed) throw new ObjectDisposedException(nameof(Ob20Stream)); }
        private void EnsureWriting() { EnsureOpen(); if (!_writing) throw new InvalidOperationException("No OB20 request is being written."); }
        private void Abort() { try { Dispose(); } catch { } }
        protected override void Dispose(bool disposing)
        { if (disposing && !_closed) { _closed = true; _inner.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => !_closed && _inner.CanRead;
        public override bool CanWrite => !_closed && _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { EnsureOpen(); _inner.Flush(); }
        public override Task FlushAsync(CancellationToken cancellationToken) { EnsureOpen(); return _inner.FlushAsync(cancellationToken); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
