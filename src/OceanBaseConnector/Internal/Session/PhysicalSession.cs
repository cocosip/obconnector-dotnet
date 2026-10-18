using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;

namespace OceanBaseConnector.Internal.Session
{
    internal enum SessionState { Closed, Connecting, Open, Busy, Broken }

    internal sealed class PhysicalSession : IDisposable
    {
        private readonly object _gate = new object();
        private readonly OceanBaseConnectionOptions _options;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private SessionState _state;
        private bool _started;
        private TcpTransport? _transport;
        private PacketChannel? _channel;
        private CommandLease? _lease;
        private ConnectResult? _info;
        private readonly HashSet<uint> _statementCloses = new HashSet<uint>();

        public PhysicalSession(OceanBaseConnectionOptions options) => _options = options;

        public SessionState State { get { lock (_gate) return _state; } }
        public ConnectResult? Info { get { lock (_gate) return _info; } }
        internal string Endpoint => _options.Endpoint;

        public void Open()
        {
            StartOpen();
            using var budget = CreateBudget(CancellationToken.None);
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var transport = TcpTransport.Connect(_options.Server, _options.Port, ConnectionTimeout, budget.Token);
                Attach(transport);
                using var registration = budget.Token.Register(CancelOpeningTransport);
                FinishOpen(HandshakeFlow.Authenticate(transport, _channel!, _options), budget.Token);
            }
            catch (Exception exception)
            {
                Break();
                throw MapOpenError(exception, budget, CancellationToken.None);
            }
        }

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            StartOpen();
            using var budget = CreateBudget(cancellationToken);
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var transport = await TcpTransport.ConnectAsync(_options.Server, _options.Port, ConnectionTimeout, budget.Token)
                    .ConfigureAwait(false);
                Attach(transport);
                // Closing the socket also interrupts netstandard2.0 TLS and reads.
                using var registration = budget.Token.Register(CancelOpeningTransport);
                var info = await HandshakeFlow.AuthenticateAsync(transport, _channel!, _options, budget.Token)
                    .ConfigureAwait(false);
                FinishOpen(info, budget.Token);
            }
            catch (Exception exception)
            {
                Break();
                throw MapOpenError(exception, budget, cancellationToken);
            }
        }

        private TimeSpan ConnectionTimeout => _options.ConnectionTimeoutSeconds == 0
            ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);

        private void CancelOpeningTransport()
        {
            lock (_gate)
            {
                if (_state == SessionState.Connecting) _transport?.Close();
            }
        }

        private CancellationTokenSource CreateBudget(CancellationToken caller)
        {
            var budget = CancellationTokenSource.CreateLinkedTokenSource(caller, _closing.Token);
            budget.CancelAfter(ConnectionTimeout);
            return budget;
        }

        private void StartOpen()
        {
            lock (_gate)
            {
                if (_started) throw new InvalidOperationException("A physical session cannot be reopened.");
                if (_options.Pooling) throw new NotSupportedException("Pooling requires a verified session reset implementation.");
                _started = true;
                _state = SessionState.Connecting;
            }
        }

        private void Attach(TcpTransport transport)
        {
            lock (_gate)
            {
                if (_state != SessionState.Connecting)
                {
                    transport.Dispose();
                    throw new OperationCanceledException(_closing.Token);
                }
                _transport = transport;
                _channel = new PacketChannel(transport.Stream);
            }
        }

        private void FinishOpen(ConnectResult info, CancellationToken token)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_state != SessionState.Connecting) throw new OperationCanceledException(_closing.Token);
                _info = info;
                _state = SessionState.Open;
            }
        }

        private Exception MapOpenError(Exception exception, CancellationTokenSource budget, CancellationToken caller)
        {
            if (caller.IsCancellationRequested) return new OperationCanceledException("Connection opening canceled.", exception, caller);
            if (_closing.IsCancellationRequested) return new OperationCanceledException("Session closed during opening.", exception, _closing.Token);
            if (budget.IsCancellationRequested || exception is TimeoutException)
                return new TimeoutException($"Connection to {_options.Endpoint} timed out.", exception);
            if (exception is OceanBaseException) return exception;
            return new OceanBaseException($"Connection to {_options.Endpoint} failed.", innerException: exception);
        }

        public CommandLease Acquire()
        {
            lock (_gate)
            {
                return TryAcquire() ?? throw new InvalidOperationException($"Session is {_state}; exclusive access requires Open.");
            }
        }

        internal CommandLease? TryAcquire()
        {
            lock (_gate)
            {
                if (_state != SessionState.Open) return null;
                _state = SessionState.Busy;
                _channel!.BeginCommand();
                return _lease = new CommandLease(this, _channel);
            }
        }

        internal void QueueStatementClose(uint id)
        { lock (_gate) { if (_state == SessionState.Open || _state == SessionState.Busy) _statementCloses.Add(id); } }

        internal uint[] TakeStatementCloses()
        {
            lock (_gate)
            {
                var ids = new uint[_statementCloses.Count];
                _statementCloses.CopyTo(ids);
                _statementCloses.Clear();
                return ids;
            }
        }

        public void Break()
        {
            lock (_gate)
            {
                if (_state == SessionState.Closed) return;
                _state = SessionState.Broken;
                ReleaseTransport();
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _started = true;
                _state = SessionState.Closed;
                _closing.Cancel();
                ReleaseTransport();
            }
        }

        // No protocol cleanup is sent: unread responses always destroy the socket.
        public Task CloseAsync() { Close(); return Task.CompletedTask; }
        public void Dispose() => Close();

        private void ReleaseTransport()
        {
            _statementCloses.Clear();
            _channel?.Close();
            _transport?.Dispose();
            _transport = null;
            _channel = null;
            _lease = null;
        }

        internal sealed class CommandLease : IDisposable
        {
            private readonly PhysicalSession _owner;
            private readonly PacketChannel _channel;
            private bool _released;

            internal CommandLease(PhysicalSession owner, PacketChannel channel) { _owner = owner; _channel = channel; }

            public PacketChannel Channel
            {
                get
                {
                    lock (_owner._gate)
                    {
                        EnsureActive();
                        return _channel;
                    }
                }
            }

            // Only the command parser may certify a fully consumed terminal response.
            public void Complete()
            {
                lock (_owner._gate)
                {
                    EnsureActive();
                    _released = true;
                    _owner._lease = null;
                    _owner._state = SessionState.Open;
                }
            }

            internal void CompleteIfActive()
            {
                lock (_owner._gate)
                {
                    // A terminal reply remains authoritative if cancellation already closed the socket.
                    if (!_released && _owner._state == SessionState.Busy && ReferenceEquals(_owner._lease, this))
                        Complete();
                }
            }

            private void EnsureActive()
            {
                if (_released || _owner._state != SessionState.Busy || !ReferenceEquals(_owner._lease, this))
                    throw new InvalidOperationException("The session lease is no longer active.");
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    if (_released) return;
                    _released = true;
                    if (ReferenceEquals(_owner._lease, this)) _owner.Break();
                }
            }
        }
    }
}
