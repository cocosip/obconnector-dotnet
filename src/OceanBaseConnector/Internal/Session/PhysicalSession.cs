using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
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
        private readonly IOceanBaseAddressResolver? _addressResolver;
        private readonly EndpointSelector _endpointSelector;
        private string _endpoint;
        private string _database;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private SessionState _state;
        private bool _started;
        private TcpTransport? _transport;
        private PacketChannel? _channel;
        private CommandLease? _lease;
        private object? _reservation;
        private ConnectResult? _info;
        private string? _sessionTimeZone;
        private NationalCharacterSet? _nationalCharacterSet;
        private readonly HashSet<uint> _statementCloses = new HashSet<uint>();
        internal bool UseOraclePrepareExecute => _options.UseOraclePrepareExecute;
        internal bool UseLobLocators => _options.UseLobLocators && Info?.Mode == OceanBaseCompatibilityMode.Oracle &&
            (Info.Capabilities.Effective & CapabilityFlags.ClientSupportLobLocator) != 0;
        internal bool? OraclePrepareExecuteSupported { get; set; }

        public PhysicalSession(OceanBaseConnectionOptions options, IOceanBaseAddressResolver? addressResolver = null, EndpointSelector? endpointSelector = null)
        {
            _options = options; _endpoint = options.Endpoint; _database = options.Database;
            _addressResolver = addressResolver; _endpointSelector = endpointSelector ?? new EndpointSelector();
        }

        public SessionState State { get { lock (_gate) return _state; } }
        public ConnectResult? Info { get { lock (_gate) return _info; } }
        internal string Database { get { lock (_gate) return _database; } }
        internal void SetDatabase(CommandLease lease, string database)
        {
            lock (_gate)
            {
                if (_state == SessionState.Busy && ReferenceEquals(_lease, lease)) _database = database;
            }
        }
        internal string? SessionTimeZone { get { lock (_gate) return _sessionTimeZone; } }
        internal NationalCharacterSet? NationalCharacterSet { get { lock (_gate) return _nationalCharacterSet; } }
        internal string Endpoint { get { lock (_gate) return _endpoint; } }

        public void Open()
        {
            StartOpen();
            using var budget = CreateBudget(CancellationToken.None);
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var endpoints = _addressResolver == null ? _options.Endpoints : ParseResolved(_addressResolver.Resolve(budget.Token));
                budget.Token.ThrowIfCancellationRequested();
                var start = _endpointSelector.SelectStart(endpoints.Count, _options.LoadBalance);
                for (var i = 0; ; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var options = SelectEndpoint(endpoints[(start + i) % endpoints.Count]);
                    TcpTransport transport;
                    try { transport = TcpTransport.Connect(options.Server, options.Port, ConnectionTimeout, budget.Token); }
                    catch (IOException error) when (CanTryNext(error, i, endpoints.Count, budget)) { continue; }
                    Attach(transport);
                    using var registration = budget.Token.Register(CancelOpeningTransport);
                    FinishOpen(HandshakeFlow.Authenticate(transport, _channel!, options), budget.Token);
                    return;
                }
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
                var endpoints = await ResolveEndpointsAsync(budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                var start = _endpointSelector.SelectStart(endpoints.Count, _options.LoadBalance);
                for (var i = 0; ; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var options = SelectEndpoint(endpoints[(start + i) % endpoints.Count]);
                    TcpTransport transport;
                    try
                    {
                        transport = await TcpTransport.ConnectAsync(options.Server, options.Port, ConnectionTimeout, budget.Token)
                            .ConfigureAwait(false);
                    }
                    catch (IOException error) when (CanTryNext(error, i, endpoints.Count, budget)) { continue; }
                    Attach(transport);
                    // Closing the socket also interrupts netstandard2.0 TLS and reads.
                    using var registration = budget.Token.Register(CancelOpeningTransport);
                    var info = await HandshakeFlow.AuthenticateAsync(transport, _channel!, options, budget.Token)
                        .ConfigureAwait(false);
                    FinishOpen(info, budget.Token);
                    return;
                }
            }
            catch (Exception exception)
            {
                Break();
                throw MapOpenError(exception, budget, cancellationToken);
            }
        }

        private TimeSpan ConnectionTimeout => _options.ConnectionTimeoutSeconds == 0
            ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);

        private static bool CanTryNext(IOException error, int index, int count, CancellationTokenSource budget) =>
            error.InnerException is SocketException && index + 1 < count && !budget.IsCancellationRequested;

        private IReadOnlyList<OceanBaseEndpoint> ParseResolved(string addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses) || addresses.Length > 65536)
                throw new ArgumentException("Resolved addresses must contain between 1 and 65536 characters.");
            var count = 1;
            foreach (var c in addresses) if (c == ',' && ++count > 256) throw new ArgumentException("Resolved address count exceeds 256.");
            return OceanBaseEndpoint.Parse(addresses, _options.Port);
        }

        private async Task<IReadOnlyList<OceanBaseEndpoint>> ResolveEndpointsAsync(CancellationToken token)
        {
            if (_addressResolver == null) return _options.Endpoints;
            var resolving = _addressResolver.ResolveAsync(token) ?? throw new InvalidOperationException("The address resolver returned no task.");
            var canceled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(resolving, canceled.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, resolving))
                {
                    // A caller-owned resolver may finish after cancellation; observe faults without using its result.
                    _ = resolving.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                return ParseResolved(await completed.ConfigureAwait(false));
            }
        }

        private OceanBaseConnectionOptions SelectEndpoint(OceanBaseEndpoint endpoint)
        {
            lock (_gate)
            {
                if (_state != SessionState.Connecting) throw new OperationCanceledException(_closing.Token);
                _endpoint = endpoint.ToString();
            }
            return _options.ForEndpoint(endpoint);
        }

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
                if (_options.UseOb20 && (info.Capabilities.Effective & CapabilityFlags.ClientConnectAttrs) != 0 &&
                    (info.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapObProtocolV2) != 0)
                    _channel!.EnableOb20(info.ConnectionId,
                        _options.EnableFullLinkTrace && (info.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapFullLinkTrace) != 0,
                        (info.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapNewExtraInfo) != 0);
                _info = info;
                _sessionTimeZone = ReportedTimeZone(info.SessionVariables);
                _nationalCharacterSet = NationalCharacterSet.FromVariables(info.SessionVariables);
                _state = SessionState.Open;
            }
        }

        private Exception MapOpenError(Exception exception, CancellationTokenSource budget, CancellationToken caller)
        {
            if (caller.IsCancellationRequested) return new OperationCanceledException("Connection opening canceled.", exception, caller);
            if (_closing.IsCancellationRequested || State == SessionState.Closed)
                return new OperationCanceledException("Session closed during opening.", exception, _closing.Token);
            if (budget.IsCancellationRequested || exception is TimeoutException)
                return new TimeoutException($"Connection to {Endpoint} timed out.", exception);
            if (exception is OceanBaseException) return exception;
            return new OceanBaseException($"Connection to {Endpoint} failed.", innerException: exception);
        }

        internal bool IsReservedForOther(object? owner) { lock (_gate) return _reservation != null && !ReferenceEquals(_reservation, owner); }
        internal void Reserve(object owner)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || _reservation != null) throw new InvalidOperationException("The session already has an active operation.");
                _reservation = owner;
            }
        }
        internal void ReleaseReservation(object owner)
        { lock (_gate) { if (ReferenceEquals(_reservation, owner)) _reservation = null; } }

        public CommandLease Acquire(object? owner = null)
        {
            lock (_gate)
            {
                return TryAcquire(owner) ?? throw new InvalidOperationException($"Session is {_state}; exclusive access requires Open and no other owner.");
            }
        }

        internal CommandLease? TryAcquire(object? owner = null)
        {
            lock (_gate)
            {
                if (_state != SessionState.Open || IsReservedForOther(owner)) return null;
                _state = SessionState.Busy;
                _channel!.BeginCommand();
                return _lease = new CommandLease(this, _channel);
            }
        }

        internal string? TakeSessionTimeZone(CommandLease lease)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                var value = _sessionTimeZone;
                _sessionTimeZone = null;
                return value;
            }
        }

        internal string? ApplySessionVariables(CommandLease lease, IReadOnlyDictionary<string, string>? variables)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                _sessionTimeZone = variables == null ? null : ReportedTimeZone(variables);
                _nationalCharacterSet = NationalCharacterSet.FromVariables(variables);
                return _sessionTimeZone;
            }
        }

        private static string? ReportedTimeZone(IReadOnlyDictionary<string, string> variables)
        {
            foreach (var variable in variables)
                if (string.Equals(variable.Key, "time_zone", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(variable.Value) ? null : variable.Value;
            return null;
        }

        internal NationalCharacterSet? TakeNationalCharacterSet(CommandLease lease)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || !ReferenceEquals(_lease, lease)) return null;
                var value = _nationalCharacterSet;
                _nationalCharacterSet = null;
                return value;
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
                ReleaseTransport();
            }
            // Resolver cancellation can invoke application code; no session lock or live transport survives it.
            try { _closing.Cancel(); }
            catch (AggregateException) { }
        }

        // No protocol cleanup is sent: unread responses always destroy the socket.
        public Task CloseAsync() { Close(); return Task.CompletedTask; }
        public void Dispose() => Close();

        private void ReleaseTransport()
        {
            _statementCloses.Clear();
            _sessionTimeZone = null;
            _nationalCharacterSet = null;
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
