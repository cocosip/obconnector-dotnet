using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Reads and writes MySQL-style framed packets over a stream: a 3-byte
    /// little-endian payload length plus a 1-byte sequence id per frame.
    /// Logical payloads larger than <see cref="MaxFramePayload"/> are split
    /// into continuation frames; a payload that is an exact multiple of the
    /// frame maximum is terminated by an empty frame. Sequence ids start at 0
    /// for each command exchange and increment per frame, wrapping modulo 256.
    /// The logical packet size is capped before any buffered accumulation, so
    /// a hostile length field can never drive an unbounded allocation.
    /// The channel is single-owner by design: one physical session, one
    /// exclusive command at a time (see the development plan, A06).
    /// </summary>
    internal sealed partial class PacketChannel
    {
        public const int MaxFramePayload = 0xFFFFFF;
        public const long DefaultMaxPacketSize = 64 * 1024 * 1024;

        private readonly long _maxPacketSize;
        private readonly byte[] _headerBuffer = new byte[4];

        private Stream _stream;
        private int _sequence;
        private bool _frameWriteStarted;
        private Ob20Stream? _ob20;
        private MySqlCompressionStream? _compression;
        private ActivityContext _traceContext;
        private readonly ProtocolDiagnostics _diagnostics = new ProtocolDiagnostics();

        public PacketChannel(Stream stream, long maxPacketSize = DefaultMaxPacketSize)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (maxPacketSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPacketSize), "Maximum packet size must not be negative.");
            }

            _stream = stream;
            _maxPacketSize = maxPacketSize;
        }

        public long MaxPacketSize => _maxPacketSize;

        public void Close() { _closed = true; _stream.Dispose(); }

        internal void EnableCompression()
        {
            EnsureIdle();
            if (_compression != null || _ob20 != null) throw new InvalidOperationException("A framed transport is already enabled.");
            _stream = _compression = new MySqlCompressionStream(_stream, _diagnostics);
        }

        internal void EnableOb20(uint connectionId, bool fullLinkTrace = false, bool modernExtra = false)
        {
            EnsureIdle();
            if (_ob20 != null || _compression != null) throw new InvalidOperationException("A framed transport is already enabled.");
            _stream = _ob20 = new Ob20Stream(_stream, connectionId, fullLinkTrace ? new Ob20TraceContext(modernExtra) : null, _diagnostics);
        }

        internal bool FullLinkTraceEnabled => _ob20?.FullLinkTraceEnabled == true;
        internal void ReportNegotiated(bool compressionRequested, bool ob20Requested, bool traceRequested)
            => _diagnostics.Negotiated(_compression != null ? "compressed" : _ob20 != null ? "ob20" : "mysql",
                compressionRequested ? "compressed" : ob20Requested ? "ob20" : "mysql", traceRequested,
                FullLinkTraceEnabled, _ob20?.ModernTraceFormat == true);
        internal void SetTraceContext(ActivityContext context) => _traceContext = context;
        private void BeginOb20Request()
        { _ob20?.BeginRequest(_traceContext); _traceContext = default; }

        internal void CompleteResponse(bool expectsResponse = true)
        {
            try
            {
                EnsureIdle(); _ob20?.CompleteResponse(expectsResponse); _compression?.CompleteResponse();
                _diagnostics.Boundary("complete");
            }
            catch (Exception error) { _diagnostics.Error("mysql", "boundary", error); throw; }
        }

        internal void ValidateReceivedResponseBoundary()
        {
            // Cancellation can close a fully received bare packet before its ERR is parsed.
            if (_closed && _activePayload == null && _ob20 == null && _compression == null) return;
            CompleteResponse();
        }

        /// <summary>
        /// Swaps the underlying stream after a TLS upgrade. Sequence counters
        /// are preserved: the TLS handshake is invisible to the packet
        /// framing, which continues exactly where it left off.
        /// </summary>
        public void ReplaceUnderlyingStream(Stream stream)
        {
            EnsureIdle();
            if (_ob20 != null || _compression != null) throw new InvalidOperationException("The transport cannot change after framing negotiation.");
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            _stream = stream;
        }

        /// <summary>
        /// Starts a new command exchange: the client command is frame 0 and
        /// the server response continues after the last command frame.
        /// </summary>
        public void BeginCommand()
        {
            EnsureIdle();
            _compression?.BeginCommand();
            _sequence = 0;
            _frameWriteStarted = false;
            _diagnostics.Boundary("begin");
        }

        internal void BeginResponse()
        {
            EnsureIdle();
            _sequence = 1;
        }

        internal bool HasStartedFrameWrite => _frameWriteStarted;

        public byte[] ReadPacket()
        {
            using var payload = (PacketPayload)OpenPayload();
            return payload.ReadBuffered();
        }

        public async Task<byte[]> ReadPacketAsync(CancellationToken cancellationToken = default)
        {
            using var payload = (PacketPayload)await OpenPayloadAsync(cancellationToken).ConfigureAwait(false);
            return await payload.ReadBufferedAsync(cancellationToken).ConfigureAwait(false);
        }

        public void WritePacket(byte[] payload)
        {
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            WritePacket(payload, 0, payload.Length);
        }

        public void WritePacket(byte[] payload, int offset, int count)
        {
            EnsureIdle();
            ValidateRange(payload, offset, count);

            BeginOb20Request();

            var position = offset;
            var remaining = count;
            while (true)
            {
                var chunk = (int)Math.Min((long)remaining, MaxFramePayload);
                WriteFrame(payload, position, chunk);
                position += chunk;
                remaining -= chunk;

                if (chunk < MaxFramePayload)
                {
                    _ob20?.FinishRequest();
                    _compression?.Flush();
                    return;
                }

                if (remaining == 0)
                {
                    // Exact multiple of the frame maximum: empty terminator frame.
                    WriteFrame(payload, position, 0);
                    _ob20?.FinishRequest();
                    _compression?.Flush();
                    return;
                }
            }
        }

        public Task WritePacketAsync(byte[] payload, CancellationToken cancellationToken = default)
        {
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return WritePacketAsync(payload, 0, payload.Length, cancellationToken);
        }

        public async Task WritePacketAsync(byte[] payload, int offset, int count, CancellationToken cancellationToken = default)
        {
            EnsureIdle();
            ValidateRange(payload, offset, count);

            BeginOb20Request();

            var position = offset;
            var remaining = count;
            while (true)
            {
                var chunk = (int)Math.Min((long)remaining, MaxFramePayload);
                await WriteFrameAsync(payload, position, chunk, cancellationToken).ConfigureAwait(false);
                position += chunk;
                remaining -= chunk;

                if (chunk < MaxFramePayload)
                {
                    if (_ob20 != null) await _ob20.FinishRequestAsync(cancellationToken).ConfigureAwait(false);
                    if (_compression != null) await _compression.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (remaining == 0)
                {
                    // Exact multiple of the frame maximum: empty terminator frame.
                    await WriteFrameAsync(payload, position, 0, cancellationToken).ConfigureAwait(false);
                    if (_ob20 != null) await _ob20.FinishRequestAsync(cancellationToken).ConfigureAwait(false);
                    if (_compression != null) await _compression.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }

        /// <summary>
        /// Decodes the buffered 4-byte header, validating the sequence id. A
        /// frame of exactly <see cref="MaxFramePayload"/> bytes continues the
        /// chain; any shorter frame terminates it.
        /// </summary>
        private void TakeHeader(out int frameLength, out bool isContinuation)
        {
            frameLength = (int)((uint)_headerBuffer[0] |
                ((uint)_headerBuffer[1] << 8) |
                ((uint)_headerBuffer[2] << 16));
            var sequenceId = _headerBuffer[3];
            _diagnostics.Frame("mysql", "read", frameLength, sequenceId, (byte)_sequence);

            // MySQL may renumber inner packets when compressed buffers flush; the outer stream validates ordering.
            if (_compression == null && sequenceId != (byte)_sequence)
            {
                throw new ProtocolException(
                    $"Packet sequence mismatch: expected {(byte)_sequence}, received {sequenceId}.");
            }

            _sequence = (sequenceId + 1) & 0xFF;
            isContinuation = frameLength == MaxFramePayload;
        }

        private void EnforcePacketCap(long logicalSize)
        {
            if (logicalSize > _maxPacketSize)
            {
                throw new ProtocolException(
                    $"Logical packet of {logicalSize} byte(s) exceeds the maximum of {_maxPacketSize}.");
            }
        }

        private static byte[] AppendChunk(byte[]? accumulated, byte[] chunk, ref long accumulatedLength)
        {
            if (accumulated is null)
            {
                accumulatedLength = chunk.Length;
                return chunk;
            }

            var merged = new byte[accumulatedLength + chunk.Length];
            Buffer.BlockCopy(accumulated, 0, merged, 0, (int)accumulatedLength);
            Buffer.BlockCopy(chunk, 0, merged, (int)accumulatedLength, chunk.Length);
            accumulatedLength += chunk.Length;
            return merged;
        }

        private void WriteFrame(byte[] payload, int offset, int count)
        {
            PrepareHeader(count);
            _frameWriteStarted = true;
            try
            {
                _stream.Write(_headerBuffer, 0, 4);
                if (count > 0)
                {
                    _stream.Write(payload, offset, count);
                }
                _diagnostics.Frame("mysql", "write", count, _headerBuffer[3], _headerBuffer[3]);
            }
            catch (Exception error) { _diagnostics.Error("mysql", "write", error); throw; }
        }

        private async Task WriteFrameAsync(byte[] payload, int offset, int count, CancellationToken cancellationToken)
        {
            PrepareHeader(count);
            _frameWriteStarted = true;
            try
            {
                await _stream.WriteAsync(_headerBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    await _stream.WriteAsync(payload, offset, count, cancellationToken).ConfigureAwait(false);
                }
                _diagnostics.Frame("mysql", "write", count, _headerBuffer[3], _headerBuffer[3]);
            }
            catch (Exception error) { _diagnostics.Error("mysql", "write", error); throw; }
        }

        private void PrepareHeader(int count)
        {
            _headerBuffer[0] = (byte)count;
            _headerBuffer[1] = (byte)(count >> 8);
            _headerBuffer[2] = (byte)(count >> 16);
            _headerBuffer[3] = (byte)_sequence;
            _sequence = (_sequence + 1) & 0xFF;
        }

        private void ReadExactly(byte[] buffer, int offset, int count, string part)
        {
            while (count > 0)
            {
                int read;
                try
                {
                    read = _stream.Read(buffer, offset, count);
                }
                catch (ObjectDisposedException exception)
                {
                    throw new ProtocolException($"Connection closed while reading the {part}.", exception);
                }

                if (read <= 0)
                {
                    throw new ProtocolException($"Connection closed while reading the {part}.");
                }

                offset += read;
                count -= read;
            }
        }

        private async Task ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException exception)
                {
                    throw new ProtocolException("Connection closed while reading the packet.", exception);
                }

                if (read <= 0)
                {
                    throw new ProtocolException("Connection closed while reading the packet.");
                }

                offset += read;
                count -= read;
            }
        }

        private static void ValidateRange(byte[] payload, int offset, int count)
        {
            if ((uint)offset > (uint)payload.Length || (uint)count > (uint)(payload.Length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count exceed the payload buffer.");
            }
        }
    }
}
