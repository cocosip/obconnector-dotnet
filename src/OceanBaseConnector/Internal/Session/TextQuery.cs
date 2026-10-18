using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    // Owns one exchange through its terminal response, including a reader's lifetime.
    internal sealed class TextQuery : IDisposable
    {
        private readonly PhysicalSession _session;
        private readonly PhysicalSession.CommandLease _lease;
        private readonly PacketChannel _channel;
        private readonly ConnectionLog _log;
        private readonly Action _release;
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly CancellationTokenSource _budget;
        private readonly CancellationTokenRegistration _registration;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private bool _sent;
        private bool _terminal;
        private int _finished;
        private int _packets;

        internal TextQuery(PhysicalSession session, ConnectionLog log, int timeout, CancellationToken caller, Action release)
        {
            _session = session;
            _log = log;
            _release = release;
            _caller = caller;
            _budget = CancellationTokenSource.CreateLinkedTokenSource(caller, _cancel.Token);
            try
            {
                // Validate the platform timer range before acquiring the physical session.
                if (timeout != 0) _budget.CancelAfter(TimeSpan.FromSeconds(timeout));
                _lease = session.Acquire();
                _channel = _lease.Channel;
                _registration = _budget.Token.Register(() =>
                {
                    lock (_gate) { if (_sent && !_terminal) session.Break(); }
                });
            }
            catch
            {
                _lease?.Dispose();
                _budget.Dispose();
                _cancel.Dispose();
                throw;
            }
        }

        internal CapabilityFlags Capabilities => _session.Info!.Capabilities.Effective;
        internal OceanBaseCompatibilityMode Mode => _session.Info!.Mode;
        internal void EnsureReading()
        {
            if (_terminal) return;
            if (Volatile.Read(ref _finished) != 0) throw new InvalidOperationException("The command exchange has ended.");
            _budget.Token.ThrowIfCancellationRequested();
            if (_session.State != SessionState.Busy) throw new InvalidOperationException("The command session is no longer active.");
        }

        private void BeginSend()
        {
            lock (_gate) { _budget.Token.ThrowIfCancellationRequested(); _sent = true; }
        }

        internal void Send(byte[] request)
        {
            try { BeginSend(); _channel.WritePacket(request); }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task SendAsync(byte[] request)
        {
            try
            {
                BeginSend();
                await _channel.WritePacketAsync(request, _budget.Token).ConfigureAwait(false);
            }
            catch (Exception error) { throw Fail(error); }
        }

        internal byte[] Receive()
        {
            try { return CheckPacket(_channel.ReadPacket()); }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(Cancel);
            try { return CheckPacket(await _channel.ReadPacketAsync(_budget.Token).ConfigureAwait(false)); }
            catch (Exception error)
            {
                throw Fail(!_terminal && cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException("Reader operation canceled.", error, cancellationToken) : error);
            }
        }

        private byte[] CheckPacket(byte[] packet)
        {
            if (++_packets > 1_000_000) throw new ProtocolException("Command response packet limit exceeded.");
            if (packet.Length == 0) throw new ProtocolException("Empty command response packet.");
            if (ErrPacket.IsErr(packet))
            {
                var server = ErrPacket.Parse(packet);
                var error = new OceanBaseException("The server rejected the command.", server.ErrorCode, server.SqlState);
                Complete(error);
                throw error;
            }
            return packet;
        }

        internal void Complete(Exception? error = null)
        {
            lock (_gate) _terminal = true;
            _lease.CompleteIfActive();
            Finish(error);
        }

        internal Exception Fail(Exception error)
        {
            if (_terminal) return error;
            if (error is OceanBaseException databaseError && databaseError.IsExecutionOutcomeUnknown) return error;
            if (_sent) _lease.Dispose();
            else _lease.CompleteIfActive();
            Exception cause = error;
            if (_caller.IsCancellationRequested) cause = new OperationCanceledException("Command canceled.", error, _caller);
            else if (error is OperationCanceledException canceled && canceled.CancellationToken.CanBeCanceled) cause = error;
            else if (_cancel.IsCancellationRequested) cause = new OperationCanceledException("Command canceled.", error);
            else if (_budget.IsCancellationRequested) cause = new TimeoutException("Command timed out.", error);
            var mapped = _sent
                ? new OceanBaseException("The command response was not fully consumed; execution outcome is unknown.",
                    isExecutionOutcomeUnknown: true, innerException: cause)
                : cause;
            Finish(mapped);
            return mapped;
        }

        internal void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void Finish(Exception? error)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _registration.Dispose();
            _budget.Dispose();
            _cancel.Dispose();
            _release();
            _log.CommandFinished(_session.Endpoint, _session.Info!.Mode, _elapsed.ElapsedMilliseconds, error);
        }

        public void Dispose()
        {
            if (!_terminal && Volatile.Read(ref _finished) == 0)
                Fail(new InvalidOperationException("The reader was closed with an unread response."));
        }
    }
}
