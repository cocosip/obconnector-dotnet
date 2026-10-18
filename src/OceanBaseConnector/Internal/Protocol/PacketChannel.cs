using System;
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
    internal sealed class PacketChannel
    {
        public const int MaxFramePayload = 0xFFFFFF;
        public const long DefaultMaxPacketSize = 64 * 1024 * 1024;

        private readonly long _maxPacketSize;
        private readonly byte[] _headerBuffer = new byte[4];

        private Stream _stream;
        private int _sequence;

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

        public void Close() => _stream.Dispose();

        /// <summary>
        /// Swaps the underlying stream after a TLS upgrade. Sequence counters
        /// are preserved: the TLS handshake is invisible to the packet
        /// framing, which continues exactly where it left off.
        /// </summary>
        public void ReplaceUnderlyingStream(Stream stream)
        {
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
            _sequence = 0;
        }

        public byte[] ReadPacket()
        {
            byte[]? accumulated = null;
            var accumulatedLength = 0L;

            while (true)
            {
                ReadExactly(_headerBuffer, 0, 4, "packet header");
                TakeHeader(out var frameLength, out var isContinuation);

                if (accumulated is null && !isContinuation)
                {
                    // Fast path: single-frame packet.
                    EnforcePacketCap(frameLength);
                    var payload = new byte[frameLength];
                    if (frameLength > 0)
                    {
                        ReadExactly(payload, 0, frameLength, "packet payload");
                    }

                    return payload;
                }

                EnforcePacketCap(accumulatedLength + frameLength);
                var chunk = new byte[frameLength];
                if (frameLength > 0)
                {
                    ReadExactly(chunk, 0, frameLength, "packet payload");
                }

                accumulated = AppendChunk(accumulated, chunk, ref accumulatedLength);
                if (!isContinuation)
                {
                    return accumulated;
                }
            }
        }

        public async Task<byte[]> ReadPacketAsync(CancellationToken cancellationToken = default)
        {
            byte[]? accumulated = null;
            var accumulatedLength = 0L;

            while (true)
            {
                await ReadExactlyAsync(_headerBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                TakeHeader(out var frameLength, out var isContinuation);

                if (accumulated is null && !isContinuation)
                {
                    EnforcePacketCap(frameLength);
                    var payload = new byte[frameLength];
                    if (frameLength > 0)
                    {
                        await ReadExactlyAsync(payload, 0, frameLength, cancellationToken).ConfigureAwait(false);
                    }

                    return payload;
                }

                EnforcePacketCap(accumulatedLength + frameLength);
                var chunk = new byte[frameLength];
                if (frameLength > 0)
                {
                    await ReadExactlyAsync(chunk, 0, frameLength, cancellationToken).ConfigureAwait(false);
                }

                accumulated = AppendChunk(accumulated, chunk, ref accumulatedLength);
                if (!isContinuation)
                {
                    return accumulated;
                }
            }
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
            ValidateRange(payload, offset, count);

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
                    return;
                }

                if (remaining == 0)
                {
                    // Exact multiple of the frame maximum: empty terminator frame.
                    WriteFrame(payload, position, 0);
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
            ValidateRange(payload, offset, count);

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
                    return;
                }

                if (remaining == 0)
                {
                    // Exact multiple of the frame maximum: empty terminator frame.
                    await WriteFrameAsync(payload, position, 0, cancellationToken).ConfigureAwait(false);
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

            if (sequenceId != (byte)_sequence)
            {
                throw new ProtocolException(
                    $"Packet sequence mismatch: expected {(byte)_sequence}, received {sequenceId}.");
            }

            _sequence = (_sequence + 1) & 0xFF;
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
            _stream.Write(_headerBuffer, 0, 4);
            if (count > 0)
            {
                _stream.Write(payload, offset, count);
            }
        }

        private async Task WriteFrameAsync(byte[] payload, int offset, int count, CancellationToken cancellationToken)
        {
            PrepareHeader(count);
            await _stream.WriteAsync(_headerBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                await _stream.WriteAsync(payload, offset, count, cancellationToken).ConfigureAwait(false);
            }
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
