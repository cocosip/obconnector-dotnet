using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        internal CommandPayload OpenResponse()
        {
            try
            {
                EnsureReading();
                var raw = OpenPayload();
                var payload = new CommandPayload(this, raw);
                payload.Start(); CountPacket();
                if (payload.FirstByte == 255) CheckPacket(payload.ReadAll(), count: false);
                return payload;
            }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task<CommandPayload> OpenResponseAsync(CancellationToken token)
        {
            using var registration = token.Register(Cancel);
            try
            {
                EnsureReading();
                var raw = await OpenPayloadAsync().ConfigureAwait(false);
                var payload = new CommandPayload(this, raw);
                await payload.StartAsync(token).ConfigureAwait(false); CountPacket();
                if (payload.FirstByte == 255) CheckPacket(await payload.ReadAllAsync(token).ConfigureAwait(false), count: false);
                return payload;
            }
            catch (Exception error) { throw Fail(ReaderCanceled(error, token)); }
        }

        private Exception ReaderCanceled(Exception error, CancellationToken token) => !_terminal && token.IsCancellationRequested
            ? new OperationCanceledException("Reader operation canceled.", error, token) : error;

        internal sealed class CommandPayload : IDisposable
        {
            private readonly CommandExchange _exchange;
            private readonly PacketChannel.PacketPayload _raw;
            private readonly byte[] _one = new byte[1];
            private bool _prefixPending = true;
            private long _consumed;
            internal CommandPayload(CommandExchange exchange, PacketChannel.PacketPayload raw) { _exchange = exchange; _raw = raw; }
            internal byte FirstByte => _one[0];
            internal int FirstFrameLength => _raw.FirstFrameLength;
            internal long RemainingLimit => _raw.MaximumLength - _consumed;

            internal void EnsureActive()
            {
                try { _exchange.EnsureReading(); }
                catch (Exception error) { throw _exchange.Fail(error); }
            }

            internal void CheckCancellation(CancellationToken token)
            {
                if (!token.IsCancellationRequested) return;
                _exchange.Cancel();
                throw _exchange.Fail(new OperationCanceledException("Reader operation canceled.", token));
            }

            internal CancellationTokenRegistration RegisterCancellation(CancellationToken token) => token.Register(_exchange.Cancel);

            internal void Start()
            {
                if (ReadRaw(_one, 0, 1) != 1) throw new ProtocolException("Empty command response packet.");
            }

            internal async Task StartAsync(CancellationToken token)
            {
                if (await ReadRawAsync(_one, 0, 1).ConfigureAwait(false) != 1)
                    throw new ProtocolException("Empty command response packet.");
            }

            internal int Read(byte[] buffer, int offset, int count)
            {
                try
                {
                    // A fully received terminal response must still reach its parser after cancellation.
                    if (!_raw.IsComplete) _exchange.EnsureReading();
                    if (count == 0) return 0;
                    if (_prefixPending) { buffer[offset] = FirstByte; _prefixPending = false; _consumed++; return 1; }
                    if (_raw.IsComplete) return 0;
                    var read = ReadRaw(buffer, offset, count); _consumed += read; return read;
                }
                catch (Exception error) { throw _exchange.Fail(error); }
            }

            internal async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                using var registration = _raw.IsComplete ? default : token.Register(_exchange.Cancel);
                try
                {
                    if (!_raw.IsComplete) { token.ThrowIfCancellationRequested(); _exchange.EnsureReading(); }
                    if (count == 0) return 0;
                    if (_prefixPending) { buffer[offset] = FirstByte; _prefixPending = false; _consumed++; return 1; }
                    if (_raw.IsComplete) return 0;
                    var read = await ReadRawAsync(buffer, offset, count).ConfigureAwait(false);
                    _consumed += read; return read;
                }
                catch (Exception error) { throw _exchange.Fail(_exchange.ReaderCanceled(error, token)); }
            }

            internal int ReadByte()
            {
                if (Read(_one, 0, 1) != 1) throw _exchange.Fail(new ProtocolException("Truncated row field."));
                return _one[0];
            }

            private int ReadRaw(byte[] buffer, int offset, int count)
            {
                var started = _exchange._io?.Start() ?? 0;
                try { return _raw.Read(buffer, offset, count); }
                finally { _exchange._io?.Stop(started, write: false); }
            }
            private Task<int> ReadRawAsync(byte[] buffer, int offset, int count) => _exchange._io == null
                ? _raw.ReadAsync(buffer, offset, count, _exchange._budget.Token) : TimedReadRawAsync(buffer, offset, count);
            private async Task<int> TimedReadRawAsync(byte[] buffer, int offset, int count)
            {
                var started = _exchange._io?.Start() ?? 0;
                try { return await _raw.ReadAsync(buffer, offset, count, _exchange._budget.Token).ConfigureAwait(false); }
                finally { _exchange._io?.Stop(started, write: false); }
            }

            internal async Task<int> ReadByteAsync(CancellationToken token)
            {
                if (await ReadAsync(_one, 0, 1, token).ConfigureAwait(false) != 1)
                    throw _exchange.Fail(new ProtocolException("Truncated row field."));
                return _one[0];
            }

            internal void ReadExactly(byte[] buffer)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = Read(buffer, offset, buffer.Length - offset);
                    if (read == 0) throw _exchange.Fail(new ProtocolException("Truncated row field."));
                    offset += read;
                }
            }

            internal async Task ReadExactlyAsync(byte[] buffer, CancellationToken token)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
                    if (read == 0) throw _exchange.Fail(new ProtocolException("Truncated row field."));
                    offset += read;
                }
            }

            internal byte[] ReadAll()
            {
                using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = Read(buffer, 0, buffer.Length)) != 0) bytes.Write(buffer, 0, count);
                return bytes.ToArray();
            }

            internal async Task<byte[]> ReadAllAsync(CancellationToken token)
            {
                using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = await ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0) bytes.Write(buffer, 0, count);
                return bytes.ToArray();
            }

            internal Exception Fault(string message) => _exchange.Fail(new ProtocolException(message));
            public void Dispose() => _raw.Dispose();
        }
    }
}
